using System.Numerics;

namespace VoxSlop.App.Engine;

/// <summary>
/// Free-look camera. The raymarcher only needs an origin and an orthonormal
/// basis, so there are no view/projection matrices anywhere in this demo.
/// </summary>
public sealed class Camera
{
    private const float PitchLimit = 1.55334f; // ~89 degrees

    /// <summary>Eye position, in metres.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Rotation around +Y. Zero looks down -Z.</summary>
    public float Yaw { get; private set; }

    /// <summary>Rotation above the horizon, clamped just shy of straight up/down.</summary>
    public float Pitch { get; private set; }

    public float FovDegrees { get; set; } = 75f;

    public Vector3 Forward { get; private set; } = -Vector3.UnitZ;
    public Vector3 Right { get; private set; } = Vector3.UnitX;
    public Vector3 Up { get; private set; } = Vector3.UnitY;

    public Camera() => Rebuild();

    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -PitchLimit, PitchLimit);
        Rebuild();
    }

    /// <summary>Forward flattened onto the XZ plane — what WASD should follow.</summary>
    public Vector3 ForwardHorizontal
    {
        get
        {
            var f = new Vector3(Forward.X, 0f, Forward.Z);
            float len = f.Length();
            return len > 1e-5f ? f / len : -Vector3.UnitZ;
        }
    }

    public float TanHalfFov => MathF.Tan(float.DegreesToRadians(FovDegrees) * 0.5f);

    private void Rebuild()
    {
        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
        float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);

        Forward = Vector3.Normalize(new Vector3(sy * cp, sp, -cy * cp));
        Right = Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
        Up = Vector3.Cross(Right, Forward);
    }
}
