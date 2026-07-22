using System.Numerics;
using Silk.NET.Input;
using VoxSlop.App.Voxels;

namespace VoxSlop.App.Engine;

/// <summary>
/// Standard FPS locomotion: mouse look, WASD, gravity and jumping, with an
/// axis-separated move-and-slide against the voxel volume. Press F for noclip.
/// </summary>
public sealed class PlayerController
{
    // Player capsule approximated as an AABB, in metres.
    private const float HalfWidth = 0.3f;
    private const float Height = 1.8f;
    private const float EyeHeight = 1.65f;

    private const float WalkSpeed = 4.5f;
    private const float SprintSpeed = 8.5f;
    private const float FlySpeed = 8f;
    private const float FlySprintSpeed = 30f;
    private const float Gravity = -22f;
    private const float JumpSpeed = 7.2f;
    private const float MouseSensitivity = 0.0022f;

    // At cm-scale voxels every slope is a staircase of tiny ledges, so horizontal
    // motion has to be allowed to climb over them or the player wedges instantly.
    private const float StepHeight = 0.65f;
    private const float StepProbe = 0.02f;

    private readonly VoxelWorld _world;
    private Vector3 _feet;
    private Vector3 _velocity;
    private bool _onGround;

    public Camera Camera { get; } = new();
    public bool Flying { get; private set; }
    public bool OnGround => _onGround;
    public Vector3 FeetPosition => _feet;

    public PlayerController(VoxelWorld world, Vector3 spawnFeet)
    {
        _world = world;
        _feet = spawnFeet;
        ResolveOverlap();
        SyncCamera();
    }

    public void Look(Vector2 mouseDelta) =>
        Camera.Rotate(mouseDelta.X * MouseSensitivity, -mouseDelta.Y * MouseSensitivity);

    public void ToggleFlying()
    {
        Flying = !Flying;
        _velocity = Vector3.Zero;
    }

    public void Update(IKeyboard kb, float dt)
    {
        // Clamp dt so a stall (shader reload, alt-tab) can't teleport the player through geometry.
        dt = MathF.Min(dt, 0.05f);

        var wish = Vector3.Zero;
        if (kb.IsKeyPressed(Key.W)) wish += Camera.ForwardHorizontal;
        if (kb.IsKeyPressed(Key.S)) wish -= Camera.ForwardHorizontal;
        if (kb.IsKeyPressed(Key.D)) wish += Camera.Right;
        if (kb.IsKeyPressed(Key.A)) wish -= Camera.Right;
        if (wish.LengthSquared() > 1e-6f) wish = Vector3.Normalize(wish);

        bool sprint = kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight);

        if (Flying)
        {
            var dir = wish;
            if (kb.IsKeyPressed(Key.Space)) dir += Vector3.UnitY;
            if (kb.IsKeyPressed(Key.ControlLeft)) dir -= Vector3.UnitY;
            if (dir.LengthSquared() > 1e-6f) dir = Vector3.Normalize(dir);

            _feet += dir * (sprint ? FlySprintSpeed : FlySpeed) * dt;
            _velocity = Vector3.Zero;
        }
        else
        {
            ResolveOverlap();

            float speed = sprint ? SprintSpeed : WalkSpeed;
            _velocity.X = wish.X * speed;
            _velocity.Z = wish.Z * speed;
            _velocity.Y += Gravity * dt;

            if (_onGround && kb.IsKeyPressed(Key.Space))
            {
                _velocity.Y = JumpSpeed;
                _onGround = false;
            }

            MoveAndSlide(_velocity * dt);
        }

        ClampToWorld();
        SyncCamera();
    }

    /// <summary>
    /// Resolves one axis at a time so that sliding along a wall keeps the other
    /// two components of motion intact.
    /// </summary>
    private void MoveAndSlide(Vector3 delta)
    {
        bool couldStep = _onGround; // grounded state as of the start of this frame
        _onGround = false;

        MoveHorizontal(ref _feet.X, delta.X, ref _velocity.X, couldStep);
        MoveHorizontal(ref _feet.Z, delta.Z, ref _velocity.Z, couldStep);

        _feet.Y += delta.Y;
        if (Collides())
        {
            _feet.Y -= delta.Y;
            if (delta.Y < 0f) _onGround = true;
            _velocity.Y = 0f;
        }
    }

    /// <summary>
    /// Moves along one horizontal axis, stepping up over small obstructions
    /// rather than stopping dead. The lift search takes the *smallest* clearance
    /// that works, so following a slope reads as a glide rather than a hop.
    /// </summary>
    private void MoveHorizontal(ref float coord, float delta, ref float velocityComponent, bool couldStep)
    {
        if (delta == 0f) return;

        float startY = _feet.Y;
        coord += delta;
        if (!Collides()) return;

        if (couldStep)
        {
            for (float lift = StepProbe; lift <= StepHeight; lift += StepProbe)
            {
                _feet.Y = startY + lift;
                if (!Collides())
                {
                    _onGround = true;
                    return;
                }
            }
        }

        // Genuinely walled in on this axis — give the movement back.
        _feet.Y = startY;
        coord -= delta;
        velocityComponent = 0f;
    }

    /// <summary>
    /// Lifts the player out of geometry they somehow ended up inside. Without
    /// this, one bad frame leaves every subsequent move colliding and reverting.
    /// </summary>
    private void ResolveOverlap()
    {
        for (int i = 0; i < 200 && Collides(); i++)
            _feet.Y += StepProbe;
    }

    private bool Collides()
    {
        var min = new Vector3(_feet.X - HalfWidth, _feet.Y, _feet.Z - HalfWidth);
        var max = new Vector3(_feet.X + HalfWidth, _feet.Y + Height, _feet.Z + HalfWidth);
        return _world.OverlapsSolid(min, max);
    }

    /// <summary>Keeps the player inside the grid — outside it every voxel reads as air.</summary>
    private void ClampToWorld()
    {
        float maxX = _world.VoxelDimX * VoxelWorld.VoxelSize - HalfWidth;
        float maxZ = _world.VoxelDimZ * VoxelWorld.VoxelSize - HalfWidth;
        float maxY = _world.VoxelDimY * VoxelWorld.VoxelSize - Height;

        _feet.X = Math.Clamp(_feet.X, HalfWidth, maxX);
        _feet.Z = Math.Clamp(_feet.Z, HalfWidth, maxZ);
        _feet.Y = Math.Clamp(_feet.Y, 0f, maxY);
    }

    private void SyncCamera() => Camera.Position = _feet + new Vector3(0f, EyeHeight, 0f);

    /// <summary>Finds a standing spot near the middle of the map.</summary>
    public static Vector3 FindSpawn(VoxelWorld world)
    {
        int vx = world.VoxelDimX / 2, vz = world.VoxelDimZ / 2;
        int surface = world.SurfaceHeight(vx, vz);
        float y = (surface + 1) * VoxelWorld.VoxelSize + 0.2f;
        return new Vector3(vx * VoxelWorld.VoxelSize, y, vz * VoxelWorld.VoxelSize);
    }
}
