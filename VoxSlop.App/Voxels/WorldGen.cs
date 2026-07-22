using System.Diagnostics;
using System.Threading;

namespace VoxSlop.App.Voxels;

/// <summary>
/// Builds the demo scene: an fBm heightfield plus a handful of hand-placed
/// solids. Generation reasons at brick granularity because sampling billions of
/// individual voxel slots would take minutes — see the pass methods called from
/// <see cref="Generate"/>: ClassifyBricks, AssignPoolSlots, FillPartialBricks,
/// CompactPool.
/// </summary>
public static class WorldGen
{
    // Depth below the surface, in voxels, at which each material takes over:
    // grass down to GrassDepth, dirt down to DirtDepth, stone below that.
    private const int GrassDepth = 2;
    private const int DirtDepth = 10;

    // Brick classification results, used between the classify and slot-assign passes.
    private const byte BrickEmpty = 0;
    private const byte BrickUniform = 1;
    private const byte BrickPartial = 2;

    public static VoxelWorld Generate(int brickDimX, int brickDimY, int brickDimZ, int seed,
                                      bool addTerrainNoise)
    {
        var sw = Stopwatch.StartNew();
        var world = new VoxelWorld(brickDimX, brickDimY, brickDimZ);
        Console.WriteLine("Generating world...");

        int[] height = BuildHeightmap(world, seed, addTerrainNoise, out byte[] tuft);
        Shape[] shapes = BuildShapes(world, height, seed);

        ClassifyBricks(world, height, shapes, out byte[] kind, out byte[] uniformMat);
        int[] partialBricks = AssignPoolSlots(world, kind, uniformMat);
        FillPartialBricks(world, partialBricks, height, tuft, shapes);
        int kept = CompactPool(world, partialBricks);
        world.BuildMips(); // LOD payloads, derived from the compacted pool

        PrintSummary(world, partialBricks.Length, kept, sw);
        return world;
    }

    /// <summary>
    /// Pass 1 — classify each brick as empty / uniform / partial from the heightfield
    /// and shape AABBs alone, without touching individual voxels. Classification is
    /// deliberately conservative: anything it cannot prove empty or uniform is marked
    /// partial and resolved properly by the fill + compact passes.
    /// </summary>
    private static void ClassifyBricks(VoxelWorld world, int[] height, Shape[] shapes,
                                       out byte[] kind, out byte[] uniformMat)
    {
        int brickCount = world.Index.Length;
        var kindLocal = new byte[brickCount];
        var uniformLocal = new byte[brickCount];

        int classified = 0, progressStep = Math.Max(1, world.BrickDimZ / 100);
        Parallel.For(0, world.BrickDimZ, bz =>
        {
            for (int by = 0; by < world.BrickDimY; by++)
            for (int bx = 0; bx < world.BrickDimX; bx++)
            {
                int bi = world.BrickIndexOf(bx, by, bz);
                int y0 = by * VoxelWorld.BrickEdge;

                if (OverlapsAnyShape(shapes, bx, by, bz))
                {
                    kindLocal[bi] = BrickPartial; // must sample properly
                    continue;
                }

                ColumnRange(height, world.VoxelDimX, bx, bz, out int minH, out int maxH);

                if (y0 >= maxH)
                    kindLocal[bi] = BrickEmpty;   // entirely above the terrain
                else if (y0 + VoxelWorld.BrickEdge - 1 <= minH - 1 - DirtDepth)
                    (kindLocal[bi], uniformLocal[bi]) = (BrickUniform, Materials.Stone);
                else
                    kindLocal[bi] = BrickPartial; // straddles the surface or a material band
            }

            int d = Interlocked.Increment(ref classified);
            if (d % progressStep == 0 || d == world.BrickDimZ)
                PrintProgress("Classifying bricks", d, world.BrickDimZ);
        });
        FinishProgress();

        kind = kindLocal;
        uniformMat = uniformLocal;
    }

    /// <summary>
    /// Pass 2 — write the index entry for every brick and hand each partial brick a
    /// pool slot. Serial, but it is a single linear scan. Returns the partial bricks
    /// in slot order (slot i holds brick partialBricks[i]).
    /// </summary>
    private static int[] AssignPoolSlots(VoxelWorld world, byte[] kind, byte[] uniformMat)
    {
        int allocated = 0;
        for (int bi = 0; bi < kind.Length; bi++)
            if (kind[bi] == BrickPartial) allocated++;

        world.AllocatePool(allocated);
        var partialBricks = new int[allocated];
        int next = 0;
        for (int bi = 0; bi < kind.Length; bi++)
        {
            world.Index[bi] = kind[bi] switch
            {
                BrickEmpty => VoxelWorld.EntryEmpty,
                BrickUniform => VoxelWorld.UniformFlag | uniformMat[bi],
                _ => (uint)(next + 1),
            };
            if (kind[bi] == BrickPartial) partialBricks[next++] = bi;
        }

        return partialBricks;
    }

    /// <summary>
    /// Pass 3 — sample and store voxels for the partial bricks only; every other
    /// brick is already fully described by its index entry.
    /// </summary>
    private static void FillPartialBricks(VoxelWorld world, int[] partialBricks,
                                          int[] height, byte[] tuft, Shape[] shapes)
    {
        int strideY = world.BrickDimX;
        int strideZ = world.BrickDimX * world.BrickDimY;
        int allocated = partialBricks.Length;

        int filled = 0, progressStep = Math.Max(1, allocated / 100);
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
                    height, tuft, world.VoxelDimX, shapes);

                if (m != Materials.Air)
                    world.WritePoolVoxel(slot, VoxelWorld.LocalVoxelIndex(lx, ly, lz), m);
            }

            int d = Interlocked.Increment(ref filled);
            if (d % progressStep == 0 || d == allocated) PrintProgress("Filling bricks", d, allocated);
        });
        FinishProgress();
    }

    private static void PrintSummary(VoxelWorld world, int allocated, int kept, Stopwatch sw)
    {
        long totalSlots = (long)world.VoxelDimX * world.VoxelDimY * world.VoxelDimZ;
        int brickCount = world.Index.Length;
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
    }

    /// <summary>
    /// Pass 4 — rewrites any filled brick whose voxels are all the same value back
    /// into an inline index entry, then compacts the pool to drop the freed slots.
    /// Because classification is conservative, a large share of the material-band
    /// and shape-adjacent bricks end up uniform, so this reclaims a significant
    /// fraction of the pool. Returns the number of bricks still holding real storage.
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
    private static byte SampleVoxel(int vx, int vy, int vz, int[] height, byte[] tuft, int dimX, Shape[] shapes)
    {
        int col = vx + dimX * vz;
        int h = height[col];
        byte m = Materials.Air;
        if (vy < h)
        {
            int depth = h - 1 - vy;
            int t = tuft[col];
            if (depth < t)
                m = Materials.GrassTuft;                 // the raised tuft voxels
            else
            {
                int baseDepth = depth - t;               // depth below the base (untufted) surface
                m = baseDepth < GrassDepth ? Materials.Grass
                  : baseDepth < DirtDepth ? Materials.Dirt
                  : Materials.Stone;
            }
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

    private static int[] BuildHeightmap(VoxelWorld world, int seed, bool addTerrainNoise, out byte[] tuft)
    {
        int dimX = world.VoxelDimX, dimZ = world.VoxelDimZ;
        var height = new int[dimX * dimZ];
        var tuftLocal = new byte[dimX * dimZ];

        // Gently rolling ground: the surface sits around BaseHeight voxels and
        // swings by up to Amplitude voxels. Large feature size + few octaves keeps
        // the relief broad and smooth rather than choppy.
        const float BaseHeight = 80f;    // voxels
        const float Amplitude = 160f;    // voxels
        const float FeatureSize = 1400f; // voxels per noise cell at the base octave

        // Short-wavelength wobble added before rounding. It barely changes the
        // overall shape but shifts where each integer step lands, so the contour
        // lines between height levels turn ragged instead of clean and artificial.
        const float EdgeSize = 22f;       // voxels per noise cell
        const float EdgeAmplitude = 3.0f; // voxels the step boundary can shift by

        // Only this fraction of columns get a tuft at all; the rest stay flat.
        const uint TuftChance = (uint)(0.5f * uint.MaxValue);

        int done = 0, step = Math.Max(1, dimZ / 100);
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

                // Sparse tufts: most columns are flat, a few rise by few voxels.
                uint hash = ColumnHash(x, z, seed);
                int bump = (addTerrainNoise && hash < TuftChance) ? 1 + (int)((hash >> 8) & 1u) : 0;
                h += bump;

                height[x + dimX * z] = h;
                tuftLocal[x + dimX * z] = (byte)bump;
            }

            int d = Interlocked.Increment(ref done);
            if (d % step == 0 || d == dimZ) PrintProgress("Heightmap", d, dimZ);
        });
        FinishProgress();

        tuft = tuftLocal;
        return height;
    }

    /// <summary>Overwrites one console line with a phase percentage. Safe to call from threads.</summary>
    private static void PrintProgress(string phase, int done, int total) =>
        Console.Write($"\r  {phase,-18} {100.0 * done / total,3:0}%");

    private static void FinishProgress() => Console.WriteLine();

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
