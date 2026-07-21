using System.Numerics;

namespace VoxSlop.App.Voxels;

/// <summary>
/// Sparse voxel storage in "brickmap" form: a dense index grid over fixed-size
/// bricks, where only bricks that actually straddle a surface get a payload in
/// the pool. Fully-empty and fully-uniform bricks are encoded in the index entry
/// itself, which is what keeps a billion-voxel world inside a few tens of MB.
///
/// Both arrays are uploaded verbatim as SSBOs and traversed by the raymarch
/// shader, so the layout here is the GPU layout — keep it in sync with
/// Render/Shaders/raymarch.frag.
/// </summary>
public sealed class VoxelWorld
{
    /// <summary>Voxels along one edge of a brick.</summary>
    public const int BrickEdge = 8;

    public const int VoxelsPerBrick = BrickEdge * BrickEdge * BrickEdge; // 512
    /// <summary>Voxel materials are one byte, packed 4-per-uint for SSBO access.</summary>
    public const int UintsPerBrick = VoxelsPerBrick / 4;                 // 128

    /// <summary>Mip: each brick is also downsampled to a 4^3 grid (each cell = 2^3 voxels)
    /// for distance LOD. Derived from the pool, not stored in the cache.</summary>
    public const int MipEdge = 4;
    public const int MipCellsPerBrick = MipEdge * MipEdge * MipEdge;     // 64
    public const int MipUintsPerBrick = MipCellsPerBrick / 4;            // 16

    /// <summary>Edge length of a single voxel, in metres.</summary>
    public const float VoxelSize = 0.02f;

    /// <summary>Index entry meaning "nothing here" — no pool storage.</summary>
    public const uint EntryEmpty = 0u;

    /// <summary>
    /// Index entry bit meaning "every voxel in this brick is the material in the
    /// low byte" — also no pool storage. Any other non-zero entry is a pool
    /// slot index, biased by one so that zero stays free for <see cref="EntryEmpty"/>.
    /// </summary>
    public const uint UniformFlag = 0x8000_0000u;

    public int BrickDimX { get; }
    public int BrickDimY { get; }
    public int BrickDimZ { get; }

    public int VoxelDimX => BrickDimX * BrickEdge;
    public int VoxelDimY => BrickDimY * BrickEdge;
    public int VoxelDimZ => BrickDimZ * BrickEdge;

    /// <summary>One entry per brick, indexed x + dimX * (y + dimY * z).</summary>
    public uint[] Index { get; }

    /// <summary>Packed voxel payloads for partial bricks, <see cref="UintsPerBrick"/> uints each.</summary>
    public uint[] Pool { get; private set; } = [];

    /// <summary>Downsampled 4^3 payload per stored brick (<see cref="MipUintsPerBrick"/> uints),
    /// same slot indexing as <see cref="Pool"/>. Built by <see cref="BuildMips"/>.</summary>
    public uint[] MipPool { get; private set; } = [];

    /// <summary>Number of bricks that needed real storage.</summary>
    public int AllocatedBricks => Pool.Length / UintsPerBrick;

    public VoxelWorld(int brickDimX, int brickDimY, int brickDimZ)
    {
        BrickDimX = brickDimX;
        BrickDimY = brickDimY;
        BrickDimZ = brickDimZ;
        Index = new uint[(long)brickDimX * brickDimY * brickDimZ is var n && n <= int.MaxValue
            ? (int)n
            : throw new ArgumentException("Brick grid too large for a single array.")];
    }

    internal void AllocatePool(int brickCount) => Pool = new uint[(long)brickCount * UintsPerBrick];

    internal void ReplacePool(uint[] pool) => Pool = pool;

    public int BrickIndexOf(int bx, int by, int bz) => bx + BrickDimX * (by + BrickDimY * bz);

    /// <summary>Writes a voxel into an already-allocated pool slot.</summary>
    internal void WritePoolVoxel(int poolSlot, int localIndex, byte material)
    {
        int word = poolSlot * UintsPerBrick + (localIndex >> 2);
        int shift = (localIndex & 3) * 8;
        Pool[word] = (Pool[word] & ~(0xFFu << shift)) | ((uint)material << shift);
    }

    public static int LocalVoxelIndex(int lx, int ly, int lz) =>
        lx + BrickEdge * (ly + BrickEdge * lz);

    /// <summary>
    /// Builds the mip pool by downsampling every stored brick's 8^3 payload to a
    /// 4^3 grid. A mip cell (2^3 voxels) takes a representative material if at least
    /// two of its eight voxels are solid, else air. Derived from <see cref="Pool"/>,
    /// so it is recomputed after both generation and cache load rather than stored.
    /// </summary>
    public void BuildMips()
    {
        int slots = AllocatedBricks;
        MipPool = new uint[(long)slots * MipUintsPerBrick];

        System.Threading.Tasks.Parallel.For(0, slots, slot =>
        {
            int fullBase = slot * UintsPerBrick;
            int mipBase = slot * MipUintsPerBrick;

            for (int mz = 0; mz < MipEdge; mz++)
            for (int my = 0; my < MipEdge; my++)
            for (int mx = 0; mx < MipEdge; mx++)
            {
                int solid = 0, bestLy = -1;
                byte mat = 0;
                for (int dz = 0; dz < 2; dz++)
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int li = LocalVoxelIndex(mx * 2 + dx, my * 2 + dy, mz * 2 + dz);
                    byte v = (byte)((Pool[fullBase + (li >> 2)] >> ((li & 3) * 8)) & 0xFF);
                    if (v == 0) continue;
                    solid++;
                    int ly = my * 2 + dy;           // take the topmost solid -> the visible surface
                    if (ly > bestLy) { bestLy = ly; mat = v; }
                }

                byte cell = solid >= 2 ? mat : (byte)0;
                int idx = mx + MipEdge * (my + MipEdge * mz);
                int word = mipBase + (idx >> 2);
                int shift = (idx & 3) * 8;
                MipPool[word] = (MipPool[word] & ~(0xFFu << shift)) | ((uint)cell << shift);
            }
        });
    }

    /// <summary>Material at a voxel coordinate; 0 (air) outside the world.</summary>
    public byte GetVoxel(int vx, int vy, int vz)
    {
        if ((uint)vx >= (uint)VoxelDimX || (uint)vy >= (uint)VoxelDimY || (uint)vz >= (uint)VoxelDimZ)
            return 0;

        uint entry = Index[BrickIndexOf(vx >> 3, vy >> 3, vz >> 3)];
        if (entry == EntryEmpty) return 0;
        if ((entry & UniformFlag) != 0) return (byte)(entry & 0xFF);

        int slot = (int)(entry - 1);
        int li = LocalVoxelIndex(vx & 7, vy & 7, vz & 7);
        return (byte)((Pool[slot * UintsPerBrick + (li >> 2)] >> ((li & 3) * 8)) & 0xFF);
    }

    /// <summary>
    /// Does a world-space AABB (metres) overlap any solid voxel? Used for player
    /// collision. Rejects at brick granularity first, so a 0.6 x 1.8 x 0.6 m box —
    /// 648k voxels if tested naively — usually costs a few hundred array reads.
    /// </summary>
    public bool OverlapsSolid(Vector3 min, Vector3 max)
    {
        int vx0 = (int)MathF.Floor(min.X / VoxelSize), vx1 = (int)MathF.Floor(max.X / VoxelSize);
        int vy0 = (int)MathF.Floor(min.Y / VoxelSize), vy1 = (int)MathF.Floor(max.Y / VoxelSize);
        int vz0 = (int)MathF.Floor(min.Z / VoxelSize), vz1 = (int)MathF.Floor(max.Z / VoxelSize);

        vx0 = Math.Max(vx0, 0); vx1 = Math.Min(vx1, VoxelDimX - 1);
        vy0 = Math.Max(vy0, 0); vy1 = Math.Min(vy1, VoxelDimY - 1);
        vz0 = Math.Max(vz0, 0); vz1 = Math.Min(vz1, VoxelDimZ - 1);
        if (vx0 > vx1 || vy0 > vy1 || vz0 > vz1) return false;

        for (int bz = vz0 >> 3; bz <= vz1 >> 3; bz++)
        for (int by = vy0 >> 3; by <= vy1 >> 3; by++)
        for (int bx = vx0 >> 3; bx <= vx1 >> 3; bx++)
        {
            uint entry = Index[BrickIndexOf(bx, by, bz)];
            if (entry == EntryEmpty) continue;
            if ((entry & UniformFlag) != 0) return true;

            // Partial brick: only scan the part of it the AABB actually touches.
            int slot = (int)(entry - 1);
            int lz0 = Math.Max(vz0 - (bz << 3), 0), lz1 = Math.Min(vz1 - (bz << 3), 7);
            int ly0 = Math.Max(vy0 - (by << 3), 0), ly1 = Math.Min(vy1 - (by << 3), 7);
            int lx0 = Math.Max(vx0 - (bx << 3), 0), lx1 = Math.Min(vx1 - (bx << 3), 7);

            for (int lz = lz0; lz <= lz1; lz++)
            for (int ly = ly0; ly <= ly1; ly++)
            for (int lx = lx0; lx <= lx1; lx++)
            {
                int li = LocalVoxelIndex(lx, ly, lz);
                if (((Pool[slot * UintsPerBrick + (li >> 2)] >> ((li & 3) * 8)) & 0xFF) != 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>Highest solid voxel in a column, or -1 if the column is empty.</summary>
    public int SurfaceHeight(int vx, int vz)
    {
        for (int vy = VoxelDimY - 1; vy >= 0; vy--)
            if (GetVoxel(vx, vy, vz) != 0) return vy;
        return -1;
    }
}
