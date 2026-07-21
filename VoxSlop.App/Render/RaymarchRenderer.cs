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
    private readonly uint _shadowCacheBuffer;
    private readonly uint _dynamicShapeBuffer;

    private readonly float[] _shapeScratch = new float[MaxDynamicShapes * FloatsPerShape];
    private int _dynamicShapeCount;

    private Shader _shader;

    public bool Shadows { get; set; } = true;

    /// <summary>
    /// Cap on brick-level DDA steps; the practical view distance limiter. A ray can
    /// cross up to dimX+dimY+dimZ bricks (~1600 at 640x320x640) before leaving the
    /// grid, so anything less clips distant terrain away mid-ray.
    /// </summary>
    public int MaxBrickSteps { get; set; } = 1700;

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

        // Core profile still requires a bound VAO even when the vertex shader
        // synthesises its own positions.
        _vao = _gl.GenVertexArray();

        _brickIndexBuffer = UploadStorage(BrickIndexBinding, world.Index);
        _voxelPoolBuffer = UploadStorage(VoxelPoolBinding, world.Pool.Length > 0
            ? world.Pool
            : new uint[VoxelWorld.UintsPerBrick]); // never upload a zero-sized buffer

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

    private Shader LoadShader() => Shader.FromSource(
        _gl,
        File.ReadAllText(Path.Combine(_shaderDirectory, "raymarch.vert")),
        File.ReadAllText(Path.Combine(_shaderDirectory, "raymarch.frag")));

    /// <summary>Recompiles from disk, keeping the running shader if the new one fails.</summary>
    public bool TryReloadShaders()
    {
        try
        {
            var replacement = LoadShader();
            _shader.Dispose();
            _shader = replacement;
            Console.WriteLine("Shaders reloaded.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Shader reload failed, keeping previous:\n{ex.Message}");
            return false;
        }
    }

    public void Render(Camera camera, int width, int height)
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
        _shader.Set("uSunDir", SunDirection);
        _shader.Set("uShadows", Shadows ? 1 : 0);
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

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Make this frame's cache writes visible to next frame's cache reads.
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteBuffer(_brickIndexBuffer);
        _gl.DeleteBuffer(_voxelPoolBuffer);
        _gl.DeleteBuffer(_shadowCacheBuffer);
        _gl.DeleteBuffer(_dynamicShapeBuffer);
        _gl.DeleteVertexArray(_vao);
    }
}
