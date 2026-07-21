using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoxSlop.App.Voxels;

/// <summary>
/// Persists a generated <see cref="VoxelWorld"/> to a flat binary file and loads
/// it back, skipping the multi-second generation pass on subsequent runs.
///
/// The file is a cache, not a save format: its header records every input that
/// affects the geometry (grid dimensions, seed, voxel size, brick edge, layout
/// version). If any of them differs from what the caller now wants, the cache is
/// treated as stale and the world is regenerated — so changing a constant in code
/// can never silently load a mismatched world.
/// </summary>
public static class WorldStore
{
    // "VOXSLOP1" — bump the trailing digit whenever the on-disk layout changes.
    private const ulong Magic = 0x31_50_4F_4C_53_58_4F_56;

    /// <summary>
    /// Bump when the *meaning* of the stored bytes changes in a way the header
    /// fields don't already capture (e.g. a new pool encoding) OR when the world
    /// generation itself changes (new/edited shapes), since the cache does not hash
    /// the generation code. Forces a rebuild.
    /// </summary>
    private const int LayoutVersion = 5;

    /// <summary>Loads a matching cached world, or generates a fresh one and caches it.</summary>
    public static VoxelWorld LoadOrGenerate(int brickDimX, int brickDimY, int brickDimZ, int seed, string path)
    {
        if (TryLoad(brickDimX, brickDimY, brickDimZ, seed, path, out VoxelWorld? world))
            return world!;

        world = WorldGen.Generate(brickDimX, brickDimY, brickDimZ, seed);

        try
        {
            Save(world, seed, path);
        }
        catch (Exception ex)
        {
            // A failed cache write must never take the whole run down with it.
            Console.WriteLine($"Could not write world cache '{path}': {ex.Message}");
        }

        return world;
    }

    public static void Save(VoxelWorld world, int seed, string path)
    {
        var sw = Stopwatch.StartNew();

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write to a temp file and move into place, so an interrupted save can't
        // leave a truncated cache that later loads as garbage.
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(Magic);
            bw.Write(LayoutVersion);
            bw.Write(VoxelWorld.BrickEdge);
            bw.Write(VoxelWorld.VoxelSize);
            bw.Write(seed);
            bw.Write(world.BrickDimX);
            bw.Write(world.BrickDimY);
            bw.Write(world.BrickDimZ);
            bw.Write((long)world.Index.Length);
            bw.Write((long)world.Pool.Length);
            bw.Flush();

            fs.Write(MemoryMarshal.AsBytes<uint>(world.Index));
            fs.Write(MemoryMarshal.AsBytes<uint>(world.Pool));
        }

        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);

        double mb = new FileInfo(path).Length / (1024.0 * 1024.0);
        Console.WriteLine($"Saved world cache: {mb:0.0} MB to '{path}' in {sw.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// Attempts to load a cached world matching the requested parameters.
    /// Returns false (and regenerates nothing) if the file is missing, unreadable,
    /// or describes a different world.
    /// </summary>
    public static bool TryLoad(int brickDimX, int brickDimY, int brickDimZ, int seed, string path, out VoxelWorld? world)
    {
        world = null;
        if (!File.Exists(path)) return false;

        var sw = Stopwatch.StartNew();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var br = new BinaryReader(fs);

            if (br.ReadUInt64() != Magic) return Reject(path, "not a VoxSlop world cache");
            if (br.ReadInt32() != LayoutVersion) return Reject(path, "layout version changed");
            if (br.ReadInt32() != VoxelWorld.BrickEdge) return Reject(path, "brick edge changed");
            if (br.ReadSingle() != VoxelWorld.VoxelSize) return Reject(path, "voxel size changed");
            if (br.ReadInt32() != seed) return Reject(path, "seed changed");
            if (br.ReadInt32() != brickDimX || br.ReadInt32() != brickDimY || br.ReadInt32() != brickDimZ)
                return Reject(path, "grid dimensions changed");

            long indexLen = br.ReadInt64();
            long poolLen = br.ReadInt64();

            var loaded = new VoxelWorld(brickDimX, brickDimY, brickDimZ);
            if (loaded.Index.Length != indexLen) return Reject(path, "index length mismatch");
            if (poolLen < 0 || poolLen % VoxelWorld.UintsPerBrick != 0) return Reject(path, "corrupt pool length");

            var pool = new uint[poolLen];
            fs.ReadExactly(MemoryMarshal.AsBytes(loaded.Index.AsSpan()));
            fs.ReadExactly(MemoryMarshal.AsBytes(pool.AsSpan()));
            loaded.ReplacePool(pool);

            world = loaded;
            double mb = fs.Length / (1024.0 * 1024.0);
            Console.WriteLine($"Loaded world cache: {mb:0.0} MB from '{path}' in {sw.ElapsedMilliseconds} ms.");
            return true;
        }
        catch (Exception ex)
        {
            return Reject(path, $"read failed ({ex.Message})");
        }
    }

    private static bool Reject(string path, string reason)
    {
        Console.WriteLine($"Ignoring world cache '{path}': {reason}. Regenerating.");
        return false;
    }
}
