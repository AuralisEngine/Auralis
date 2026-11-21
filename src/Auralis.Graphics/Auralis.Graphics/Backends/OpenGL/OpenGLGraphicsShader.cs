using System.Buffers;
using System.Text;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>A compiled GLSL source or specialized SPIR-V shader object.</summary>
internal sealed unsafe class OpenGLGraphicsShader : GraphicsDeviceObject, IGraphicsShader, IOpenGLNativeResource
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

    protected override void DisposeCore()
    {
        if (_device.IsDisposed)
            return;

        _device.ReleaseResource(this);
    }

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
        GraphicsShaderStage.Mesh => (ShaderType)Silk.NET.OpenGL.Extensions.EXT.EXT.MeshShaderExt,
        GraphicsShaderStage.Task => (ShaderType)Silk.NET.OpenGL.Extensions.EXT.EXT.TaskShaderExt,
        _ => throw new GraphicsValidationException($"OpenGL does not support the requested shader stage: {stage}.")
    };

    internal static OpenGLGraphicsShader Create(OpenGLGraphicsDevice device, in GraphicsShaderDescription description)
    {
        description.Validate();
        bool isGlsl = description.Format == GraphicsShaderFormat.GlslSource;
        bool isSpirV = description.Format == GraphicsShaderFormat.SpirV;
        if (!isGlsl && !isSpirV)
            throw new GraphicsValidationException(
                $"The OpenGL backend does not accept shader format '{description.Format.Identifier}'.");

        if (isGlsl && !string.Equals(description.EntryPoint, "main", StringComparison.Ordinal))
            throw new GraphicsValidationException("GLSL shaders must use the main entry point.");
        if (isGlsl && !description.SpecializationConstants.IsEmpty)
            throw new GraphicsValidationException("GLSL shader source does not accept SPIR-V specialization constants.");
        if (isSpirV)
            OpenGLSpirVValidation.Validate(description, device.SpirVApi is not null);
        if (description.Stage is GraphicsShaderStage.Mesh or GraphicsShaderStage.Task &&
            device.MeshShaderApi is null)
        {
            GraphicsFeature required = description.Stage == GraphicsShaderStage.Mesh
                ? GraphicsFeature.MeshShaders
                : GraphicsFeature.TaskShaders;
            throw new GraphicsFeatureNotSupportedException(required, required);
        }

        string? source = null;
        if (isGlsl)
        {
            source = Encoding.UTF8.GetString(description.Code.Span);
            if (source.IndexOf('\0') >= 0)
                throw new GraphicsValidationException("GLSL source cannot contain embedded null characters.");
        }

        GraphicsShaderDescription shaderDescription = description;
        GraphicsShaderStage stage = shaderDescription.Stage;
        return device.WithContext(gl =>
        {
            uint handle = gl.CreateShader(ToShaderType(stage));
            try
            {
                if (isGlsl)
                {
                    gl.ShaderSource(handle, source!);
                    gl.CompileShader(handle);
                }
                else
                {
                    SpecializeSpirV(device, handle, shaderDescription);
                }

                gl.GetShader(handle, ShaderParameterName.CompileStatus, out int status);
                if (status == 0)
                {
                    string log = gl.GetShaderInfoLog(handle);
                    throw new GraphicsException(
                        $"OpenGL {(isGlsl ? "GLSL compilation" : "SPIR-V specialization")} failed ({stage}):\n{log}");
                }

                device.LabelObject(ObjectIdentifier.Shader, handle, $"Auralis {stage} Shader");
                OpenGLGraphicsShader resource = new(device, shaderDescription, handle);
                device.RegisterUnderContext(resource);
                return resource;
            }
            catch
            {
                gl.DeleteShader(handle);
                throw;
            }
        });
    }

    private static void SpecializeSpirV(
        OpenGLGraphicsDevice device,
        uint shader,
        in GraphicsShaderDescription description)
    {
        using MemoryHandle codePin = description.Code.Pin();
        if (codePin.Pointer == null)
            throw new GraphicsValidationException("The SPIR-V code buffer could not be pinned.");

        uint shaderHandle = shader;
        device.ShaderBinaryApi.ShaderBinary(
            1,
            &shaderHandle,
            ARB.ShaderBinaryFormatSpirVArb,
            codePin.Pointer,
            checked((uint)description.Code.Length));

        ReadOnlySpan<GraphicsShaderSpecializationConstant> constants =
            description.SpecializationConstants.Span;
        int count = constants.Length;

        uint[]? rentedIds = null;
        uint[]? rentedValues = null;
        Span<uint> ids = count <= 16
            ? stackalloc uint[count]
            : (rentedIds = ArrayPool<uint>.Shared.Rent(count)).AsSpan(0, count);
        Span<uint> values = count <= 16
            ? stackalloc uint[count]
            : (rentedValues = ArrayPool<uint>.Shared.Rent(count)).AsSpan(0, count);

        int entryPointLength = Encoding.UTF8.GetByteCount(description.EntryPoint);
        byte[]? rentedEntryPoint = null;
        Span<byte> entryPointBytes = entryPointLength < 255
            ? stackalloc byte[entryPointLength + 1]
            : (rentedEntryPoint = ArrayPool<byte>.Shared.Rent(entryPointLength + 1)).AsSpan(0, entryPointLength + 1);

        try
        {
            for (int i = 0; i < count; i++)
            {
                ids[i] = constants[i].ConstantId;
                values[i] = constants[i].Value;
            }

            Encoding.UTF8.GetBytes(description.EntryPoint.AsSpan(), entryPointBytes[..entryPointLength]);
            entryPointBytes[entryPointLength] = 0;

            fixed (byte* entryPointPointer = entryPointBytes)
            fixed (uint* idPointer = ids)
            fixed (uint* valuePointer = values)
            {
                device.SpirVApi!.SpecializeShader(
                    shader,
                    entryPointPointer,
                    checked((uint)count),
                    count == 0 ? null : idPointer,
                    count == 0 ? null : valuePointer);
            }
        }
        finally
        {
            if (rentedEntryPoint is not null)
                ArrayPool<byte>.Shared.Return(rentedEntryPoint, clearArray: true);
            if (rentedIds is not null)
                ArrayPool<uint>.Shared.Return(rentedIds, clearArray: true);
            if (rentedValues is not null)
                ArrayPool<uint>.Shared.Return(rentedValues, clearArray: true);
        }
    }
}
