using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Conventional graphics pipeline backed by one linked OpenGL program and one DSA vertex array.
/// Descriptor layouts are retained for later binding validation; descriptor binding is implemented
/// independently from the program object and uses the common pipeline-layout contract.
/// </summary>
internal sealed class OpenGLGraphicsRenderPipeline : GraphicsDeviceObject, IGraphicsRenderPipeline, IOpenGLNativeResource
{
    private readonly OpenGLGraphicsDevice _device;
    private uint _program;
    private uint _vertexArray;

    private OpenGLGraphicsRenderPipeline(
        OpenGLGraphicsDevice device,
        GraphicsRenderPipelineDescription description,
        uint program,
        uint vertexArray) : base(device)
    {
        _device = device;
        Description = description;
        Layout = description.Layout;
        _program = program;
        _vertexArray = vertexArray;
    }

    public IGraphicsPipelineLayout Layout { get; }
    public GraphicsRenderPipelineDescription Description { get; }
    public string? DebugName => null;

    internal uint Program
    {
        get
        {
            ThrowIfDisposed();
            return _program;
        }
    }

    internal uint VertexArray
    {
        get
        {
            ThrowIfDisposed();
            return _vertexArray;
        }
    }

    internal PrimitiveType Topology => OpenGLPipelineMapping.ToPrimitiveType(Description.Topology);

    internal static OpenGLGraphicsRenderPipeline Create(
        OpenGLGraphicsDevice device,
        GraphicsRenderPipelineDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        description.Validate();

        if (description.Layout is not OpenGLGraphicsPipelineLayout layout ||
            !ReferenceEquals(layout.Device, device))
            throw new GraphicsValidationException("The pipeline layout must be created by this OpenGL device.");

        for (int i = 0; i < description.Shaders.Length; i++)
        {
            GraphicsShaderStage stage = description.Shaders[i].Description.Stage;
            if (stage is GraphicsShaderStage.Mesh or GraphicsShaderStage.Task)
                throw new GraphicsFeatureNotSupportedException(
                    GraphicsFeature.MeshShaders | GraphicsFeature.TaskShaders,
                    stage == GraphicsShaderStage.Mesh ? GraphicsFeature.MeshShaders : GraphicsFeature.TaskShaders);
            if (description.Shaders[i] is not OpenGLGraphicsShader)
                throw new GraphicsValidationException("All pipeline shaders must belong to this OpenGL backend.");
        }

        if (description.Rasterization.DepthBiasClamp != 0)
            throw new GraphicsValidationException(
                "Depth-bias clamping requires GL_ARB_polygon_offset_clamp, which is not enabled by this backend.");

        device.Capabilities.ValidateVertexInputLayout(
            description.VertexInput ?? new GraphicsVertexInputLayout(
                ReadOnlySpan<GraphicsVertexBufferLayout>.Empty,
                ReadOnlySpan<GraphicsVertexAttribute>.Empty));

        return device.WithContext(gl =>
        {
            uint program = LinkProgram(gl, description.Shaders);
            uint vertexArray = 0;
            try
            {
                device.DirectStateAccess.CreateVertexArrays(1, out vertexArray);
                if (vertexArray == 0)
                    throw new GraphicsException("OpenGL failed to create a vertex-array object.");

                ConfigureVertexArray(device.DirectStateAccess, vertexArray, description.VertexInput);
                OpenGLGraphicsRenderPipeline pipeline = new(device, description, program, vertexArray);
                device.RegisterUnderContext(pipeline);
                return pipeline;
            }
            catch
            {
                if (vertexArray != 0)
                    gl.DeleteVertexArray(vertexArray);
                gl.DeleteProgram(program);
                throw;
            }
        });
    }

    private static uint LinkProgram(GL gl, ReadOnlySpan<IGraphicsShader> shaders)
    {
        uint program = gl.CreateProgram();
        if (program == 0)
            throw new GraphicsException("OpenGL failed to create a shader program.");

        try
        {
            for (int i = 0; i < shaders.Length; i++)
                gl.AttachShader(program, ((OpenGLGraphicsShader)shaders[i]).Handle);

            gl.LinkProgram(program);
            gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
            if (linked == 0)
                throw new GraphicsException($"OpenGL shader program link failed:\n{gl.GetProgramInfoLog(program)}");

            for (int i = 0; i < shaders.Length; i++)
                gl.DetachShader(program, ((OpenGLGraphicsShader)shaders[i]).Handle);

            return program;
        }
        catch
        {
            gl.DeleteProgram(program);
            throw;
        }
    }

    private static void ConfigureVertexArray(
        ArbDirectStateAccess dsa,
        uint vertexArray,
        GraphicsVertexInputLayout? input)
    {
        if (input is null)
            return;

        ReadOnlySpan<GraphicsVertexBufferLayout> buffers = input.Buffers;
        for (int i = 0; i < buffers.Length; i++)
        {
            GraphicsVertexBufferLayout buffer = buffers[i];
            dsa.VertexArrayBindingDivisor(
                vertexArray,
                buffer.Binding,
                buffer.InputRate == GraphicsVertexInputRate.PerInstance ? 1u : 0u);
        }

        ReadOnlySpan<GraphicsVertexAttribute> attributes = input.Attributes;
        for (int i = 0; i < attributes.Length; i++)
        {
            GraphicsVertexAttribute attribute = attributes[i];
            if (!OpenGLPipelineMapping.TryGetVertexFormat(attribute.Format, out OpenGLVertexFormat format))
                throw new GraphicsValidationException(
                    $"OpenGL does not support {attribute.Format} as a vertex attribute.");

            dsa.EnableVertexArrayAttrib(vertexArray, attribute.Location);
            dsa.VertexArrayAttribBinding(vertexArray, attribute.Location, attribute.Binding);
            if (format.IsInteger)
            {
                dsa.VertexArrayAttribIFormat(
                    vertexArray, attribute.Location, format.ComponentCount,
                    format.IntegerType, attribute.Offset);
            }
            else
            {
                dsa.VertexArrayAttribFormat(
                    vertexArray, attribute.Location, format.ComponentCount,
                    format.Type, format.Normalized, attribute.Offset);
            }
        }
    }

    internal void Apply(GL gl)
    {
        ThrowIfDisposed();
        gl.UseProgram(_program);
        gl.BindVertexArray(_vertexArray);

        GraphicsRasterizationState rasterization = Description.Rasterization;
        gl.PolygonMode(TriangleFace.FrontAndBack, rasterization.PolygonMode switch
        {
            GraphicsPolygonMode.Fill => PolygonMode.Fill,
            GraphicsPolygonMode.Line => PolygonMode.Line,
            GraphicsPolygonMode.Point => PolygonMode.Point,
            _ => throw new ArgumentOutOfRangeException()
        });

        if (rasterization.CullMode == GraphicsCullMode.None)
        {
            gl.Disable(EnableCap.CullFace);
        }
        else
        {
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(rasterization.CullMode switch
            {
                GraphicsCullMode.Front => TriangleFace.Front,
                GraphicsCullMode.Back => TriangleFace.Back,
                GraphicsCullMode.FrontAndBack => TriangleFace.FrontAndBack,
                _ => throw new ArgumentOutOfRangeException()
            });
        }

        gl.FrontFace(rasterization.FrontFace == GraphicsFrontFace.Clockwise
            ? FrontFaceDirection.CW
            : FrontFaceDirection.Ccw);

        SetEnabled(gl, EnableCap.DepthClamp, rasterization.DepthClampEnabled);
        SetEnabled(gl, EnableCap.PolygonOffsetFill, rasterization.DepthBiasEnabled);
        if (rasterization.DepthBiasEnabled)
            gl.PolygonOffset(rasterization.DepthBiasSlopeFactor, rasterization.DepthBiasConstantFactor);
        gl.LineWidth(rasterization.LineWidth);

        ApplyDepthStencil(gl, Description.DepthStencil);
        ApplyBlend(gl, _device.DrawBuffersBlendApi, Description.Blend, Description.ColorFormats.Length);

        if (Description.Tessellation is { } tessellation)
            gl.PatchParameter(PatchParameterName.Vertices, checked((int)tessellation.PatchControlPoints));
    }

    private static void ApplyDepthStencil(GL gl, GraphicsDepthStencilState state)
    {
        SetEnabled(gl, EnableCap.DepthTest, state.DepthTestEnabled);
        gl.DepthMask(state.DepthWriteEnabled);
        gl.DepthFunc(OpenGLPipelineMapping.ToDepthFunction(state.DepthCompare));

        SetEnabled(gl, EnableCap.StencilTest, state.StencilTestEnabled);
        if (state.StencilTestEnabled)
        {
            ApplyStencilFace(gl, TriangleFace.Front, state.FrontStencil);
            ApplyStencilFace(gl, TriangleFace.Back, state.BackStencil);
        }
    }

    private static void ApplyStencilFace(GL gl, TriangleFace face, GraphicsStencilFaceState state)
    {
        gl.StencilOpSeparate(
            face,
            OpenGLPipelineMapping.ToStencilOperation(state.Fail),
            OpenGLPipelineMapping.ToStencilOperation(state.DepthFail),
            OpenGLPipelineMapping.ToStencilOperation(state.Pass));
        gl.StencilFuncSeparate(
            (GLEnum)face,
            (GLEnum)OpenGLPipelineMapping.ToDepthFunction(state.Compare),
            unchecked((int)state.Reference),
            state.CompareMask);
        gl.StencilMaskSeparate(face, state.WriteMask);
    }

    private static void ApplyBlend(
        GL gl,
        ArbDrawBuffersBlend drawBuffersBlend,
        GraphicsBlendState blend,
        int colorTargetCount)
    {
        if (colorTargetCount == 0)
            return;

        ReadOnlySpan<GraphicsBlendAttachmentState> attachments = blend.Attachments;
        GraphicsBlendAttachmentState first = attachments.IsEmpty
            ? new GraphicsBlendAttachmentState(
                false,
                GraphicsBlendFactor.One,
                GraphicsBlendFactor.Zero,
                GraphicsBlendOperation.Add,
                GraphicsBlendFactor.One,
                GraphicsBlendFactor.Zero,
                GraphicsBlendOperation.Add,
                GraphicsColorWriteMask.All)
            : attachments[0];

        // GL 4.5 provides indexed blend factors/equations, but Silk.NET 2.23.0 exposes the
        // indexed enable and color-mask operations only through the EXT_draw_buffers2 wrapper
        // in its Legacy package. This backend does not mix OpenGL binding assemblies. Therefore
        // enable state and write mask are required to be common across draw buffers; blend
        // factors/equations remain independently configured through ARB_draw_buffers_blend.
        for (int i = 1; i < colorTargetCount; i++)
        {
            GraphicsBlendAttachmentState current = attachments.IsEmpty
                ? first
                : attachments[i];
            if (current.BlendEnabled != first.BlendEnabled || current.WriteMask != first.WriteMask)
                throw new GraphicsValidationException(
                    "OpenGL 4.5 backend currently requires identical blend-enable and color-write-mask state for every color attachment.");
        }

        SetEnabled(gl, EnableCap.Blend, first.BlendEnabled);
        GraphicsColorWriteMask mask = first.WriteMask;
        gl.ColorMask(
            (mask & GraphicsColorWriteMask.Red) != 0,
            (mask & GraphicsColorWriteMask.Green) != 0,
            (mask & GraphicsColorWriteMask.Blue) != 0,
            (mask & GraphicsColorWriteMask.Alpha) != 0);

        for (int i = 0; i < colorTargetCount; i++)
        {
            GraphicsBlendAttachmentState state = attachments.IsEmpty ? first : attachments[i];
            drawBuffersBlend.BlendFuncSeparate(
                (uint)i,
                OpenGLPipelineMapping.ToBlendFactor(state.SourceColor),
                OpenGLPipelineMapping.ToBlendFactor(state.DestinationColor),
                OpenGLPipelineMapping.ToBlendFactor(state.SourceAlpha),
                OpenGLPipelineMapping.ToBlendFactor(state.DestinationAlpha));
            drawBuffersBlend.BlendEquationSeparate(
                (uint)i,
                OpenGLPipelineMapping.ToBlendEquation(state.ColorOperation),
                OpenGLPipelineMapping.ToBlendEquation(state.AlphaOperation));
        }
    }

    private static void SetEnabled(GL gl, EnableCap cap, bool enabled)
    {
        if (enabled)
            gl.Enable(cap);
        else
            gl.Disable(cap);
    }

    protected override void DisposeCore() => _device.ReleaseResource(this);

    public void ReleaseNative(GL gl)
    {
        uint vertexArray = Interlocked.Exchange(ref _vertexArray, 0);
        uint program = Interlocked.Exchange(ref _program, 0);
        if (vertexArray != 0)
            gl.DeleteVertexArray(vertexArray);
        if (program != 0)
            gl.DeleteProgram(program);
    }
}
