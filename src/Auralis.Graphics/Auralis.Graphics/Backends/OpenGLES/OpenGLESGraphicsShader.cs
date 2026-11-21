using System.Text;
using System.Text.RegularExpressions;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>Compiled GLSL ES shader. Desktop GLSL and SPIR-V are deliberately rejected.</summary>
internal sealed class OpenGLESGraphicsShader : OpenGLESNativeDeviceObject, IGraphicsShader
{
    private readonly OpenGLESGraphicsDevice _device;
    private uint _handle;

    private OpenGLESGraphicsShader(
        OpenGLESGraphicsDevice device,
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
            uint value = Volatile.Read(ref _handle);
            return value != 0 ? value : throw new ObjectDisposedException(nameof(OpenGLESGraphicsShader));
        }
    }

    internal static OpenGLESGraphicsShader CreateUnderContext(
        OpenGLESGraphicsDevice device,
        GL gl,
        in GraphicsShaderDescription description)
    {
        GraphicsShaderDescription copy = description;
        copy.Validate();

        if (copy.Format != GraphicsShaderFormat.GlslSource)
            throw new GraphicsValidationException(
                $"OpenGL ES accepts GLSL ES source only; shader format '{copy.Format.Identifier}' is unsupported. SPIR-V ingestion is not assumed.");

        if (!string.Equals(copy.EntryPoint, "main", StringComparison.Ordinal))
            throw new GraphicsValidationException("GLSL ES shaders must use the 'main' entry point.");

        if (!copy.SpecializationConstants.IsEmpty)
            throw new GraphicsValidationException("GLSL ES source does not support SPIR-V specialization constants.");

        ShaderType stage = GetShaderType(device, copy.Stage);
        string source;
        try
        {
            source = new UTF8Encoding(false, true).GetString(copy.Code.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new GraphicsValidationException($"GLSL ES source is not valid UTF-8: {exception.Message}");
        }

        if (source.IndexOf('\0') >= 0)
            throw new GraphicsValidationException("GLSL ES source cannot contain embedded null characters.");

        ValidateGlslEsVersion(device, copy.Stage, source);

        uint handle = gl.CreateShader(stage);
        if (handle == 0)
            throw new GraphicsException("OpenGL ES failed to create a shader object.");

        try
        {
            gl.ShaderSource(handle, source);
            gl.CompileShader(handle);
            gl.GetShader(handle, GLEnum.CompileStatus, out int status);
            if (status == 0)
            {
                string diagnostic = gl.GetShaderInfoLog(handle);
                throw new GraphicsException(
                    $"OpenGL ES {device.Version} {copy.Stage} shader compilation failed:{Environment.NewLine}{diagnostic}");
            }

            OpenGLESGraphicsShader shader = new(device, copy, handle);
            device.RegisterUnderContext(shader);
            return shader;
        }
        catch
        {
            gl.DeleteShader(handle);
            throw;
        }
    }

    internal static ShaderType GetShaderType(OpenGLESGraphicsDevice device, GraphicsShaderStage stage)
    {
        return stage switch
        {
            GraphicsShaderStage.Vertex => ShaderType.VertexShader,
            GraphicsShaderStage.Fragment => ShaderType.FragmentShader,
            GraphicsShaderStage.Compute when device.Version.IsAtLeast(3, 1) => ShaderType.ComputeShader,
            GraphicsShaderStage.Geometry when device.Version.IsAtLeast(3, 2) => ShaderType.GeometryShader,
            GraphicsShaderStage.TessellationControl when device.Version.IsAtLeast(3, 2) => ShaderType.TessControlShader,
            GraphicsShaderStage.TessellationEvaluation when device.Version.IsAtLeast(3, 2) => ShaderType.TessEvaluationShader,
            GraphicsShaderStage.Compute =>
                throw new GraphicsFeatureNotSupportedException(GraphicsFeature.ComputeShaders, GraphicsFeature.ComputeShaders),
            GraphicsShaderStage.Geometry =>
                throw new GraphicsFeatureNotSupportedException(GraphicsFeature.GeometryShaders, GraphicsFeature.GeometryShaders),
            GraphicsShaderStage.TessellationControl or GraphicsShaderStage.TessellationEvaluation =>
                throw new GraphicsFeatureNotSupportedException(GraphicsFeature.TessellationShaders, GraphicsFeature.TessellationShaders),
            GraphicsShaderStage.Mesh =>
                throw new GraphicsFeatureNotSupportedException(GraphicsFeature.MeshShaders, GraphicsFeature.MeshShaders),
            GraphicsShaderStage.Task =>
                throw new GraphicsFeatureNotSupportedException(GraphicsFeature.TaskShaders, GraphicsFeature.TaskShaders),
            _ => throw new GraphicsValidationException($"Unsupported OpenGL ES shader stage: {stage}.")
        };
    }

    private static void ValidateGlslEsVersion(
        OpenGLESGraphicsDevice device,
        GraphicsShaderStage stage,
        string source)
    {
        Match match = Regex.Match(
            source,
            @"^\s*#\s*version\s+(?<version>100|300|310|320)\s+es\b",
            RegexOptions.CultureInvariant);

        if (!match.Success)
            throw new GraphicsValidationException(
                "GLSL ES source must begin with an explicit '#version 300 es', '#version 310 es', or '#version 320 es' directive.");

        int languageVersion = int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (languageVersion == 100)
            throw new GraphicsValidationException("GLSL ES 1.00 is not supported; the backend requires GLSL ES 3.00 or newer.");

        if (languageVersion >= 310 && !device.Version.IsAtLeast(3, 1))
            throw new GraphicsValidationException("GLSL ES 3.10 requires an OpenGL ES 3.1 context.");
        if (languageVersion >= 320 && !device.Version.IsAtLeast(3, 2))
            throw new GraphicsValidationException("GLSL ES 3.20 requires an OpenGL ES 3.2 context.");

        if (stage is GraphicsShaderStage.Compute && languageVersion < 310)
            throw new GraphicsValidationException("Compute shaders require '#version 310 es' or newer.");
        if (stage is GraphicsShaderStage.Geometry or GraphicsShaderStage.TessellationControl or GraphicsShaderStage.TessellationEvaluation &&
            languageVersion < 320)
            throw new GraphicsValidationException("Geometry and tessellation shaders require '#version 320 es'.");
    }

    protected override void DisposeCore() => ReleaseOwnerReference();

    public override void ReleaseNative(GL gl)
    {
        uint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
            gl.DeleteShader(handle);
    }
}
