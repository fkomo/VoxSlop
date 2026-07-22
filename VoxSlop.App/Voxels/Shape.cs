using System.Numerics;

namespace VoxSlop.App.Voxels;

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
