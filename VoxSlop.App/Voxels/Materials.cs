namespace VoxSlop.App.Voxels;

/// <summary>
/// Voxel material ids, stored one byte per voxel. Mirrored by the MAT_* constants
/// in Render/Shaders/raymarch.frag — extend both together (see STYLE.md §4).
/// </summary>
public static class Materials
{
    public const byte Air = 0;
    public const byte Grass = 1;
    public const byte Dirt = 2;
    public const byte Stone = 3;
    public const byte Concrete = 4;
    public const byte Rust = 5;
    public const byte GrassTuft = 6; // the raised voxel tufts, coloured lighter than the ground
}
