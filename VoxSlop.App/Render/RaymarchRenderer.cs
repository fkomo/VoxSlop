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

    private readonly GL _gl;
    private readonly VoxelWorld _world;
    private readonly string _shaderDirectory;

    private readonly uint _vao;
    private readonly uint _brickIndexBuffer;
    private readonly uint _voxelPoolBuffer;

    private Shader _shader;

    public bool Shadows { get; set; } = true;

    /// <summary>
    /// Cap on brick-level DDA steps; the practical view distance limiter. A ray can
    /// cross up to dimX+dimY+dimZ bricks (~2100 at 1024x48x1024) before leaving the
    /// grid, so anything less clips distant terrain away mid-ray.
    /// </summary>
    public int MaxBrickSteps { get; set; } = 2200;

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.72f, 0.28f));

    /// <summary>
    /// Shadow sub-samples per axis across each voxel face (NxN). Higher gives a
    /// smoother coverage ratio at voxel edges but costs N*N shadow rays per pixel.
    /// </summary>
    public int ShadowSamples { get; set; } = 2;

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
    }

    private uint UploadStorage(uint binding, uint[] data)
    {
        uint buffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        _gl.BufferData<uint>(
            BufferTargetARB.ShaderStorageBuffer,
            (nuint)(data.Length * sizeof(uint)),
            new ReadOnlySpan<uint>(data),
            BufferUsageARB.StaticDraw);
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

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteBuffer(_brickIndexBuffer);
        _gl.DeleteBuffer(_voxelPoolBuffer);
        _gl.DeleteVertexArray(_vao);
    }
}
