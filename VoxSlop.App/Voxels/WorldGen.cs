using System.Diagnostics;
using System.Numerics;

namespace VoxSlop.App.Voxels;

public static class Materials
{
    public const byte Air = 0;
    public const byte Grass = 1;
    public const byte Dirt = 2;
    public const byte Stone = 3;
    public const byte Concrete = 4;
    public const byte Rust = 5;
}

/// <summary>
/// Builds the demo scene: an fBm heightfield plus a handful of hand-placed
/// solids. Generation is a three-pass affair — classify every brick, prefix-sum
/// the ones needing storage, then fill only those — because sampling all
/// ~2 billion voxel slots directly would take minutes.
/// </summary>
public static class WorldGen
{
    // Depth below the surface, in voxels, at which each material takes over.
    private const int GrassDepth = 2;   // 2 cm of grass
    private const int DirtDepth = 10;   // then 8 cm of dirt, stone below

    public static VoxelWorld Generate(int brickDimX, int brickDimY, int brickDimZ, int seed,
                                      bool addTerrainNoise)
    {
        var sw = Stopwatch.StartNew();
        var world = new VoxelWorld(brickDimX, brickDimY, brickDimZ);

        int[] height = BuildHeightmap(world, seed, addTerrainNoise);
        Shape[] shapes = BuildShapes(world, height, seed);

        // Pass 1 — classify each brick without touching individual voxels where possible.
        int brickCount = world.Index.Length;
        var kind = new byte[brickCount];      // 0 = empty, 1 = uniform, 2 = partial
        var uniformMat = new byte[brickCount];

        Parallel.For(0, brickDimZ, bz =>
        {
            for (int by = 0; by < brickDimY; by++)
            for (int bx = 0; bx < brickDimX; bx++)
            {
                int bi = world.BrickIndexOf(bx, by, bz);
                int y0 = by * VoxelWorld.BrickEdge;

                if (OverlapsAnyShape(shapes, bx, by, bz))
                {
                    kind[bi] = 2; // must sample properly
                    continue;
                }

                ColumnRange(height, world.VoxelDimX, bx, bz, out int minH, out int maxH);

                if (y0 >= maxH)
                    kind[bi] = 0;                       // entirely above the terrain
                else if (y0 + VoxelWorld.BrickEdge - 1 <= minH - 1 - DirtDepth)
                    (kind[bi], uniformMat[bi]) = ((byte)1, Materials.Stone);
                else
                    kind[bi] = 2;                       // straddles the surface or a material band
            }
        });

        // Pass 2 — assign pool slots. Serial, but it is a single linear scan.
        int allocated = 0;
        for (int bi = 0; bi < brickCount; bi++)
            if (kind[bi] == 2) allocated++;

        world.AllocatePool(allocated);
        var partialBricks = new int[allocated];
        int next = 0;
        for (int bi = 0; bi < brickCount; bi++)
        {
            world.Index[bi] = kind[bi] switch
            {
                0 => VoxelWorld.EntryEmpty,
                1 => VoxelWorld.UniformFlag | uniformMat[bi],
                _ => (uint)(next + 1),
            };
            if (kind[bi] == 2) partialBricks[next++] = bi;
        }

        // Pass 3 — fill only the bricks that earned storage.
        int strideY = brickDimX;
        int strideZ = brickDimX * brickDimY;
        Parallel.For(0, allocated, slot =>
        {
            int bi = partialBricks[slot];
            int bz = bi / strideZ;
            int by = (bi - bz * strideZ) / strideY;
            int bx = bi - bz * strideZ - by * strideY;

            for (int lz = 0; lz < VoxelWorld.BrickEdge; lz++)
            for (int ly = 0; ly < VoxelWorld.BrickEdge; ly++)
            for (int lx = 0; lx < VoxelWorld.BrickEdge; lx++)
            {
                byte m = SampleVoxel(
                    bx * VoxelWorld.BrickEdge + lx,
                    by * VoxelWorld.BrickEdge + ly,
                    bz * VoxelWorld.BrickEdge + lz,
                    height, world.VoxelDimX, shapes);

                if (m != Materials.Air)
                    world.WritePoolVoxel(slot, VoxelWorld.LocalVoxelIndex(lx, ly, lz), m);
            }
        });

        // Pass 4 — reclaim slots that turned out uniform after all. Classification
        // is deliberately conservative (it can only reason about the heightfield),
        // so a large share of the material-band and shape-adjacent bricks end up
        // holding a single repeated value. On a 40 m map this is worth hundreds of MB.
        int kept = CompactPool(world, partialBricks);

        long totalSlots = (long)world.VoxelDimX * world.VoxelDimY * world.VoxelDimZ;
        double indexMb = world.Index.Length * 4.0 / (1024 * 1024);
        double poolMb = world.Pool.Length * 4.0 / (1024 * 1024);
        Console.WriteLine(
            $"World {world.VoxelDimX}x{world.VoxelDimY}x{world.VoxelDimZ} voxels " +
            $"({world.VoxelDimX * VoxelWorld.VoxelSize:0.#} x {world.VoxelDimY * VoxelWorld.VoxelSize:0.#} x " +
            $"{world.VoxelDimZ * VoxelWorld.VoxelSize:0.#} m), {totalSlots / 1e9:0.00} billion voxel slots.");
        Console.WriteLine(
            $"Bricks: {kept:N0} stored of {brickCount:N0} ({kept * 100.0 / brickCount:0.00}%), " +
            $"{allocated - kept:N0} reclaimed as uniform.");
        Console.WriteLine(
            $"GPU memory: {indexMb:0.0} MB index + {poolMb:0.0} MB pool = {indexMb + poolMb:0.0} MB.");
        Console.WriteLine($"Generated in {sw.ElapsedMilliseconds} ms.");

        return world;
    }

    /// <summary>
    /// Rewrites any filled brick whose voxels are all the same value back into an
    /// inline index entry, then compacts the pool to drop the freed slots.
    /// Returns the number of bricks still holding real storage.
    /// </summary>
    private static int CompactPool(VoxelWorld world, int[] partialBricks)
    {
        int allocated = partialBricks.Length;
        var isUniform = new bool[allocated];
        var uniformValue = new byte[allocated];

        Parallel.For(0, allocated, slot =>
        {
            int baseWord = slot * VoxelWorld.UintsPerBrick;
            uint first = world.Pool[baseWord];
            // All four packed bytes must agree before the brick can be uniform.
            byte value = (byte)(first & 0xFF);
            uint expected = value * 0x0101_0101u;

            for (int w = 0; w < VoxelWorld.UintsPerBrick; w++)
                if (world.Pool[baseWord + w] != expected) return;

            isUniform[slot] = true;
            uniformValue[slot] = value;
        });

        var remapped = new int[allocated];
        int kept = 0;
        for (int slot = 0; slot < allocated; slot++)
            remapped[slot] = isUniform[slot] ? -1 : kept++;

        var compacted = new uint[(long)kept * VoxelWorld.UintsPerBrick];
        for (int slot = 0; slot < allocated; slot++)
        {
            int bi = partialBricks[slot];
            if (isUniform[slot])
            {
                world.Index[bi] = uniformValue[slot] == Materials.Air
                    ? VoxelWorld.EntryEmpty
                    : VoxelWorld.UniformFlag | uniformValue[slot];
                continue;
            }

            int dst = remapped[slot];
            world.Index[bi] = (uint)(dst + 1);
            Array.Copy(world.Pool, slot * VoxelWorld.UintsPerBrick,
                       compacted, dst * VoxelWorld.UintsPerBrick, VoxelWorld.UintsPerBrick);
        }

        world.ReplacePool(compacted);
        return kept;
    }

    /// <summary>Material at a voxel: heightfield first, then shapes stamped over it in order.</summary>
    private static byte SampleVoxel(int vx, int vy, int vz, int[] height, int dimX, Shape[] shapes)
    {
        int h = height[vx + dimX * vz];
        byte m = Materials.Air;
        if (vy < h)
        {
            int depth = h - 1 - vy;
            m = depth < GrassDepth ? Materials.Grass
              : depth < DirtDepth ? Materials.Dirt
              : Materials.Stone;
        }

        foreach (var s in shapes)
            if (s.Contains(vx, vy, vz))
                m = s.Subtractive ? Materials.Air : s.Material;

        return m;
    }

    private static void ColumnRange(int[] height, int dimX, int bx, int bz, out int minH, out int maxH)
    {
        minH = int.MaxValue;
        maxH = int.MinValue;
        int x0 = bx * VoxelWorld.BrickEdge, z0 = bz * VoxelWorld.BrickEdge;
        for (int z = z0; z < z0 + VoxelWorld.BrickEdge; z++)
        for (int x = x0; x < x0 + VoxelWorld.BrickEdge; x++)
        {
            int h = height[x + dimX * z];
            if (h < minH) minH = h;
            if (h > maxH) maxH = h;
        }
    }

    private static bool OverlapsAnyShape(Shape[] shapes, int bx, int by, int bz)
    {
        int x0 = bx * VoxelWorld.BrickEdge, y0 = by * VoxelWorld.BrickEdge, z0 = bz * VoxelWorld.BrickEdge;
        int x1 = x0 + VoxelWorld.BrickEdge - 1, y1 = y0 + VoxelWorld.BrickEdge - 1, z1 = z0 + VoxelWorld.BrickEdge - 1;
        foreach (var s in shapes)
            if (s.IntersectsBox(x0, y0, z0, x1, y1, z1)) return true;
        return false;
    }

    private static int[] BuildHeightmap(VoxelWorld world, int seed, bool addTerrainNoise)
    {
        int dimX = world.VoxelDimX, dimZ = world.VoxelDimZ;
        var height = new int[dimX * dimZ];

        // Gently rolling ground: terrain spans roughly 0.8 m to 2.4 m inside the
        // 5.12 m tall world. Large feature size + few octaves keeps the relief
        // broad and smooth rather than choppy.
        const float BaseHeight = 80f;    // voxels
        const float Amplitude = 160f;    // voxels
        const float FeatureSize = 1400f; // voxels per noise cell at the base octave

        // Short-wavelength wobble added before rounding. It barely changes the
        // overall shape but shifts where each integer step lands, so the contour
        // lines between height levels turn ragged instead of clean and artificial.
        const float EdgeSize = 22f;      // voxels per noise cell (~1.1 m wobble)
        const float EdgeAmplitude = 3.0f; // voxels the step boundary can shift by

        // Only this fraction of columns get a tuft at all; the rest stay flat.
        const uint TuftChance = (uint)(0.18f * uint.MaxValue);

        Parallel.For(0, dimZ, z =>
        {
            for (int x = 0; x < dimX; x++)
            {
                // Broad base shape.
                float n = Noise.Fbm(x / FeatureSize, z / FeatureSize, seed, octaves: 5);
                float hf = BaseHeight + n * Amplitude;

                // Break up the clean step boundaries with a little high-frequency noise.
                float edge = Noise.Fbm(x / EdgeSize, z / EdgeSize, seed + 4242, octaves: 3) - 0.5f;
                hf += edge * EdgeAmplitude;

                int h = (int)MathF.Round(hf);

                // Sparse tufts: most columns are flat, a few rise by 1 or 2 voxels.
                uint hash = ColumnHash(x, z, seed);
                if (addTerrainNoise && hash < TuftChance) h += 1 + (int)((hash >> 8) & 1u);

                height[x + dimX * z] = h;
            }
        });

        return height;
    }

    /// <summary>Deterministic per-column hash used for the rough-grass scatter.</summary>
    private static uint ColumnHash(int x, int z, int seed)
    {
        uint h = (uint)(x * 374761393 + z * 668265263 + seed * 2147483647);
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }

    private static Shape[] BuildShapes(VoxelWorld world, int[] height, int seed)
    {
        var rng = new Random(seed);
        var shapes = new List<Shape>();
        int dimX = world.VoxelDimX, dimZ = world.VoxelDimZ;
        int V(float metres) => (int)MathF.Round(metres / VoxelWorld.VoxelSize);

        int GroundAt(int vx, int vz) => height[Math.Clamp(vx, 0, dimX - 1) + dimX * Math.Clamp(vz, 0, dimZ - 1)];

        // Scattered pillars — vertical parallax references, 40 cm square.
        for (int i = 0; i < 28; i++)
        {
            int cx = rng.Next(V(2f), dimX - V(2f));
            int cz = rng.Next(V(2f), dimZ - V(2f));
            int half = V(0.2f);
            int baseY = GroundAt(cx, cz) - V(0.3f);
            int top = baseY + rng.Next(V(1.2f), V(2.4f));
            shapes.Add(Shape.Box(cx - half, baseY, cz - half, cx + half, top, cz + half, Materials.Concrete));
        }

        // A wall with a doorway punched through it, to show off sharp voxel edges.
        {
            int cx = dimX / 2 + V(3.5f);
            int cz = dimZ / 2;
            int baseY = GroundAt(cx, cz) - V(0.3f);
            int halfLen = V(3f), thick = V(0.15f), tall = V(3f);
            shapes.Add(Shape.Box(cx - thick, baseY, cz - halfLen, cx + thick, baseY + tall, cz + halfLen, Materials.Concrete));
            // Opening reaches ~2.2 m above ground so the 1.8 m player clears it, with a lintel left above.
            shapes.Add(Shape.Box(cx - thick - 1, baseY, cz - V(0.5f), cx + thick + 1, baseY + V(2.5f), cz + V(0.5f), Materials.Air, subtractive: true));
        }

        // The cube, beam and sphere are now live spinning shapes (see Game /
        // DynamicShape), rendered each frame rather than baked here.

        return [.. shapes];
    }
}

/// <summary>
/// A solid stamped over the terrain during generation. Coordinates are in voxel
/// units. Boxes may be oriented (rotated) about their centre; spheres are
/// rotation-invariant. All shapes carry a conservative world-space AABB so brick
/// classification can reject non-overlapping bricks without a full inside test.
/// </summary>
public readonly struct Shape
{
    private readonly bool _isSphere;
    private readonly Vector3 _center;
    private readonly Vector3 _axisX, _axisY, _axisZ; // orthonormal box axes in world space
    private readonly Vector3 _half;                  // box half extents
    private readonly float _radiusSq;                // sphere
    private readonly int _minX, _minY, _minZ, _maxX, _maxY, _maxZ; // conservative AABB

    public byte Material { get; }
    public bool Subtractive { get; }

    private Shape(bool isSphere, Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ,
                  Vector3 half, float radiusSq, Vector3 aabbHalf, byte material, bool subtractive)
    {
        _isSphere = isSphere;
        _center = center;
        _axisX = axisX; _axisY = axisY; _axisZ = axisZ;
        _half = half; _radiusSq = radiusSq;

        _minX = (int)MathF.Floor(center.X - aabbHalf.X);
        _minY = (int)MathF.Floor(center.Y - aabbHalf.Y);
        _minZ = (int)MathF.Floor(center.Z - aabbHalf.Z);
        _maxX = (int)MathF.Ceiling(center.X + aabbHalf.X);
        _maxY = (int)MathF.Ceiling(center.Y + aabbHalf.Y);
        _maxZ = (int)MathF.Ceiling(center.Z + aabbHalf.Z);

        Material = material;
        Subtractive = subtractive;
    }

    /// <summary>Axis-aligned box from inclusive voxel bounds (back-compat convenience).</summary>
    public static Shape Box(int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
                            byte material, bool subtractive = false)
    {
        var center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
        var half = new Vector3((maxX - minX) * 0.5f, (maxY - minY) * 0.5f, (maxZ - minZ) * 0.5f);
        return OrientedBox(center, half, Vector3.Zero, material, subtractive);
    }

    /// <summary>
    /// Box centred at <paramref name="center"/> (voxels) with the given half extents,
    /// rotated by Euler angles (radians, applied X then Y then Z). Pass
    /// <see cref="Vector3.Zero"/> for an axis-aligned box.
    /// </summary>
    public static Shape OrientedBox(Vector3 center, Vector3 half, Vector3 eulerRadians,
                                    byte material, bool subtractive = false)
    {
        Matrix4x4 rot = Matrix4x4.CreateRotationX(eulerRadians.X)
                      * Matrix4x4.CreateRotationY(eulerRadians.Y)
                      * Matrix4x4.CreateRotationZ(eulerRadians.Z);

        var ax = Vector3.TransformNormal(Vector3.UnitX, rot);
        var ay = Vector3.TransformNormal(Vector3.UnitY, rot);
        var az = Vector3.TransformNormal(Vector3.UnitZ, rot);

        // Extent of the oriented box projected onto each world axis.
        var aabbHalf = new Vector3(
            half.X * MathF.Abs(ax.X) + half.Y * MathF.Abs(ay.X) + half.Z * MathF.Abs(az.X),
            half.X * MathF.Abs(ax.Y) + half.Y * MathF.Abs(ay.Y) + half.Z * MathF.Abs(az.Y),
            half.X * MathF.Abs(ax.Z) + half.Y * MathF.Abs(ay.Z) + half.Z * MathF.Abs(az.Z));

        return new Shape(false, center, ax, ay, az, half, 0f, aabbHalf, material, subtractive);
    }

    /// <summary>A rotated cube: an oriented box with equal half extents on every axis.</summary>
    public static Shape Cube(Vector3 center, float halfSize, Vector3 eulerRadians,
                             byte material, bool subtractive = false) =>
        OrientedBox(center, new Vector3(halfSize), eulerRadians, material, subtractive);

    public static Shape Sphere(int cx, int cy, int cz, int radius, byte material, bool subtractive = false)
    {
        var center = new Vector3(cx, cy, cz);
        var aabbHalf = new Vector3(radius);
        return new Shape(true, center, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
                         Vector3.Zero, radius * (float)radius, aabbHalf, material, subtractive);
    }

    public bool Contains(int x, int y, int z)
    {
        if (x < _minX || x > _maxX || y < _minY || y > _maxY || z < _minZ || z > _maxZ) return false;

        var d = new Vector3(x, y, z) - _center;
        if (_isSphere) return d.LengthSquared() <= _radiusSq;

        return MathF.Abs(Vector3.Dot(d, _axisX)) <= _half.X
            && MathF.Abs(Vector3.Dot(d, _axisY)) <= _half.Y
            && MathF.Abs(Vector3.Dot(d, _axisZ)) <= _half.Z;
    }

    /// <summary>Conservative AABB test — used to decide whether a brick needs full sampling.</summary>
    public bool IntersectsBox(int x0, int y0, int z0, int x1, int y1, int z1) =>
        x1 >= _minX && x0 <= _maxX && y1 >= _minY && y0 <= _maxY && z1 >= _minZ && z0 <= _maxZ;
}

/// <summary>Hash-based value noise. No dependencies, deterministic for a given seed.</summary>
internal static class Noise
{
    private static float Hash(int x, int z, int seed)
    {
        uint h = (uint)(x * 374761393 + z * 668265263 + seed * 1442695041);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }

    private static float ValueNoise(float x, float z, int seed)
    {
        int xi = (int)MathF.Floor(x), zi = (int)MathF.Floor(z);
        float fx = x - xi, fz = z - zi;
        // Smoothstep the interpolants so octaves don't show grid creases.
        fx = fx * fx * (3f - 2f * fx);
        fz = fz * fz * (3f - 2f * fz);

        float a = Hash(xi, zi, seed), b = Hash(xi + 1, zi, seed);
        float c = Hash(xi, zi + 1, seed), d = Hash(xi + 1, zi + 1, seed);
        return float.Lerp(float.Lerp(a, b, fx), float.Lerp(c, d, fx), fz);
    }

    /// <summary>Fractal sum of value noise, returned in roughly [0, 1].</summary>
    public static float Fbm(float x, float z, int seed, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f, freq = 1f;
        for (int i = 0; i < octaves; i++)
        {
            sum += ValueNoise(x * freq, z * freq, seed + i * 7919) * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
        }
        return sum / norm;
    }
}
