using System.Numerics;

namespace VoxSlop.App.Engine;

/// <summary>
/// A shape that is rendered live each frame (not baked into the world), so it can
/// move and spin. The renderer voxelises it onto the world grid, so it keeps the
/// blocky look; it receives shadows from the terrain but does not cast them.
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

    /// <summary>Rest position the shape springs back to after being pushed, metres.</summary>
    public Vector3 Home { get; private set; }

    /// <summary>Linear velocity, metres/second. Driven by pushes, spring and damping.</summary>
    public Vector3 Velocity { get; set; }

    // Spring/damping that pull a shoved shape back to its home spot. Kept firm so a
    // push produces a visible drift-and-return rather than the shape wandering off.
    private const float HomeStiffness = 10f;  // spring acceleration per metre of offset
    private const float LinearDamping = 3.5f; // velocity decay per second
    private const float MaxOffset = 1.5f;     // hard cap on how far it can be pushed, metres
    private const float PushCoupling = 4f;    // how strongly the player shoves the shape

    public static DynamicShape Box(Vector3 center, Vector3 halfExtents, Vector3 angularVelocity, Vector3 color) =>
        new() { Center = center, Home = center, HalfExtents = halfExtents, AngularVelocity = angularVelocity, Color = color };

    public static DynamicShape Cube(Vector3 center, float halfSize, Vector3 angularVelocity, Vector3 color) =>
        Box(center, new Vector3(halfSize), angularVelocity, color);

    public static DynamicShape Sphere(Vector3 center, float radius, Vector3 angularVelocity, Vector3 color) =>
        new() { IsSphere = true, Center = center, Home = center, HalfExtents = new Vector3(radius), AngularVelocity = angularVelocity, Color = color };

    public void Advance(float dt)
    {
        Rotation += AngularVelocity * dt;

        // Integrate the linear body: spring toward home, damping, then move.
        Velocity += (Home - Center) * (HomeStiffness * dt);
        Velocity *= MathF.Max(0f, 1f - LinearDamping * dt);
        Center += Velocity * dt;

        // Never let it stray past the cap.
        Vector3 offset = Center - Home;
        if (offset.Length() > MaxOffset)
        {
            Center = Home + Vector3.Normalize(offset) * MaxOffset;
            Velocity -= offset * (Vector3.Dot(Velocity, offset) / offset.LengthSquared()); // kill outward part
        }
    }

    /// <summary>Adds a bounded impulse from the player pushing the shape.</summary>
    public void ApplyPush(Vector3 direction, float amount) =>
        Velocity += direction * MathF.Min(amount * PushCoupling, 3f);

    /// <summary>World-space velocity of the shape's surface at a point (spin + linear).</summary>
    public Vector3 SurfaceVelocityAt(Vector3 worldPoint) =>
        Velocity + Vector3.Cross(AngularVelocity, worldPoint - Center);

    /// <summary>
    /// Does a world-space axis-aligned box (metres) overlap this shape? Sphere is a
    /// clamp-distance test; box uses the separating-axis theorem against the OBB.
    /// </summary>
    public bool OverlapsAabb(Vector3 min, Vector3 max)
    {
        Vector3 ca = (min + max) * 0.5f;   // AABB centre
        Vector3 ea = (max - min) * 0.5f;   // AABB half extents

        if (IsSphere)
        {
            Vector3 closest = Vector3.Clamp(Center, min, max);
            float r = HalfExtents.X;
            return (closest - Center).LengthSquared() <= r * r;
        }

        var (u0, u1, u2) = Axes();
        Vector3 eb = HalfExtents;
        Vector3 d = Center - ca;

        // Separating-axis test: the 3 AABB axes, the 3 OBB axes, and 9 cross products.
        bool Separated(Vector3 axis)
        {
            float len2 = axis.LengthSquared();
            if (len2 < 1e-8f) return false; // degenerate cross product: skip
            float ra = ea.X * MathF.Abs(axis.X) + ea.Y * MathF.Abs(axis.Y) + ea.Z * MathF.Abs(axis.Z);
            float rb = eb.X * MathF.Abs(Vector3.Dot(axis, u0))
                     + eb.Y * MathF.Abs(Vector3.Dot(axis, u1))
                     + eb.Z * MathF.Abs(Vector3.Dot(axis, u2));
            return MathF.Abs(Vector3.Dot(d, axis)) > ra + rb;
        }

        if (Separated(Vector3.UnitX) || Separated(Vector3.UnitY) || Separated(Vector3.UnitZ)) return false;
        if (Separated(u0) || Separated(u1) || Separated(u2)) return false;

        Span<Vector3> world = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        Span<Vector3> obb = [u0, u1, u2];
        foreach (var w in world)
            foreach (var o in obb)
                if (Separated(Vector3.Cross(w, o))) return false;

        return true;
    }

    /// <summary>
    /// If the AABB overlaps this shape, returns the minimum push that separates the
    /// AABB from it. <paramref name="push"/> moves the AABB out; <paramref name="normal"/>
    /// is the unit push direction (from shape toward AABB); <paramref name="contact"/>
    /// is an approximate contact point.
    /// </summary>
    public bool TryResolveAabb(Vector3 min, Vector3 max, out Vector3 push, out Vector3 normal, out Vector3 contact)
    {
        push = Vector3.Zero; normal = Vector3.Zero; contact = Vector3.Zero;
        Vector3 ca = (min + max) * 0.5f;

        if (IsSphere)
        {
            Vector3 closest = Vector3.Clamp(Center, min, max);
            Vector3 diff = closest - Center;
            float dist = diff.Length();
            float r = HalfExtents.X;
            if (dist >= r) return false;

            // Push the AABB away from the sphere centre; if the centre is inside the
            // box (dist ~ 0), fall back to pushing straight up.
            normal = dist > 1e-5f ? diff / dist : Vector3.UnitY;
            push = normal * (r - dist);
            contact = Center + normal * r;
            return true;
        }

        var (u0, u1, u2) = Axes();
        Vector3 eb = HalfExtents;
        Vector3 d = ca - Center;   // from shape toward AABB, so the MTV pushes the player out

        float bestOverlap = float.MaxValue;
        Vector3 bestAxis = Vector3.Zero;

        // Test the same 15 axes, tracking the one with the least overlap (the MTV).
        Vector3 ea = (max - min) * 0.5f;
        bool Overlap(Vector3 axis)
        {
            float len = axis.Length();
            if (len < 1e-5f) return true; // degenerate cross product: not separating
            axis /= len;
            float ra = ea.X * MathF.Abs(axis.X) + ea.Y * MathF.Abs(axis.Y) + ea.Z * MathF.Abs(axis.Z);
            float rb = eb.X * MathF.Abs(Vector3.Dot(axis, u0))
                     + eb.Y * MathF.Abs(Vector3.Dot(axis, u1))
                     + eb.Z * MathF.Abs(Vector3.Dot(axis, u2));
            float sep = Vector3.Dot(d, axis);
            float overlap = ra + rb - MathF.Abs(sep);
            if (overlap <= 0f) return false; // separating axis found -> no collision
            if (overlap < bestOverlap)
            {
                bestOverlap = overlap;
                bestAxis = sep < 0f ? -axis : axis; // orient toward the AABB
            }
            return true;
        }

        Span<Vector3> axes =
        [
            Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, u0, u1, u2,
            Vector3.Cross(Vector3.UnitX, u0), Vector3.Cross(Vector3.UnitX, u1), Vector3.Cross(Vector3.UnitX, u2),
            Vector3.Cross(Vector3.UnitY, u0), Vector3.Cross(Vector3.UnitY, u1), Vector3.Cross(Vector3.UnitY, u2),
            Vector3.Cross(Vector3.UnitZ, u0), Vector3.Cross(Vector3.UnitZ, u1), Vector3.Cross(Vector3.UnitZ, u2),
        ];
        foreach (var axis in axes)
            if (!Overlap(axis)) return false;

        normal = bestAxis;
        push = bestAxis * bestOverlap;
        contact = ca - normal * Vector3.Dot(ca - Center, normal); // AABB centre projected toward the face
        return true;
    }

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
