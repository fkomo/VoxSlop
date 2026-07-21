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
    // 640 x 320 x 640 bricks of 8 voxels at 2 cm = 5120 x 2560 x 5120 voxels,
    // i.e. a ~102 x 51 x 102 m world. The brick index is dense over the whole
    // volume at 4 bytes/brick, so the tall (mostly-empty) headroom costs VRAM: the
    // index alone is ~500 MB here. The surface pool is unaffected by height.
    private const int BrickDimX = 640;
    private const int BrickDimY = 320;
    private const int BrickDimZ = 640;
    private const int Seed = 1337;

    // Scatter single +1 voxels on the surface for a rougher, tuftier grass look.
    private const bool AddTerrainNoise = true;

    private readonly IWindow _window;

    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private IMouse _mouse = null!;
    private VoxelWorld _world = null!;
    private RaymarchRenderer _renderer = null!;
    private PlayerController _player = null!;

    private Vector3 _playerStartingPosition;

    private Vector2 _lookDelta;
    private Vector2? _lastMousePosition;
    private bool _mouseCaptured;

    // Borderless (windowed) fullscreen state, restored when toggled back.
    private bool _borderless;
    private Vector2D<int> _savedSize, _savedPos;

    private double _titleTimer;
    private int _framesSinceTitle;

    // Sun orbit. The sun sweeps a vertical east-west arc (rising +X, overhead +Y,
    // setting -X, then below the horizon for "night"), tilted slightly so it never
    // passes exactly overhead. Advances even when the cursor is released, and can
    // be paused with P.
    private const float SunSpeed = 0.05f; // radians per second, ~42 s per full circle
    private float _sunAngle = 0.6f;       // start mid-morning
    private bool _sunPaused;

    // Shadow-cache generations. Bumping the epoch invalidates every cached voxel
    // face; we do it only once the sun has travelled a small step, so a paused or
    // slow-moving sun keeps reusing cached shadows across frames. The visible cost
    // is that shadows update in ~ShadowEpochStep increments rather than continuously.
    private const float ShadowEpochStep = 0.008f; // radians of sun travel per generation
    private float _epochAngle = 0.6f;

    // Point light orbiting the player. It changes to the next palette colour on
    // every completed revolution.
    private const float PointOrbitSpeed = 0.5f;  // radians per second, ~5.7 s per orbit
    private const float PointOrbitRadius = 2.5f; // metres from the player
    private const float PointOrbitHeight = 1.2f; // metres above the player's feet
    private float _pointAngle;
    private int _pointColorIndex;
    private static readonly Vector3[] PointColors =
    [
        new(0.0f, 0.00f, 0.80f), // blue
        //new(1.0f, 0.25f, 0.20f), // red
        //new(1.0f, 0.55f, 0.15f), // orange
        //new(1.0f, 0.95f, 0.30f), // yellow
        //new(0.30f, 1.0f, 0.35f), // green
        //new(0.25f, 0.8f, 1.0f),  // cyan
        //new(0.45f, 0.4f, 1.0f),  // indigo
        //new(1.0f, 0.4f, 0.95f),  // magenta
    ];

    // Live spinning shapes, rendered each frame (not baked into the world).
    private readonly List<DynamicShape> _shapes = [];

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
        _world = WorldStore.LoadOrGenerate(BrickDimX, BrickDimY, BrickDimZ, Seed, AddTerrainNoise, cachePath);

        string shaderDir = Path.Combine(AppContext.BaseDirectory, "Render", "Shaders");
        _renderer = new RaymarchRenderer(_gl, _world, shaderDir);
        _player = new PlayerController(_world, PlayerController.FindSpawn(_world));
        BuildDynamicShapes();

        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        _mouse = _input.Mice[0];

        _keyboard.KeyDown += OnKeyDown;
        _mouse.MouseMove += OnMouseMove;
        _mouse.MouseDown += (_, _) => SetMouseCaptured(true);

        SetMouseCaptured(true);
        PrintControls();
    }

    private void BuildDynamicShapes()
    {
        // Float the objects near spawn so they are immediately in view; they spin
        // in place. Colours mirror the concrete/rust world materials.
        var concrete = new Vector3(0.70f, 0.68f, 0.64f);
        var rust = new Vector3(0.83f, 0.42f, 0.14f);
        Vector3 near(float x, float y, float z) => _player.FeetPosition + new Vector3(x, y, z);

        _shapes.Add(DynamicShape.Cube(near(6f, 3f, 4f), 2.5f, new Vector3(0.08f, 0.14f, 0.05f), concrete)); // 5 m cube
        _shapes.Add(DynamicShape.Box(near(-7f, 3f, 3f), new Vector3(4f, 0.75f, 1.5f),                       // 8 x 1.5 x 3 m slab
                                     new Vector3(0.03f, 0.11f, 0.06f), concrete));
        _shapes.Add(DynamicShape.Sphere(near(0f, 3.5f, -6f), 1.4f, new Vector3(0.09f, 0.09f, 0.09f), rust));

        _playerStartingPosition = _player.FeetPosition;
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
        Console.WriteLine("  O            toggle ambient occlusion");
        Console.WriteLine("  P            pause / resume the sun");
        Console.WriteLine("  C            toggle per-voxel-face shadow cache");
        Console.WriteLine("  G            toggle the orbiting point light");
        Console.WriteLine("  T            toggle temporal anti-aliasing (TAA)");
        Console.WriteLine("  F11          toggle borderless fullscreen");
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
            case Key.O:
                _renderer.AmbientOcclusion = !_renderer.AmbientOcclusion;
                break;
            case Key.P:
                _sunPaused = !_sunPaused;
                break;
            case Key.C:
                _renderer.ShadowCache = !_renderer.ShadowCache;
                // Force a rebuild of every face next frame so the toggle is clean.
                _renderer.ShadowEpoch++;
                break;
            case Key.G:
                _renderer.PointLightEnabled = !_renderer.PointLightEnabled;
                break;
            case Key.T:
                _renderer.TaaEnabled = !_renderer.TaaEnabled;
                break;
            case Key.F11:
                ToggleBorderlessFullscreen();
                break;
            case Key.R:
                _renderer.TryReloadShaders();
                break;
        }
    }

    // Borderless windowed fullscreen: a hidden-border window sized to the monitor,
    // rather than an exclusive-fullscreen video mode change (which alt-tabs poorly).
    private void ToggleBorderlessFullscreen()
    {
        if (!_borderless)
        {
            _savedSize = _window.Size;
            _savedPos = _window.Position;
            if (_window.Monitor is { } monitor)
            {
                _window.WindowBorder = WindowBorder.Hidden;
                _window.Position = monitor.Bounds.Origin;
                _window.Size = monitor.Bounds.Size;
                _borderless = true;
            }
        }
        else
        {
            _window.WindowBorder = WindowBorder.Resizable;
            _window.Size = _savedSize;
            _window.Position = _savedPos;
            _borderless = false;
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
        UpdatePointLight(deltaSeconds);
        UpdateShapes(deltaSeconds);
        UpdateTitle(deltaSeconds);
    }

    private void UpdateShapes(double deltaSeconds)
    {
        foreach (var shape in _shapes) shape.Advance((float)deltaSeconds);
        _renderer.UpdateDynamicShapes(_shapes);
    }

    private void UpdatePointLight(double deltaSeconds)
    {
        float previous = _pointAngle;
        _pointAngle += (float)deltaSeconds * PointOrbitSpeed;

        // Advance the colour whenever the orbit passes a full-revolution boundary.
        if (MathF.Floor(_pointAngle / MathF.Tau) != MathF.Floor(previous / MathF.Tau))
            _pointColorIndex = (_pointColorIndex + 1) % PointColors.Length;

        var centre = _playerStartingPosition + new Vector3(0f, PointOrbitHeight, 0f);
        _renderer.PointLightPosition = centre + new Vector3(
            MathF.Cos(_pointAngle) * PointOrbitRadius, 0f, MathF.Sin(_pointAngle) * PointOrbitRadius);
        _renderer.PointLightColor = PointColors[_pointColorIndex];
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
