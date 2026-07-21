using System.Numerics;

namespace VoxSlop.App.Engine;

/// <summary>
/// A shape that is rendered live each frame (not baked into the world), so it can
/// move and spin. The renderer voxelises it onto the world grid, so it keeps the
/// blocky look; it casts and receives shadows but has no collision.
///
/// Positions and sizes are in metres; the renderer converts to voxel units.
/// </summary>
public sealed class DynamicShape
{
    public bool IsSphere { get; init; }

    /// <summary>Centre in metres.</summary>
    public Vector3 Center { get; set; }

    /// <summary>Box half extents in metres. For a sphere, X is the radius.</summary>
    public Vector3 HalfExtents { get; init; }

    /// <summary>Current orientation, Euler radians applied X then Y then Z.</summary>
    public Vector3 Rotation { get; set; }

    /// <summary>Spin rate, radians per second per axis.</summary>
    public Vector3 AngularVelocity { get; init; }

    public Vector3 Color { get; init; } = new(0.8f, 0.8f, 0.8f);

    public static DynamicShape Box(Vector3 center, Vector3 halfExtents, Vector3 angularVelocity, Vector3 color) =>
        new() { Center = center, HalfExtents = halfExtents, AngularVelocity = angularVelocity, Color = color };

    public static DynamicShape Cube(Vector3 center, float halfSize, Vector3 angularVelocity, Vector3 color) =>
        Box(center, new Vector3(halfSize), angularVelocity, color);

    public static DynamicShape Sphere(Vector3 center, float radius, Vector3 angularVelocity, Vector3 color) =>
        new() { IsSphere = true, Center = center, HalfExtents = new Vector3(radius), AngularVelocity = angularVelocity, Color = color };

    public void Advance(float dt) => Rotation += AngularVelocity * dt;

    /// <summary>The current orthonormal box axes in world space.</summary>
    public (Vector3 X, Vector3 Y, Vector3 Z) Axes()
    {
        Matrix4x4 rot = Matrix4x4.CreateRotationX(Rotation.X)
                      * Matrix4x4.CreateRotationY(Rotation.Y)
                      * Matrix4x4.CreateRotationZ(Rotation.Z);
        return (Vector3.TransformNormal(Vector3.UnitX, rot),
                Vector3.TransformNormal(Vector3.UnitY, rot),
                Vector3.TransformNormal(Vector3.UnitZ, rot));
    }
}
