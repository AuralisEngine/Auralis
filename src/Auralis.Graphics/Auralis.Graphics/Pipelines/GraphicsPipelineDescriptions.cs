using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Pipelines;

[Flags]
public enum GraphicsShaderStages
{
    None = 0,
    Vertex = 1 << 0,
    Fragment = 1 << 1,
    Compute = 1 << 2,
    Geometry = 1 << 3,
    TessellationControl = 1 << 4,
    TessellationEvaluation = 1 << 5,
    Task = 1 << 6,
    Mesh = 1 << 7,
    AllGraphics = Vertex | Fragment | Geometry | TessellationControl | TessellationEvaluation | Task | Mesh,
    All = AllGraphics | Compute
}

public enum GraphicsDescriptorType
{
    UniformBuffer,
    StorageBuffer,
    SampledTexture,
    StorageTexture,
    Sampler,
    CombinedTextureSampler
}

[Flags]
public enum GraphicsBindingFlags
{
    None = 0,
    PartiallyBound = 1 << 0,
    UpdateAfterBind = 1 << 1,
    VariableCount = 1 << 2
}

public readonly record struct GraphicsBindingLayoutEntry(
    uint Binding,
    GraphicsDescriptorType Type,
    uint Count,
    GraphicsShaderStages Stages,
    GraphicsBindingFlags Flags = GraphicsBindingFlags.None);

/// <summary>Immutable binding layout description. Entries are copied during construction.</summary>
public sealed class GraphicsBindingLayoutDescription
{
    private readonly GraphicsBindingLayoutEntry[] _entries;

    public ReadOnlySpan<GraphicsBindingLayoutEntry> Entries => _entries;

    public GraphicsBindingLayoutDescription(ReadOnlySpan<GraphicsBindingLayoutEntry> entries)
    {
        _entries = entries.ToArray();
        Validate();
    }

    public void Validate()
    {
        const GraphicsShaderStages allStages = GraphicsShaderStages.All;
        const GraphicsBindingFlags allFlags =
            GraphicsBindingFlags.PartiallyBound | GraphicsBindingFlags.UpdateAfterBind |
            GraphicsBindingFlags.VariableCount;

        for (int i = 0; i < _entries.Length; i++)
        {
            GraphicsBindingLayoutEntry entry = _entries[i];
            if (!Enum.IsDefined(entry.Type))
                throw new ArgumentOutOfRangeException(nameof(Entries), "A descriptor entry has an unknown type.");
            if (entry.Count == 0)
                throw new ArgumentOutOfRangeException(nameof(Entries), "Descriptor count must be greater than zero.");
            if (entry.Stages == GraphicsShaderStages.None || (entry.Stages & ~allStages) != 0)
                throw new ArgumentOutOfRangeException(nameof(Entries), "Descriptor shader stages are invalid.");
            if ((entry.Flags & ~allFlags) != 0)
                throw new ArgumentOutOfRangeException(nameof(Entries), "Descriptor binding flags are invalid.");
            if ((entry.Flags & GraphicsBindingFlags.VariableCount) != 0 && i != _entries.Length - 1)
                throw new ArgumentException("A variable-count descriptor binding must be the final entry.", nameof(Entries));

            for (int j = 0; j < i; j++)
            {
                if (_entries[j].Binding == entry.Binding)
                    throw new ArgumentException($"Binding {entry.Binding} is declared more than once.", nameof(Entries));
            }
        }
    }
}

public enum GraphicsVertexInputRate
{
    PerVertex,
    PerInstance
}

public readonly record struct GraphicsVertexBufferLayout(
    uint Binding,
    uint Stride,
    GraphicsVertexInputRate InputRate = GraphicsVertexInputRate.PerVertex);

public readonly record struct GraphicsVertexAttribute(
    uint Location,
    uint Binding,
    GraphicsFormat Format,
    uint Offset);

/// <summary>Immutable vertex input layout with validation of binding references and locations.</summary>
public sealed class GraphicsVertexInputLayout
{
    private readonly GraphicsVertexBufferLayout[] _buffers;
    private readonly GraphicsVertexAttribute[] _attributes;

    public ReadOnlySpan<GraphicsVertexBufferLayout> Buffers => _buffers;
    public ReadOnlySpan<GraphicsVertexAttribute> Attributes => _attributes;

    public GraphicsVertexInputLayout(
        ReadOnlySpan<GraphicsVertexBufferLayout> buffers,
        ReadOnlySpan<GraphicsVertexAttribute> attributes)
    {
        _buffers = buffers.ToArray();
        _attributes = attributes.ToArray();
        Validate();
    }

    public void Validate()
    {
        for (int i = 0; i < _buffers.Length; i++)
        {
            if (_buffers[i].Stride == 0)
                throw new ArgumentOutOfRangeException(nameof(Buffers), "Vertex buffer stride must be greater than zero.");
            if (!Enum.IsDefined(_buffers[i].InputRate))
                throw new ArgumentOutOfRangeException(nameof(Buffers));
            for (int j = 0; j < i; j++)
            {
                if (_buffers[j].Binding == _buffers[i].Binding)
                    throw new ArgumentException($"Vertex binding {_buffers[i].Binding} is duplicated.");
            }
        }

        for (int i = 0; i < _attributes.Length; i++)
        {
            GraphicsVertexAttribute attribute = _attributes[i];
            if (attribute.Format == GraphicsFormat.Unknown || !Enum.IsDefined(attribute.Format))
                throw new ArgumentOutOfRangeException(nameof(Attributes), "Vertex attributes require a concrete format.");

            bool bindingFound = false;
            for (int j = 0; j < _buffers.Length; j++)
                bindingFound |= _buffers[j].Binding == attribute.Binding;

            if (!bindingFound)
                throw new ArgumentException($"Vertex attribute {attribute.Location} refers to missing binding {attribute.Binding}.");

            for (int j = 0; j < i; j++)
            {
                if (_attributes[j].Location == attribute.Location)
                    throw new ArgumentException($"Vertex attribute location {attribute.Location} is duplicated.");
            }
        }
    }
}

public enum GraphicsPrimitiveTopology
{
    PointList,
    LineList,
    LineStrip,
    TriangleList,
    TriangleStrip,
    TriangleFan,
    LineListAdjacency,
    LineStripAdjacency,
    TriangleListAdjacency,
    TriangleStripAdjacency,
    PatchList
}

public enum GraphicsPolygonMode
{
    Fill,
    Line,
    Point
}

public enum GraphicsCullMode
{
    None,
    Front,
    Back,
    FrontAndBack
}

public enum GraphicsFrontFace
{
    CounterClockwise,
    Clockwise
}

/// <summary>Rasterization options shared by the common API. Some options require optional capabilities.</summary>
public sealed record GraphicsRasterizationState
{
    public GraphicsPolygonMode PolygonMode { get; init; } = GraphicsPolygonMode.Fill;
    public GraphicsCullMode CullMode { get; init; } = GraphicsCullMode.Back;
    public GraphicsFrontFace FrontFace { get; init; } = GraphicsFrontFace.CounterClockwise;
    public bool DepthClampEnabled { get; init; }
    public bool DepthBiasEnabled { get; init; }
    public float DepthBiasConstantFactor { get; init; }
    public float DepthBiasClamp { get; init; }
    public float DepthBiasSlopeFactor { get; init; }
    public float LineWidth { get; init; } = 1;

    public void Validate()
    {
        if (!Enum.IsDefined(PolygonMode) || !Enum.IsDefined(CullMode) || !Enum.IsDefined(FrontFace))
            throw new ArgumentOutOfRangeException(nameof(PolygonMode));
        if (!float.IsFinite(DepthBiasConstantFactor) || !float.IsFinite(DepthBiasClamp) ||
            !float.IsFinite(DepthBiasSlopeFactor) || !float.IsFinite(LineWidth) || LineWidth <= 0)
            throw new ArgumentException("Rasterization values must be finite and line width must be positive.");
    }
}

public enum GraphicsStencilOperation
{
    Keep,
    Zero,
    Replace,
    IncrementClamp,
    DecrementClamp,
    Invert,
    IncrementWrap,
    DecrementWrap
}

public readonly record struct GraphicsStencilFaceState(
    GraphicsStencilOperation Fail,
    GraphicsStencilOperation Pass,
    GraphicsStencilOperation DepthFail,
    GraphicsCompareOperation Compare,
    uint CompareMask,
    uint WriteMask,
    uint Reference);

public sealed record GraphicsDepthStencilState
{
    public bool DepthTestEnabled { get; init; }
    public bool DepthWriteEnabled { get; init; }
    public GraphicsCompareOperation DepthCompare { get; init; } = GraphicsCompareOperation.LessEqual;
    public bool StencilTestEnabled { get; init; }
    public GraphicsStencilFaceState FrontStencil { get; init; }
    public GraphicsStencilFaceState BackStencil { get; init; }
    public bool DepthBoundsTestEnabled { get; init; }
    public float MinimumDepthBounds { get; init; }
    public float MaximumDepthBounds { get; init; } = 1;

    public void Validate()
    {
        if (!Enum.IsDefined(DepthCompare))
            throw new ArgumentOutOfRangeException(nameof(DepthCompare));
        if (!float.IsFinite(MinimumDepthBounds) || !float.IsFinite(MaximumDepthBounds) ||
            MinimumDepthBounds < 0 || MaximumDepthBounds > 1 || MinimumDepthBounds > MaximumDepthBounds)
            throw new ArgumentException("Depth bounds must be finite, ordered, and within [0, 1].");
        ValidateStencilFace(FrontStencil);
        ValidateStencilFace(BackStencil);
    }

    private static void ValidateStencilFace(GraphicsStencilFaceState face)
    {
        if (!Enum.IsDefined(face.Fail) || !Enum.IsDefined(face.Pass) ||
            !Enum.IsDefined(face.DepthFail) || !Enum.IsDefined(face.Compare))
            throw new ArgumentOutOfRangeException(nameof(FrontStencil), "Stencil operations are invalid.");
    }
}

[Flags]
public enum GraphicsColorWriteMask
{
    None = 0,
    Red = 1 << 0,
    Green = 1 << 1,
    Blue = 1 << 2,
    Alpha = 1 << 3,
    All = Red | Green | Blue | Alpha
}

public enum GraphicsBlendFactor
{
    Zero,
    One,
    SourceColor,
    OneMinusSourceColor,
    DestinationColor,
    OneMinusDestinationColor,
    SourceAlpha,
    OneMinusSourceAlpha,
    DestinationAlpha,
    OneMinusDestinationAlpha,
    ConstantColor,
    OneMinusConstantColor,
    ConstantAlpha,
    OneMinusConstantAlpha,
    SourceAlphaSaturate
}

public enum GraphicsBlendOperation
{
    Add,
    Subtract,
    ReverseSubtract,
    Minimum,
    Maximum
}

public readonly record struct GraphicsBlendAttachmentState(
    bool BlendEnabled,
    GraphicsBlendFactor SourceColor,
    GraphicsBlendFactor DestinationColor,
    GraphicsBlendOperation ColorOperation,
    GraphicsBlendFactor SourceAlpha,
    GraphicsBlendFactor DestinationAlpha,
    GraphicsBlendOperation AlphaOperation,
    GraphicsColorWriteMask WriteMask = GraphicsColorWriteMask.All);

/// <summary>Blend state. An empty attachment list means backend-default opaque writes for each target.</summary>
public sealed class GraphicsBlendState
{
    private readonly GraphicsBlendAttachmentState[] _attachments;

    public ReadOnlySpan<GraphicsBlendAttachmentState> Attachments => _attachments;

    public GraphicsBlendState() => _attachments = [];

    public GraphicsBlendState(ReadOnlySpan<GraphicsBlendAttachmentState> attachments)
    {
        _attachments = attachments.ToArray();
        Validate();
    }

    public void Validate()
    {
        const GraphicsColorWriteMask allMasks = GraphicsColorWriteMask.All;

        for (int i = 0; i < _attachments.Length; i++)
        {
            GraphicsBlendAttachmentState attachment = _attachments[i];
            if (!Enum.IsDefined(attachment.SourceColor) || !Enum.IsDefined(attachment.DestinationColor) ||
                !Enum.IsDefined(attachment.ColorOperation) || !Enum.IsDefined(attachment.SourceAlpha) ||
                !Enum.IsDefined(attachment.DestinationAlpha) || !Enum.IsDefined(attachment.AlphaOperation))
                throw new ArgumentOutOfRangeException(nameof(Attachments));
            if ((attachment.WriteMask & ~allMasks) != 0)
                throw new ArgumentOutOfRangeException(nameof(Attachments));
        }
    }
}

/// <summary>Complete fixed-function state for a render pipeline.</summary>
public sealed class GraphicsRenderPipelineDescription
{
    private readonly GraphicsFormat[] _colorFormats;

    public GraphicsRenderPipelineDescription(
        IGraphicsPipelineLayout layout,
        IGraphicsShader vertexShader,
        IGraphicsShader? fragmentShader,
        GraphicsPrimitiveTopology topology,
        ReadOnlySpan<GraphicsFormat> colorFormats,
        GraphicsFormat depthStencilFormat = GraphicsFormat.Unknown,
        uint sampleCount = 1,
        GraphicsVertexInputLayout? vertexInput = null,
        GraphicsRasterizationState? rasterization = null,
        GraphicsDepthStencilState? depthStencil = null,
        GraphicsBlendState? blend = null)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        VertexShader = vertexShader ?? throw new ArgumentNullException(nameof(vertexShader));
        FragmentShader = fragmentShader;
        Topology = topology;
        _colorFormats = colorFormats.ToArray();
        DepthStencilFormat = depthStencilFormat;
        SampleCount = sampleCount;
        VertexInput = vertexInput;
        Rasterization = rasterization ?? new GraphicsRasterizationState();
        DepthStencil = depthStencil ?? new GraphicsDepthStencilState();
        Blend = blend ?? new GraphicsBlendState();
        Validate();
    }

    public IGraphicsPipelineLayout Layout { get; }
    public IGraphicsShader VertexShader { get; }
    public IGraphicsShader? FragmentShader { get; }
    public GraphicsPrimitiveTopology Topology { get; }
    public ReadOnlySpan<GraphicsFormat> ColorFormats => _colorFormats;
    public GraphicsFormat DepthStencilFormat { get; }
    public uint SampleCount { get; }
    public GraphicsVertexInputLayout? VertexInput { get; }
    public GraphicsRasterizationState Rasterization { get; }
    public GraphicsDepthStencilState DepthStencil { get; }
    public GraphicsBlendState Blend { get; }

    public void Validate()
    {
        if (!ReferenceEquals(Layout.Device, VertexShader.Device) ||
            (FragmentShader is not null && !ReferenceEquals(Layout.Device, FragmentShader.Device)))
            throw new ArgumentException("Pipeline layout and shaders must belong to the same graphics device.");

        if (VertexShader.Description.Stage != GraphicsShaderStage.Vertex)
            throw new ArgumentException("The render pipeline vertex shader must use the Vertex stage.", nameof(VertexShader));
        if (FragmentShader is not null && FragmentShader.Description.Stage != GraphicsShaderStage.Fragment)
            throw new ArgumentException("The render pipeline fragment shader must use the Fragment stage.", nameof(FragmentShader));
        if (!Enum.IsDefined(Topology))
            throw new ArgumentOutOfRangeException(nameof(Topology));
        if (SampleCount is 0 or > 64 || (SampleCount & (SampleCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(SampleCount));
        if (_colorFormats.Length == 0 && DepthStencilFormat == GraphicsFormat.Unknown)
            throw new ArgumentException("A render pipeline must declare at least one color or depth/stencil format.");
        for (int i = 0; i < _colorFormats.Length; i++)
        {
            GraphicsFormat format = _colorFormats[i];
            if (format == GraphicsFormat.Unknown || !Enum.IsDefined(format))
                throw new ArgumentOutOfRangeException(nameof(ColorFormats));
            if (!Layout.Device.Capabilities.SupportsFormat(format, GraphicsFormatSupport.ColorAttachment) ||
                !Layout.Device.Capabilities.SupportsSampleCount(format, SampleCount))
                throw new GraphicsException($"The device does not support {format} as a {SampleCount}-sample color attachment.");
        }
        if (DepthStencilFormat != GraphicsFormat.Unknown)
        {
            if (!Enum.IsDefined(DepthStencilFormat))
                throw new ArgumentOutOfRangeException(nameof(DepthStencilFormat));
            if (!Layout.Device.Capabilities.SupportsFormat(DepthStencilFormat, GraphicsFormatSupport.DepthStencilAttachment) ||
                !Layout.Device.Capabilities.SupportsSampleCount(DepthStencilFormat, SampleCount))
                throw new GraphicsException($"The device does not support {DepthStencilFormat} as a {SampleCount}-sample depth/stencil attachment.");
        }
        if (DepthStencil.DepthTestEnabled && DepthStencilFormat == GraphicsFormat.Unknown)
            throw new ArgumentException("Depth testing requires a depth/stencil attachment format.", nameof(DepthStencil));
        if (DepthStencil.StencilTestEnabled && DepthStencilFormat == GraphicsFormat.Unknown)
            throw new ArgumentException("Stencil testing requires a depth/stencil attachment format.", nameof(DepthStencil));
        if (Blend.Attachments.Length != 0 && Blend.Attachments.Length != _colorFormats.Length)
            throw new ArgumentException("Blend attachment count must match the number of color formats.", nameof(Blend));

        Rasterization.Validate();
        DepthStencil.Validate();
        Blend.Validate();
        VertexInput?.Validate();

        GraphicsFeature required = GraphicsFeature.None;
        if (Rasterization.DepthClampEnabled) required |= GraphicsFeature.DepthClamp;
        if (Rasterization.PolygonMode != GraphicsPolygonMode.Fill) required |= GraphicsFeature.FillModeNonSolid;
        if (Rasterization.LineWidth != 1) required |= GraphicsFeature.WideLines;
        if (DepthStencil.DepthBoundsTestEnabled) required |= GraphicsFeature.DepthBoundsTest;
        GraphicsFeature missing = Layout.Device.Capabilities.Features.MissingFrom(required);
        if (missing != GraphicsFeature.None)
            throw new GraphicsFeatureNotSupportedException(required, missing);
    }
}

/// <summary>Fixed state for a compute pipeline.</summary>
public sealed class GraphicsComputePipelineDescription
{
    public GraphicsComputePipelineDescription(IGraphicsPipelineLayout layout, IGraphicsShader computeShader)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ComputeShader = computeShader ?? throw new ArgumentNullException(nameof(computeShader));
        Validate();
    }

    public IGraphicsPipelineLayout Layout { get; }
    public IGraphicsShader ComputeShader { get; }

    public void Validate()
    {
        if (!ReferenceEquals(Layout.Device, ComputeShader.Device))
            throw new ArgumentException("Pipeline layout and compute shader must belong to the same graphics device.");
        if (ComputeShader.Description.Stage != GraphicsShaderStage.Compute)
            throw new ArgumentException("A compute pipeline requires a Compute-stage shader.", nameof(ComputeShader));
    }
}
