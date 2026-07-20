using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using VoxSlop.App.Render;
using VoxSlop.App.Voxels;

namespace VoxSlop.App.Engine;

/// <summary>Owns the window, the world and the render loop.</summary>
public sealed class Game : IDisposable
{
    // 1024 x 48 x 1024 bricks of 8 voxels at 5 cm = 8192 x 384 x 8192 voxels,
    // i.e. a ~410 x 19 x 410 m landscape. Y is only as tall as the terrain plus
    // headroom; the dense brick index costs 4 bytes per brick over the whole volume.
    private const int BrickDimX = 1024;
    private const int BrickDimY = 48;
    private const int BrickDimZ = 1024;
    private const int Seed = 1337;

    private readonly IWindow _window;

    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private IMouse _mouse = null!;
    private VoxelWorld _world = null!;
    private RaymarchRenderer _renderer = null!;
    private PlayerController _player = null!;

    private Vector2 _lookDelta;
    private Vector2? _lastMousePosition;
    private bool _mouseCaptured;

    private double _titleTimer;
    private int _framesSinceTitle;

    // Sun orbit. The sun sweeps a vertical east-west arc (rising +X, overhead +Y,
    // setting -X, then below the horizon for "night"), tilted slightly so it never
    // passes exactly overhead. Advances even when the cursor is released, and can
    // be paused with P.
    private const float SunSpeed = 0.15f; // radians per second, ~42 s per full circle
    private float _sunAngle = 0.6f;       // start mid-morning
    private bool _sunPaused;

    // Shadow-cache generations. Bumping the epoch invalidates every cached voxel
    // face; we do it only once the sun has travelled a small step, so a paused or
    // slow-moving sun keeps reusing cached shadows across frames. The visible cost
    // is that shadows update in ~ShadowEpochStep increments rather than continuously.
    private const float ShadowEpochStep = 0.008f; // radians of sun travel per generation
    private float _epochAngle = 0.6f;

    public Game()
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = "VoxSlop",
            // 4.3 is the floor for shader storage buffers, which the brickmap needs.
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 3)),
            VSync = true,
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        Console.WriteLine($"GPU: {_gl.GetStringS(StringName.Renderer)}");
        Console.WriteLine($"GL:  {_gl.GetStringS(StringName.Version)}");
        Console.WriteLine();

        // Cache the generated world next to the executable; a matching cache is
        // loaded on later runs instead of regenerating from scratch.
        string cachePath = Path.Combine(AppContext.BaseDirectory, "world.voxcache");
        _world = WorldStore.LoadOrGenerate(BrickDimX, BrickDimY, BrickDimZ, Seed, cachePath);

        string shaderDir = Path.Combine(AppContext.BaseDirectory, "Render", "Shaders");
        _renderer = new RaymarchRenderer(_gl, _world, shaderDir);
        _player = new PlayerController(_world, PlayerController.FindSpawn(_world));

        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        _mouse = _input.Mice[0];

        _keyboard.KeyDown += OnKeyDown;
        _mouse.MouseMove += OnMouseMove;
        _mouse.MouseDown += (_, _) => SetMouseCaptured(true);

        SetMouseCaptured(true);
        PrintControls();
    }

    private static void PrintControls()
    {
        Console.WriteLine();
        Console.WriteLine("Controls");
        Console.WriteLine("  Mouse        look");
        Console.WriteLine("  WASD         move        Shift  sprint");
        Console.WriteLine("  Space        jump / fly up          Ctrl  fly down");
        Console.WriteLine("  F            toggle walk / noclip");
        Console.WriteLine("  L            toggle sun shadows");
        Console.WriteLine("  P            pause / resume the sun");
        Console.WriteLine("  C            toggle per-voxel-face shadow cache");
        Console.WriteLine("  R            reload shaders from disk");
        Console.WriteLine("  Esc          release / recapture the cursor (click to recapture)");
        Console.WriteLine();
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int _)
    {
        switch (key)
        {
            case Key.Escape:
                SetMouseCaptured(!_mouseCaptured);
                break;
            case Key.F:
                _player.ToggleFlying();
                break;
            case Key.L:
                _renderer.Shadows = !_renderer.Shadows;
                break;
            case Key.P:
                _sunPaused = !_sunPaused;
                break;
            case Key.C:
                _renderer.ShadowCache = !_renderer.ShadowCache;
                // Force a rebuild of every face next frame so the toggle is clean.
                _renderer.ShadowEpoch++;
                break;
            case Key.R:
                _renderer.TryReloadShaders();
                break;
        }
    }

    private void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;
        _mouse.Cursor.CursorMode = captured ? CursorMode.Raw : CursorMode.Normal;
        _lastMousePosition = null;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (!_mouseCaptured)
        {
            _lastMousePosition = position;
            return;
        }

        // Raw mode still reports absolute positions, so accumulate deltas ourselves.
        if (_lastMousePosition is { } last) _lookDelta += position - last;
        _lastMousePosition = position;
    }

    private void OnUpdate(double deltaSeconds)
    {
        if (_mouseCaptured && _lookDelta != Vector2.Zero) _player.Look(_lookDelta);
        _lookDelta = Vector2.Zero;

        if (_mouseCaptured) _player.Update(_keyboard, (float)deltaSeconds);

        UpdateSun(deltaSeconds);
        UpdateTitle(deltaSeconds);
    }

    private void UpdateSun(double deltaSeconds)
    {
        if (!_sunPaused) _sunAngle += (float)deltaSeconds * SunSpeed;

        // Circle in the X-Y plane with a small constant Z lean, so the arc is
        // offset from straight overhead. The shader treats uSunDir as unit length
        // (it's a shadow-ray direction and SHADOW_RANGE assumes |dir| == 1), so
        // normalise here.
        _renderer.SunDirection = Vector3.Normalize(
            new Vector3(MathF.Cos(_sunAngle), MathF.Sin(_sunAngle), 0.30f));

        // Advance the shadow-cache generation once the sun has moved a full step.
        if (MathF.Abs(_sunAngle - _epochAngle) >= ShadowEpochStep)
        {
            _renderer.ShadowEpoch++;
            _epochAngle = _sunAngle;
        }
    }

    private void UpdateTitle(double deltaSeconds)
    {
        _framesSinceTitle++;
        _titleTimer += deltaSeconds;
        if (_titleTimer < 0.25) return;

        var p = _player.FeetPosition;
        _window.Title =
            $"VoxSlop  |  {_framesSinceTitle / _titleTimer:0} fps  |  " +
            $"{p.X:0.00}, {p.Y:0.00}, {p.Z:0.00} m  |  " +
            $"{(_player.Flying ? "noclip" : "walk")}  |  shadows {(_renderer.Shadows ? "on" : "off")}" +
            $"  |  cache {(_renderer.ShadowCache ? "on" : "off")}";

        _titleTimer = 0;
        _framesSinceTitle = 0;
    }

    private void OnRender(double _)
    {
        // Every pixel is written by the raymarch, so there is nothing to clear.
        var size = _window.FramebufferSize;
        _renderer.Render(_player.Camera, size.X, size.Y);
    }

    private void OnClosing() => Dispose();

    public void Dispose()
    {
        _renderer?.Dispose();
        //_input?.Dispose();
    }
}
