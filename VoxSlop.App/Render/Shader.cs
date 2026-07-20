using Silk.NET.OpenGL;

namespace VoxSlop.App.Render;

/// <summary>Compiles and links a vertex/fragment pair, with cached uniform lookups.</summary>
public sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _uniforms = [];

    public uint Handle { get; }

    private Shader(GL gl, uint handle)
    {
        _gl = gl;
        Handle = handle;
    }

    public static Shader FromSource(GL gl, string vertexSource, string fragmentSource)
    {
        uint vs = Compile(gl, ShaderType.VertexShader, vertexSource);
        uint fs = Compile(gl, ShaderType.FragmentShader, fragmentSource);

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            throw new InvalidOperationException($"Shader link failed:\n{log}");
        }

        gl.DetachShader(program, vs);
        gl.DetachShader(program, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        return new Shader(gl, program);
    }

    private static uint Compile(GL gl, ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compile failed:\n{log}");
        }

        return shader;
    }

    public void Use() => _gl.UseProgram(Handle);

    private int Location(string name)
    {
        if (!_uniforms.TryGetValue(name, out int loc))
        {
            loc = _gl.GetUniformLocation(Handle, name);
            _uniforms[name] = loc;
        }
        return loc;
    }

    // Silently ignore uniforms the compiler optimised away — handy while iterating on GLSL.
    public void Set(string name, int value)
    {
        int l = Location(name);
        if (l >= 0) _gl.Uniform1(l, value);
    }

    public void Set(string name, float value)
    {
        int l = Location(name);
        if (l >= 0) _gl.Uniform1(l, value);
    }

    public void Set(string name, float x, float y)
    {
        int l = Location(name);
        if (l >= 0) _gl.Uniform2(l, x, y);
    }

    public void Set(string name, System.Numerics.Vector3 v)
    {
        int l = Location(name);
        if (l >= 0) _gl.Uniform3(l, v.X, v.Y, v.Z);
    }

    public void SetIVec3(string name, int x, int y, int z)
    {
        int l = Location(name);
        if (l >= 0) _gl.Uniform3(l, x, y, z);
    }

    public void Dispose() => _gl.DeleteProgram(Handle);
}
