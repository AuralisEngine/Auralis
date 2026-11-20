using System.Text;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>A compiled GLSL shader object.</summary>
internal sealed class OpenGLGraphicsShader : GraphicsDeviceObject, IGraphicsShader, IOpenGLNativeResource
{
    private readonly OpenGLGraphicsDevice _device;
    private uint _handle;

    internal OpenGLGraphicsShader(
        OpenGLGraphicsDevice device,
        in GraphicsShaderDescription description,
        uint handle) : base(device)
    {
        _device = device;
        Description = description;
        _handle = handle;
    }

    public GraphicsShaderDescription Description { get; }

    public string? DebugName => null;

    internal uint Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    protected override void DisposeCore() => _device.WithContext(ReleaseNative);

    public void ReleaseNative(GL gl)
    {
        uint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
            gl.DeleteShader(handle);
    }

    internal static ShaderType ToShaderType(GraphicsShaderStage stage) => stage switch
    {
        GraphicsShaderStage.Vertex => ShaderType.VertexShader,
        GraphicsShaderStage.Fragment => ShaderType.FragmentShader,
        GraphicsShaderStage.Geometry => ShaderType.GeometryShader,
        GraphicsShaderStage.Compute => ShaderType.ComputeShader,
        GraphicsShaderStage.TessellationControl => ShaderType.TessControlShader,
        GraphicsShaderStage.TessellationEvaluation => ShaderType.TessEvaluationShader,
        _ => throw new GraphicsValidationException($"OpenGL 4.5 does not support the requested shader stage: {stage}.")
    };

    internal static OpenGLGraphicsShader Create(OpenGLGraphicsDevice device, in GraphicsShaderDescription description)
    {
        description.Validate();
        if (description.Format != GraphicsShaderFormat.GlslSource)
            throw new GraphicsValidationException("The OpenGL backend accepts GLSL source only.");
        if (!string.Equals(description.EntryPoint, "main", StringComparison.Ordinal))
            throw new GraphicsValidationException("GLSL shaders must use the main entry point.");

        string source = Encoding.UTF8.GetString(description.Code.Span);
        if (source.IndexOf('\0') >= 0)
            throw new GraphicsValidationException("GLSL source cannot contain embedded null characters.");

        GraphicsShaderStage stage = description.Stage;
        uint shader = 0;
        try
        {
            shader = device.WithContext(gl =>
            {
                uint handle = gl.CreateShader(ToShaderType(stage));
                gl.ShaderSource(handle, source);
                gl.CompileShader(handle);
                gl.GetShader(handle, ShaderParameterName.CompileStatus, out int status);
                if (status == 0)
                {
                    string log = gl.GetShaderInfoLog(handle);
                    gl.DeleteShader(handle);
                    throw new GraphicsException($"OpenGL GLSL compilation failed ({description.Stage}):\n{log}");
                }

                return handle;
            });

            return new OpenGLGraphicsShader(device, description, shader);
        }
        catch
        {
            if (shader != 0)
                device.WithContext(gl => gl.DeleteShader(shader));
            throw;
        }
    }
}
