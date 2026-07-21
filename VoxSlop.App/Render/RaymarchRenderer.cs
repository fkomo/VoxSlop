using System.Numerics;
using Silk.NET.OpenGL;
using VoxSlop.App.Engine;
using VoxSlop.App.Voxels;

namespace VoxSlop.App.Render;

/// <summary>
/// Draws the world as a single fullscreen triangle whose fragment shader traces
/// the brickmap. The voxel data is uploaded once as two SSBOs; per frame we only
/// push a handful of camera uniforms.
/// </summary>
public sealed class RaymarchRenderer : IDisposable
{
    private const uint BrickIndexBinding = 0;
    private const uint VoxelPoolBinding = 1;
    private const uint ShadowCacheBinding = 2;
    private const uint DynamicShapeBinding = 3;
    private const uint MipPoolBinding = 4;

    /// <summary>Number of slots in the voxel-face shadow cache (16 bytes each = 32 MB).</summary>
    public const uint ShadowCacheSlots = 1u << 21;

    /// <summary>Upper bound on live shapes; the SSBO is sized for this many.</summary>
    public const int MaxDynamicShapes = 32;

    private const int FloatsPerShape = 20; // 5 vec4

    private readonly GL _gl;
    private readonly VoxelWorld _world;
    private readonly string _shaderDirectory;

    private readonly uint _vao;
    private readonly uint _brickIndexBuffer;
    private readonly uint _voxelPoolBuffer;
    private readonly uint _mipPoolBuffer;
    private readonly uint _shadowCacheBuffer;
    private readonly uint _dynamicShapeBuffer;

    private readonly float[] _shapeScratch = new float[MaxDynamicShapes * FloatsPerShape];
    private int _dynamicShapeCount;

    private Shader _shader;

    // --- Temporal anti-aliasing ---
    private Shader _taaShader = null!;
    private Shader _presentShader = null!;
    private uint _fbo;
    private uint _sceneTex;                 // this frame's raymarch output (rgb + depth)
    private readonly uint[] _historyTex = new uint[2];
    private int _historyCur;                // index of the most recently written history
    private int _fbWidth, _fbHeight;
    private int _jitterIndex;
    private bool _historyValid;             // false on first frame / resize / toggle

    // Previous frame's camera (voxel units), for reprojection.
    private Vector3 _prevPos, _prevRight, _prevUp, _prevForward;
    private float _prevTanHalfFov;

    private bool _taaEnabled = true;
    public bool TaaEnabled
    {
        get => _taaEnabled;
        set { if (value != _taaEnabled) { _taaEnabled = value; _historyValid = false; } }
    }

    public bool Shadows { get; set; } = true;

    public bool AmbientOcclusion { get; set; } = true;

    /// <summary>
    /// Cap on brick-level DDA steps; the practical view distance limiter. A ray can
    /// cross up to dimX+dimY+dimZ bricks (~640 at 256x128x256) before leaving the
    /// grid, so anything less clips distant terrain away mid-ray.
    /// </summary>
    public int MaxBrickSteps { get; set; } = 700;

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.72f, 0.28f));

    /// <summary>
    /// Shadow sub-samples per axis across each voxel face (NxN). Higher gives a
    /// smoother coverage ratio at voxel edges but costs N*N shadow rays per pixel.
    /// </summary>
    public int ShadowSamples { get; set; } = 2;

    /// <summary>
    /// When true, each voxel face's shadow is computed once per <see cref="ShadowEpoch"/>
    /// and reused across every pixel that covers it, instead of recomputing per pixel.
    /// </summary>
    public bool ShadowCache { get; set; } = true;

    /// <summary>
    /// Cache generation. Bump it whenever the shadow result would change (the sun
    /// moved, or the sample count changed) to invalidate stale cached faces.
    /// </summary>
    public uint ShadowEpoch { get; set; } = 1;

    // --- Orbiting point light ---
    public bool PointLightEnabled { get; set; } = true;

    /// <summary>Point light position, in metres (converted to voxel units on upload).</summary>
    public Vector3 PointLightPosition { get; set; }

    public Vector3 PointLightColor { get; set; } = new(1f, 0.3f, 0.2f);

    /// <summary>
    /// Intensity of the point light. Also the reach control: a surface only counts
    /// as lit (and only casts a shadow for this light) where strength * attenuation
    /// * NdotL clears the shader's threshold, so raising it enlarges the lit area.
    /// </summary>
    public float PointLightStrength { get; set; } = 6f;

    public RaymarchRenderer(GL gl, VoxelWorld world, string shaderDirectory)
    {
        _gl = gl;
        _world = world;
        _shaderDirectory = shaderDirectory;
        _shader = LoadShader();
        _taaShader = LoadPass("taa.frag");
        _presentShader = LoadPass("present.frag");

        // The raymarch writes every pixel, so no depth testing is ever needed.
        _gl.Disable(EnableCap.DepthTest);

        // Core profile still requires a bound VAO even when the vertex shader
        // synthesises its own positions.
        _vao = _gl.GenVertexArray();

        _brickIndexBuffer = UploadStorage(BrickIndexBinding, world.Index);
        _voxelPoolBuffer = UploadStorage(VoxelPoolBinding, world.Pool.Length > 0
            ? world.Pool
            : new uint[VoxelWorld.UintsPerBrick]); // never upload a zero-sized buffer
        _mipPoolBuffer = UploadStorage(MipPoolBinding, world.MipPool.Length > 0
            ? world.MipPool
            : new uint[VoxelWorld.MipUintsPerBrick]);

        // Zero-initialised: slots must not spuriously match epoch 1 with garbage data.
        _shadowCacheBuffer = UploadStorage(ShadowCacheBinding, new uint[ShadowCacheSlots * 4],
            BufferUsageARB.DynamicCopy);

        // Re-uploaded every frame with the current shape transforms; starts empty.
        _dynamicShapeBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _dynamicShapeBuffer);
        _gl.BufferData<float>(
            BufferTargetARB.ShaderStorageBuffer,
            (nuint)(_shapeScratch.Length * sizeof(float)),
            new ReadOnlySpan<float>(new float[_shapeScratch.Length]),
            BufferUsageARB.DynamicDraw);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, DynamicShapeBinding, _dynamicShapeBuffer);
    }

    /// <summary>
    /// Packs the current shapes and uploads them for this frame. Call once per
    /// frame before <see cref="Render"/>; shapes beyond <see cref="MaxDynamicShapes"/>
    /// are dropped.
    /// </summary>
    public void UpdateDynamicShapes(IReadOnlyList<DynamicShape> shapes)
    {
        int count = Math.Min(shapes.Count, MaxDynamicShapes);
        float inv = 1f / VoxelWorld.VoxelSize;

        for (int i = 0; i < count; i++)
        {
            DynamicShape s = shapes[i];
            var (ax, ay, az) = s.Axes();
            Vector3 c = s.Center * inv;
            Vector3 h = s.HalfExtents * inv; // for a sphere, h.X is the radius
            int o = i * FloatsPerShape;

            _shapeScratch[o + 0] = c.X;  _shapeScratch[o + 1] = c.Y;  _shapeScratch[o + 2] = c.Z;
            _shapeScratch[o + 3] = s.IsSphere ? 1f : 0f;
            _shapeScratch[o + 4] = ax.X; _shapeScratch[o + 5] = ax.Y; _shapeScratch[o + 6] = ax.Z;
            _shapeScratch[o + 7] = h.X;
            _shapeScratch[o + 8] = ay.X; _shapeScratch[o + 9] = ay.Y; _shapeScratch[o + 10] = ay.Z;
            _shapeScratch[o + 11] = h.Y;
            _shapeScratch[o + 12] = az.X; _shapeScratch[o + 13] = az.Y; _shapeScratch[o + 14] = az.Z;
            _shapeScratch[o + 15] = h.Z;
            _shapeScratch[o + 16] = s.Color.X; _shapeScratch[o + 17] = s.Color.Y; _shapeScratch[o + 18] = s.Color.Z;
            _shapeScratch[o + 19] = 0f;
        }

        _dynamicShapeCount = count;
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _dynamicShapeBuffer);
        _gl.BufferSubData<float>(
            BufferTargetARB.ShaderStorageBuffer, 0,
            (nuint)(count * FloatsPerShape * sizeof(float)),
            new ReadOnlySpan<float>(_shapeScratch, 0, count * FloatsPerShape));
    }

    private uint UploadStorage(uint binding, uint[] data, BufferUsageARB usage = BufferUsageARB.StaticDraw)
    {
        // 64-bit byte count: at high resolution the pool exceeds 2 GB, and
        // (int * int) here would silently overflow to a garbage buffer size.
        nuint bytes = (nuint)data.Length * sizeof(uint);

        const GetPName MaxShaderStorageBlockSize = (GetPName)0x90DE;
        _gl.GetInteger(MaxShaderStorageBlockSize, out int maxBlock);
        if (maxBlock > 0 && bytes > (nuint)maxBlock)
            Console.WriteLine(
                $"WARNING: SSBO binding {binding} needs {bytes / (1024 * 1024)} MB but the driver caps " +
                $"shader storage blocks at {maxBlock / (1024 * 1024)} MB. The world is too big for this GPU " +
                $"at the current voxel size -- expect missing/corrupt geometry. Reduce the world size or voxel count.");

        uint buffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        _gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, bytes, new ReadOnlySpan<uint>(data), usage);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, buffer);
        return buffer;
    }

    private Shader LoadShader() => LoadPass("raymarch.frag");

    // All post passes reuse the fullscreen-triangle vertex shader.
    private Shader LoadPass(string frag) => Shader.FromSource(
        _gl,
        File.ReadAllText(Path.Combine(_shaderDirectory, "raymarch.vert")),
        File.ReadAllText(Path.Combine(_shaderDirectory, frag)));

    /// <summary>Recompiles all passes from disk, keeping the running ones if any fail.</summary>
    public bool TryReloadShaders()
    {
        try
        {
            var raymarch = LoadPass("raymarch.frag");
            var taa = LoadPass("taa.frag");
            var present = LoadPass("present.frag");
            _shader.Dispose(); _shader = raymarch;
            _taaShader.Dispose(); _taaShader = taa;
            _presentShader.Dispose(); _presentShader = present;
            Console.WriteLine("Shaders reloaded.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Shader reload failed, keeping previous:\n{ex.Message}");
            return false;
        }
    }

    // Recreates the offscreen scene texture and the two ping-pong history textures
    // when the framebuffer size changes. RGBA16F: rgb colour, plus depth in scene's alpha.
    private unsafe void EnsureTargets(int width, int height)
    {
        if (width == _fbWidth && height == _fbHeight) return;
        _fbWidth = width; _fbHeight = height;
        _historyValid = false;

        if (_fbo == 0) _fbo = _gl.GenFramebuffer();
        if (_sceneTex != 0) { _gl.DeleteTexture(_sceneTex); _gl.DeleteTexture(_historyTex[0]); _gl.DeleteTexture(_historyTex[1]); }

        _sceneTex = CreateColorTexture(width, height, linear: false);
        _historyTex[0] = CreateColorTexture(width, height, linear: true);
        _historyTex[1] = CreateColorTexture(width, height, linear: true);
    }

    private unsafe uint CreateColorTexture(int width, int height, bool linear)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.Float, null);
        int filter = (int)(linear ? GLEnum.Linear : GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    // Halton-based sub-pixel jitter in NDC, cycling over 8 samples.
    private Vector2 NextJitter(int width, int height)
    {
        int i = (_jitterIndex++ & 7) + 1;
        float hx = Halton(i, 2) - 0.5f, hy = Halton(i, 3) - 0.5f; // [-0.5, 0.5] pixels
        return new Vector2(2f * hx / width, 2f * hy / height);
    }

    private static float Halton(int index, int base_)
    {
        float f = 1f, r = 0f;
        while (index > 0) { f /= base_; r += f * (index % base_); index /= base_; }
        return r;
    }

    public void Render(Camera camera, int width, int height)
    {
        _gl.BindVertexArray(_vao);

        if (!TaaEnabled)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            DrawScene(camera, width, height, Vector2.Zero);
            StorePrevCamera(camera);
            return;
        }

        EnsureTargets(width, height);
        Vector2 jitter = NextJitter(width, height);
        int prev = _historyCur;
        int cur = 1 - _historyCur;

        // Pass 1: raymarch the jittered frame into the scene texture.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _sceneTex, 0);
        DrawScene(camera, width, height, jitter);

        // Pass 2: blend into history (reprojected + clamped) -> historyTex[cur].
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _historyTex[cur], 0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _taaShader.Use();
        BindTexture(0, _sceneTex);
        BindTexture(1, _historyTex[prev]);
        _taaShader.Set("uScene", 0);
        _taaShader.Set("uHistory", 1);
        _taaShader.Set("uResolution", width, height);
        _taaShader.Set("uJitter", jitter.X, jitter.Y);
        _taaShader.Set("uTanHalfFov", camera.TanHalfFov);
        _taaShader.Set("uCamPos", camera.Position / VoxelWorld.VoxelSize);
        _taaShader.Set("uCamRight", camera.Right);
        _taaShader.Set("uCamUp", camera.Up);
        _taaShader.Set("uCamForward", camera.Forward);
        _taaShader.Set("uPrevPos", _prevPos);
        _taaShader.Set("uPrevRight", _prevRight);
        _taaShader.Set("uPrevUp", _prevUp);
        _taaShader.Set("uPrevForward", _prevForward);
        _taaShader.Set("uPrevTanHalfFov", _prevTanHalfFov);
        _taaShader.Set("uReset", _historyValid ? 0 : 1);
        _taaShader.Set("uBlend", 0.1f);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Pass 3: present history[cur] to the screen with the crosshair on top.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _presentShader.Use();
        BindTexture(0, _historyTex[cur]);
        _presentShader.Set("uImage", 0);
        _presentShader.Set("uResolution", width, height);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        _historyCur = cur;
        _historyValid = true;
        StorePrevCamera(camera);
    }

    // Sets all scene uniforms and draws the raymarch pass into the current framebuffer.
    private void DrawScene(Camera camera, int width, int height, Vector2 jitter)
    {
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        _shader.Use();
        _shader.SetIVec3("uBrickDims", _world.BrickDimX, _world.BrickDimY, _world.BrickDimZ);
        // The shader works in voxel units, so the DDA never has to scale by voxel size.
        _shader.Set("uCamPos", camera.Position / VoxelWorld.VoxelSize);
        _shader.Set("uCamRight", camera.Right);
        _shader.Set("uCamUp", camera.Up);
        _shader.Set("uCamForward", camera.Forward);
        _shader.Set("uResolution", width, height);
        _shader.Set("uTanHalfFov", camera.TanHalfFov);
        _shader.Set("uJitter", jitter.X, jitter.Y);
        _shader.Set("uSunDir", SunDirection);
        _shader.Set("uShadows", Shadows ? 1 : 0);
        _shader.Set("uAo", AmbientOcclusion ? 1 : 0);
        _shader.Set("uMaxBrickSteps", MaxBrickSteps);
        _shader.Set("uShadowSamples", ShadowSamples);
        _shader.Set("uShadowCache", ShadowCache ? 1 : 0);
        _shader.Set("uShadowEpoch", ShadowEpoch);
        _shader.Set("uShadowCacheSize", ShadowCacheSlots);
        _shader.Set("uPointOn", PointLightEnabled ? 1 : 0);
        _shader.Set("uPointPos", PointLightPosition / VoxelWorld.VoxelSize);
        _shader.Set("uPointColor", PointLightColor);
        _shader.Set("uPointStrength", PointLightStrength);
        _shader.Set("uShapeCount", _dynamicShapeCount);

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Make this frame's cache writes visible to next frame's cache reads.
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    private void BindTexture(uint unit, uint tex)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + (int)unit);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
    }

    private void StorePrevCamera(Camera camera)
    {
        _prevPos = camera.Position / VoxelWorld.VoxelSize;
        _prevRight = camera.Right;
        _prevUp = camera.Up;
        _prevForward = camera.Forward;
        _prevTanHalfFov = camera.TanHalfFov;
    }

    public void Dispose()
    {
        _shader.Dispose();
        _taaShader.Dispose();
        _presentShader.Dispose();
        _gl.DeleteBuffer(_brickIndexBuffer);
        _gl.DeleteBuffer(_voxelPoolBuffer);
        _gl.DeleteBuffer(_mipPoolBuffer);
        _gl.DeleteBuffer(_shadowCacheBuffer);
        _gl.DeleteBuffer(_dynamicShapeBuffer);
        if (_fbo != 0) _gl.DeleteFramebuffer(_fbo);
        if (_sceneTex != 0) { _gl.DeleteTexture(_sceneTex); _gl.DeleteTexture(_historyTex[0]); _gl.DeleteTexture(_historyTex[1]); }
        _gl.DeleteVertexArray(_vao);
    }
}
