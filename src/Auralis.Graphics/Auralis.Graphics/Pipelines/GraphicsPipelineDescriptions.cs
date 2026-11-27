using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
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
    RayGeneration = 1 << 8,
    Miss = 1 << 9,
    ClosestHit = 1 << 10,
    AnyHit = 1 << 11,
    Intersection = 1 << 12,
    Callable = 1 << 13,
    AllGraphics = Vertex | Fragment | Geometry | TessellationControl | TessellationEvaluation | Task | Mesh,
    AllRayTracing = RayGeneration | Miss | ClosestHit | AnyHit | Intersection | Callable,
    All = AllGraphics | AllRayTracing | Compute
}

public enum GraphicsDescriptorType
{
    UniformBuffer,
    StorageBuffer,
    SampledTexture,
    StorageTexture,
    Sampler,
    CombinedTextureSampler,
    DynamicUniformBuffer,
    DynamicStorageBuffer
}

[Flags]
public enum GraphicsBindingFlags
{
    None = 0,
    PartiallyBound = 1 << 0,
    UpdateAfterBind = 1 << 1,
    VariableCount = 1 << 2,
    UpdateUnusedWhilePending = 1 << 3
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

    /// <summary>Validates runtime counts supplied when allocating a variable-count binding set.</summary>
    public void ValidateVariableCounts(ReadOnlySpan<GraphicsVariableDescriptorCount> variableCounts)
    {
        int variableBindingCount = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            if ((_entries[i].Flags & GraphicsBindingFlags.VariableCount) != 0)
                variableBindingCount++;
        }

        if (variableCounts.Length != variableBindingCount)
            throw new ArgumentException("A runtime count must be supplied for every variable-count binding and no other binding.", nameof(variableCounts));

        for (int i = 0; i < variableCounts.Length; i++)
        {
            bool found = false;
            uint maximumCount = 0;
            for (int j = 0; j < _entries.Length; j++)
            {
                if (_entries[j].Binding == variableCounts[i].Binding &&
                    (_entries[j].Flags & GraphicsBindingFlags.VariableCount) != 0)
                {
                    found = true;
                    maximumCount = _entries[j].Count;
                    break;
                }
            }

            if (!found)
                throw new ArgumentException($"Binding {variableCounts[i].Binding} is not a variable-count binding.", nameof(variableCounts));
            if (variableCounts[i].Count > maximumCount)
                throw new ArgumentOutOfRangeException(nameof(variableCounts), $"Runtime descriptor count exceeds the declared maximum for binding {variableCounts[i].Binding}.");

            for (int j = 0; j < i; j++)
            {
                if (variableCounts[j].Binding == variableCounts[i].Binding)
                    throw new ArgumentException($"Runtime count for binding {variableCounts[i].Binding} is duplicated.", nameof(variableCounts));
            }
        }
    }

    /// <summary>
    /// Validates variable descriptor allocation counts against structural layout rules and logical-device capabilities.
    /// Zero is accepted only when the device explicitly reports ZeroVariableDescriptorCount as enabled.
    /// </summary>
    public void ValidateVariableCounts(
        ReadOnlySpan<GraphicsVariableDescriptorCount> variableCounts,
        GraphicsDeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ValidateVariableCounts(variableCounts);
        for (int i = 0; i < variableCounts.Length; i++)
        {
            if (variableCounts[i].Count == 0 &&
                !capabilities.DescriptorIndexing.ZeroVariableDescriptorCount)
                throw new GraphicsException(
                    "A zero-sized variable descriptor allocation is not enabled on this logical device.");
        }
    }

    /// <summary>Validates ordered dynamic offsets. Device-specific alignment is checked by the overload receiving device limits.</summary>
    public void ValidateDynamicOffsets(ReadOnlySpan<GraphicsDynamicBufferOffset> dynamicOffsets)
    {
        int expectedCount = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Type is not (GraphicsDescriptorType.DynamicUniformBuffer or GraphicsDescriptorType.DynamicStorageBuffer))
                continue;
            if (_entries[i].Count > int.MaxValue || expectedCount > int.MaxValue - (int)_entries[i].Count)
                throw new ArgumentOutOfRangeException(nameof(dynamicOffsets),
                    "The layout has more dynamic descriptors than can be represented by a managed span.");
            expectedCount += (int)_entries[i].Count;
        }

        if (dynamicOffsets.Length != expectedCount)
            throw new ArgumentException($"Expected {expectedCount} dynamic buffer offsets, received {dynamicOffsets.Length}.", nameof(dynamicOffsets));

        int cursor = 0;
        uint previousLayoutBinding = 0;
        bool hasPreviousLayoutBinding = false;
        for (int bindingIndex = 0; bindingIndex < _entries.Length; bindingIndex++)
        {
            int selectedIndex = -1;
            uint selectedBinding = uint.MaxValue;
            for (int i = 0; i < _entries.Length; i++)
            {
                GraphicsBindingLayoutEntry candidate = _entries[i];
                bool isDynamic = candidate.Type is GraphicsDescriptorType.DynamicUniformBuffer or GraphicsDescriptorType.DynamicStorageBuffer;
                if (isDynamic &&
                    (!hasPreviousLayoutBinding || candidate.Binding > previousLayoutBinding) &&
                    candidate.Binding < selectedBinding)
                {
                    selectedIndex = i;
                    selectedBinding = candidate.Binding;
                }
            }

            if (selectedIndex < 0)
                break;

            GraphicsBindingLayoutEntry entry = _entries[selectedIndex];
            for (uint element = 0; element < entry.Count; element++)
            {
                if (dynamicOffsets[cursor].Binding != entry.Binding || dynamicOffsets[cursor].ArrayElement != element)
                    throw new ArgumentException("Dynamic offsets must contain every dynamic descriptor in ascending binding/array order.", nameof(dynamicOffsets));
                cursor++;
            }

            previousLayoutBinding = entry.Binding;
            hasPreviousLayoutBinding = true;
        }
    }

    /// <summary>
    /// Validates dynamic offset ordering and device-reported uniform/storage alignment. A zero limit means
    /// the backend did not report the alignment; implementations must still apply their native rules.
    /// Buffer bounds are validated when the binding set is bound because base offsets/ranges are not part
    /// of this layout description.
    /// </summary>
    public void ValidateDynamicOffsets(
        ReadOnlySpan<GraphicsDynamicBufferOffset> dynamicOffsets,
        in GraphicsDeviceLimits limits)
    {
        ValidateDynamicOffsets(dynamicOffsets);
        for (int i = 0; i < dynamicOffsets.Length; i++)
        {
            ulong alignment = 0;
            for (int entryIndex = 0; entryIndex < _entries.Length; entryIndex++)
            {
                GraphicsBindingLayoutEntry entry = _entries[entryIndex];
                if (entry.Binding != dynamicOffsets[i].Binding)
                    continue;
                alignment = entry.Type == GraphicsDescriptorType.DynamicUniformBuffer
                    ? limits.MinUniformBufferOffsetAlignment
                    : entry.Type == GraphicsDescriptorType.DynamicStorageBuffer
                        ? limits.MinStorageBufferOffsetAlignment
                        : 0;
                break;
            }

            if (alignment != 0 && dynamicOffsets[i].Offset % alignment != 0)
                throw new ArgumentException(
                    $"Dynamic offset {dynamicOffsets[i].Offset} for binding {dynamicOffsets[i].Binding} must be aligned to {alignment} bytes.",
                    nameof(dynamicOffsets));
        }
    }

    /// <summary>
    /// Validates structural type/flag rules and this logical device's independently enabled descriptor-indexing features.
    /// Physical adapter support alone is insufficient; the backend must have enabled each required feature when
    /// creating the device. Dynamic offset alignment is validated by the device-limits overload and effective buffer
    /// bounds remain a bind-time responsibility.
    /// </summary>
    private static bool SupportsUpdateAfterBind(
        GraphicsDescriptorIndexingCapabilities capabilities,
        GraphicsDescriptorType type) =>
        type switch
        {
            GraphicsDescriptorType.UniformBuffer => capabilities.UniformBufferUpdateAfterBind,
            GraphicsDescriptorType.StorageBuffer => capabilities.StorageBufferUpdateAfterBind,
            GraphicsDescriptorType.SampledTexture or GraphicsDescriptorType.Sampler or
                GraphicsDescriptorType.CombinedTextureSampler => capabilities.SampledImageUpdateAfterBind,
            GraphicsDescriptorType.StorageTexture => capabilities.StorageImageUpdateAfterBind,
            GraphicsDescriptorType.DynamicUniformBuffer or GraphicsDescriptorType.DynamicStorageBuffer => false,
            _ => false
        };

    public void Validate(GraphicsDeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        Validate();

        bool usesDescriptorIndexing = false;
        for (int i = 0; i < _entries.Length; i++)
        {
            GraphicsBindingLayoutEntry entry = _entries[i];
            GraphicsBindingFlags flags = entry.Flags;
            usesDescriptorIndexing |= flags != GraphicsBindingFlags.None;

            if ((flags & GraphicsBindingFlags.PartiallyBound) != 0 &&
                !capabilities.DescriptorIndexing.PartiallyBound)
                throw new GraphicsException("Partially-bound descriptors are not enabled on this device.");
            if ((flags & GraphicsBindingFlags.UpdateUnusedWhilePending) != 0 &&
                !capabilities.DescriptorIndexing.UpdateUnusedWhilePending)
                throw new GraphicsException("Update-unused-while-pending descriptors are not enabled on this device.");
            if ((flags & GraphicsBindingFlags.VariableCount) != 0 &&
                !capabilities.DescriptorIndexing.VariableDescriptorCount)
                throw new GraphicsException("Variable descriptor counts are not enabled on this device.");
            if ((flags & GraphicsBindingFlags.UpdateAfterBind) != 0 &&
                !SupportsUpdateAfterBind(capabilities.DescriptorIndexing, entry.Type))
                throw new GraphicsException($"Update-after-bind is not enabled for descriptor type {entry.Type}.");
        }

        if (usesDescriptorIndexing && !capabilities.Features.Supports(GraphicsFeature.DescriptorIndexing))
            throw new GraphicsException("Descriptor indexing is not enabled on this device.");
    }

    public void Validate()
    {
        const GraphicsShaderStages allStages = GraphicsShaderStages.All;
        const GraphicsBindingFlags allFlags =
            GraphicsBindingFlags.PartiallyBound | GraphicsBindingFlags.UpdateAfterBind |
            GraphicsBindingFlags.VariableCount | GraphicsBindingFlags.UpdateUnusedWhilePending;

        uint highestBinding = 0;
        for (int i = 0; i < _entries.Length; i++)
            highestBinding = Math.Max(highestBinding, _entries[i].Binding);

        bool hasUpdateAfterBind = false;
        bool hasDynamicBufferDescriptor = false;
        for (int i = 0; i < _entries.Length; i++)
        {
            hasUpdateAfterBind |= (_entries[i].Flags & GraphicsBindingFlags.UpdateAfterBind) != 0;
            hasDynamicBufferDescriptor |= _entries[i].Type is
                GraphicsDescriptorType.DynamicUniformBuffer or GraphicsDescriptorType.DynamicStorageBuffer;
        }
        if (hasUpdateAfterBind && hasDynamicBufferDescriptor)
            throw new ArgumentException(
                "A descriptor layout containing update-after-bind bindings cannot contain dynamic uniform/storage buffer descriptors.",
                nameof(Entries));

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
            bool dynamicBufferType = entry.Type is GraphicsDescriptorType.DynamicUniformBuffer or GraphicsDescriptorType.DynamicStorageBuffer;
            if (dynamicBufferType &&
                (entry.Flags & (GraphicsBindingFlags.VariableCount | GraphicsBindingFlags.UpdateAfterBind)) != 0)
                throw new ArgumentException(
                    "Dynamic uniform/storage buffer descriptors cannot use variable-count or update-after-bind flags.",
                    nameof(Entries));
            if ((entry.Flags & GraphicsBindingFlags.VariableCount) != 0 && entry.Binding != highestBinding)
                throw new ArgumentException(
                    "A variable-count descriptor binding must have the highest binding number.",
                    nameof(Entries));

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

/// <summary>Patch assembly configuration used with a tessellation control/evaluation shader pair.</summary>
public readonly record struct GraphicsTessellationState(uint PatchControlPoints)
{
    public void Validate()
    {
        if (PatchControlPoints == 0)
            throw new ArgumentOutOfRangeException(nameof(PatchControlPoints),
                "Patch control-point count must be greater than zero.");
    }
}

/// <summary>Complete fixed-function state and shader-stage configuration for a render pipeline.</summary>
public sealed class GraphicsRenderPipelineDescription
{
    private readonly IGraphicsShader[] _shaders;
    private readonly GraphicsFormat[] _colorFormats;

    /// <summary>
    /// Creates a pipeline from an explicit shader-stage set. Conventional pipelines require a vertex shader;
    /// mesh pipelines require a mesh shader and must not include vertex, geometry, or tessellation stages.
    /// </summary>
    public GraphicsRenderPipelineDescription(
        IGraphicsPipelineLayout layout,
        ReadOnlySpan<IGraphicsShader> shaders,
        GraphicsPrimitiveTopology topology,
        ReadOnlySpan<GraphicsFormat> colorFormats,
        GraphicsFormat depthStencilFormat = GraphicsFormat.Unknown,
        uint sampleCount = 1,
        GraphicsVertexInputLayout? vertexInput = null,
        GraphicsRasterizationState? rasterization = null,
        GraphicsDepthStencilState? depthStencil = null,
        GraphicsBlendState? blend = null,
        GraphicsTessellationState? tessellation = null)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _shaders = shaders.ToArray();
        Topology = topology;
        _colorFormats = colorFormats.ToArray();
        DepthStencilFormat = depthStencilFormat;
        SampleCount = sampleCount;
        VertexInput = vertexInput;
        Rasterization = rasterization ?? new GraphicsRasterizationState();
        DepthStencil = depthStencil ?? new GraphicsDepthStencilState();
        Blend = blend ?? new GraphicsBlendState();
        Tessellation = tessellation;
        Validate();
    }

    /// <summary>Compatibility overload for conventional vertex/fragment pipelines.</summary>
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
        GraphicsBlendState? blend = null,
        GraphicsTessellationState? tessellation = null)
        : this(
            layout,
            CreateConventionalShaderList(vertexShader, fragmentShader),
            topology,
            colorFormats,
            depthStencilFormat,
            sampleCount,
            vertexInput,
            rasterization,
            depthStencil,
            blend,
            tessellation)
    {
    }

    public IGraphicsPipelineLayout Layout { get; }

    public ReadOnlySpan<IGraphicsShader> Shaders => _shaders;

    /// <summary>Returns the vertex shader for conventional pipelines, or null for mesh pipelines.</summary>
    public IGraphicsShader? VertexShader => FindShader(GraphicsShaderStage.Vertex);

    /// <summary>Returns the fragment shader when one is configured.</summary>
    public IGraphicsShader? FragmentShader => FindShader(GraphicsShaderStage.Fragment);

    public GraphicsPrimitiveTopology Topology { get; }
    public ReadOnlySpan<GraphicsFormat> ColorFormats => _colorFormats;
    public GraphicsFormat DepthStencilFormat { get; }
    public uint SampleCount { get; }
    public GraphicsVertexInputLayout? VertexInput { get; }
    public GraphicsRasterizationState Rasterization { get; }
    public GraphicsDepthStencilState DepthStencil { get; }
    public GraphicsBlendState Blend { get; }
    public GraphicsTessellationState? Tessellation { get; }

    public void Validate()
    {
        if (Layout.IsDisposed || Layout.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(Layout));
        Layout.Device.Capabilities.ValidatePipelineLayout(Layout);
        if (_shaders.Length == 0)
            throw new ArgumentException("A render pipeline must contain at least one graphics shader.", nameof(Shaders));
        if (!Enum.IsDefined(Topology))
            throw new ArgumentOutOfRangeException(nameof(Topology));
        if (SampleCount is 0 or > 64 || (SampleCount & (SampleCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(SampleCount));

        uint stageMask = 0;
        for (int i = 0; i < _shaders.Length; i++)
        {
            IGraphicsShader shader = _shaders[i] ??
                throw new ArgumentException("Shader lists cannot contain null entries.", nameof(Shaders));
            if (shader.IsDisposed || shader.Device.IsDisposed)
                throw new ObjectDisposedException(nameof(Shaders));
            if (!ReferenceEquals(Layout.Device, shader.Device))
                throw new ArgumentException("Pipeline layout and shaders must belong to the same graphics device.");

            shader.Description.Validate();
            GraphicsShaderStage stage = shader.Description.Stage;
            if (stage == GraphicsShaderStage.Compute)
                throw new ArgumentException("Compute shaders cannot be included in a render pipeline.", nameof(Shaders));
            if (stage is GraphicsShaderStage.RayGeneration or GraphicsShaderStage.Miss or
                GraphicsShaderStage.ClosestHit or GraphicsShaderStage.AnyHit or
                GraphicsShaderStage.Intersection or GraphicsShaderStage.Callable)
                throw new ArgumentException("Ray-tracing shaders cannot be included in a conventional render pipeline.", nameof(Shaders));

            uint stageBit = 1u << (int)stage;
            if ((stageMask & stageBit) != 0)
                throw new ArgumentException($"The {stage} shader stage is declared more than once.", nameof(Shaders));
            stageMask |= stageBit;
        }

        bool hasVertex = HasStage(stageMask, GraphicsShaderStage.Vertex);
        bool hasGeometry = HasStage(stageMask, GraphicsShaderStage.Geometry);
        bool hasTessellationControl = HasStage(stageMask, GraphicsShaderStage.TessellationControl);
        bool hasTessellationEvaluation = HasStage(stageMask, GraphicsShaderStage.TessellationEvaluation);
        bool hasTask = HasStage(stageMask, GraphicsShaderStage.Task);
        bool hasMesh = HasStage(stageMask, GraphicsShaderStage.Mesh);
        bool hasTessellation = hasTessellationControl || hasTessellationEvaluation;

        if (hasTask && !hasMesh)
            throw new ArgumentException("A task shader requires a mesh shader.", nameof(Shaders));

        if (hasMesh)
        {
            if (Tessellation is not null)
                throw new ArgumentException("Mesh pipelines cannot declare tessellation state.", nameof(Tessellation));
            if (hasVertex || hasGeometry || hasTessellation)
                throw new ArgumentException(
                    "Mesh pipelines cannot combine mesh shading with vertex, geometry, or tessellation stages.",
                    nameof(Shaders));
            if (Topology == GraphicsPrimitiveTopology.PatchList ||
                Topology is GraphicsPrimitiveTopology.LineListAdjacency or
                    GraphicsPrimitiveTopology.LineStripAdjacency or
                    GraphicsPrimitiveTopology.TriangleListAdjacency or
                    GraphicsPrimitiveTopology.TriangleStripAdjacency)
                throw new ArgumentException("Mesh pipelines cannot use patch or adjacency input-assembly topologies.", nameof(Topology));
            if (VertexInput is not null)
                throw new ArgumentException("Mesh pipelines cannot declare a vertex input layout.", nameof(VertexInput));
        }
        else
        {
            if (!hasVertex)
                throw new ArgumentException("A conventional render pipeline requires a vertex shader.", nameof(Shaders));
            if (hasTessellationControl != hasTessellationEvaluation)
                throw new ArgumentException(
                    "Tessellation control and evaluation shaders must be supplied together.",
                    nameof(Shaders));

            bool patchTopology = Topology == GraphicsPrimitiveTopology.PatchList;
            if (patchTopology != hasTessellation)
                throw new ArgumentException(
                    "PatchList topology requires both tessellation stages, and tessellation stages require PatchList topology.",
                    nameof(Topology));

            if (patchTopology)
            {
                if (Tessellation is not { } tessellation)
                    throw new ArgumentException("PatchList topology requires tessellation state.", nameof(Tessellation));
                tessellation.Validate();
                uint patchLimit = Layout.Device.Capabilities.Limits.MaxTessellationPatchSize;
                if (patchLimit != 0 && tessellation.PatchControlPoints > patchLimit)
                    throw new GraphicsException(
                        $"Patch control-point count {tessellation.PatchControlPoints} exceeds the device limit {patchLimit}.");
            }
            else if (Tessellation is not null)
            {
                throw new ArgumentException("Tessellation state is valid only with PatchList topology.", nameof(Tessellation));
            }
        }

        bool adjacencyTopology = Topology is
            GraphicsPrimitiveTopology.LineListAdjacency or
            GraphicsPrimitiveTopology.LineStripAdjacency or
            GraphicsPrimitiveTopology.TriangleListAdjacency or
            GraphicsPrimitiveTopology.TriangleStripAdjacency;
        if (adjacencyTopology && !hasGeometry)
            throw new ArgumentException("Adjacency topologies require a geometry shader stage.", nameof(Topology));

        if (_colorFormats.Length == 0 && DepthStencilFormat == GraphicsFormat.Unknown)
            throw new ArgumentException("A render pipeline must declare at least one color or depth/stencil format.");
        uint maximumColorAttachments = Layout.Device.Capabilities.Limits.MaxColorAttachments;
        if (maximumColorAttachments != 0 && (uint)_colorFormats.Length > maximumColorAttachments)
            throw new GraphicsException(
                $"Pipeline declares {_colorFormats.Length} color attachments; the device limit is {maximumColorAttachments}.");
        for (int i = 0; i < _colorFormats.Length; i++)
        {
            GraphicsFormat format = _colorFormats[i];
            if (format == GraphicsFormat.Unknown || !Enum.IsDefined(format))
                throw new ArgumentOutOfRangeException(nameof(ColorFormats));
            if (GraphicsFormatInfo.GetAspects(format) != GraphicsTextureAspect.Color)
                throw new ArgumentException($"Color attachment format {format} must expose only the color aspect.", nameof(ColorFormats));
            if (!Layout.Device.Capabilities.SupportsFormat(format, GraphicsFormatSupport.ColorAttachment) ||
                !Layout.Device.Capabilities.SupportsSampleCount(format, SampleCount))
                throw new GraphicsException(
                    $"The device does not support {format} as a {SampleCount}-sample color attachment.");
        }
        GraphicsTextureAspect depthStencilAspects = GraphicsFormatInfo.GetAspects(DepthStencilFormat);
        if (DepthStencilFormat != GraphicsFormat.Unknown)
        {
            if (!Enum.IsDefined(DepthStencilFormat))
                throw new ArgumentOutOfRangeException(nameof(DepthStencilFormat));
            if ((depthStencilAspects & (GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil)) == 0)
                throw new ArgumentException(
                    $"Depth/stencil attachment format {DepthStencilFormat} must contain a depth or stencil aspect.",
                    nameof(DepthStencilFormat));
            if (!Layout.Device.Capabilities.SupportsFormat(
                    DepthStencilFormat, GraphicsFormatSupport.DepthStencilAttachment) ||
                !Layout.Device.Capabilities.SupportsSampleCount(DepthStencilFormat, SampleCount))
                throw new GraphicsException(
                    $"The device does not support {DepthStencilFormat} as a {SampleCount}-sample depth/stencil attachment.");
        }
        if ((DepthStencil.DepthTestEnabled || DepthStencil.DepthWriteEnabled || DepthStencil.DepthBoundsTestEnabled) &&
            (depthStencilAspects & GraphicsTextureAspect.Depth) == 0)
            throw new ArgumentException("Depth testing, writes, and depth-bounds testing require a format with a depth aspect.", nameof(DepthStencil));
        if (DepthStencil.StencilTestEnabled &&
            (depthStencilAspects & GraphicsTextureAspect.Stencil) == 0)
            throw new ArgumentException("Stencil testing requires a format with a stencil aspect.", nameof(DepthStencil));
        if (Blend.Attachments.Length != 0 && Blend.Attachments.Length != _colorFormats.Length)
            throw new ArgumentException("Blend attachment count must match the number of color formats.", nameof(Blend));

        Rasterization.Validate();
        DepthStencil.Validate();
        Blend.Validate();

        if (VertexInput is not null)
            Layout.Device.Capabilities.ValidateVertexInputLayout(VertexInput);

        ReadOnlySpan<GraphicsBlendAttachmentState> blendAttachments = Blend.Attachments;
        for (int i = 0; i < blendAttachments.Length; i++)
        {
            if (blendAttachments[i].BlendEnabled &&
                !Layout.Device.Capabilities.SupportsFormat(_colorFormats[i], GraphicsFormatSupport.Blend))
                throw new GraphicsException(
                    $"The device does not support blending with color format {_colorFormats[i]}.");
        }

        GraphicsFeature required = GraphicsFeature.None;
        if (hasGeometry) required |= GraphicsFeature.GeometryShaders;
        if (hasTessellation) required |= GraphicsFeature.TessellationShaders;
        if (hasMesh) required |= GraphicsFeature.MeshShaders;
        if (hasTask) required |= GraphicsFeature.TaskShaders;
        if (Rasterization.DepthClampEnabled) required |= GraphicsFeature.DepthClamp;
        if (Rasterization.PolygonMode != GraphicsPolygonMode.Fill) required |= GraphicsFeature.FillModeNonSolid;
        if (Rasterization.LineWidth != 1) required |= GraphicsFeature.WideLines;
        if (DepthStencil.DepthBoundsTestEnabled) required |= GraphicsFeature.DepthBoundsTest;

        GraphicsFeature missing = Layout.Device.Capabilities.Features.MissingFrom(required);
        if (missing != GraphicsFeature.None)
            throw new GraphicsFeatureNotSupportedException(required, missing);
    }

    private IGraphicsShader? FindShader(GraphicsShaderStage stage)
    {
        for (int i = 0; i < _shaders.Length; i++)
        {
            if (_shaders[i].Description.Stage == stage)
                return _shaders[i];
        }

        return null;
    }

    private static bool HasStage(uint stageMask, GraphicsShaderStage stage) =>
        (stageMask & (1u << (int)stage)) != 0;

    private static IGraphicsShader[] CreateConventionalShaderList(
        IGraphicsShader vertexShader,
        IGraphicsShader? fragmentShader)
    {
        ArgumentNullException.ThrowIfNull(vertexShader);
        return fragmentShader is null ? [vertexShader] : [vertexShader, fragmentShader];
    }
}

/// <summary>Thread dimensions selected for each compute workgroup.</summary>
public readonly record struct GraphicsComputeThreadGroupSize(uint X, uint Y, uint Z)
{
    /// <summary>Validates that every workgroup dimension is non-zero.</summary>
    public void Validate()
    {
        if (X == 0)
            throw new ArgumentOutOfRangeException(nameof(X), "Compute workgroup dimensions must be non-zero.");
        if (Y == 0)
            throw new ArgumentOutOfRangeException(nameof(Y), "Compute workgroup dimensions must be non-zero.");
        if (Z == 0)
            throw new ArgumentOutOfRangeException(nameof(Z), "Compute workgroup dimensions must be non-zero.");
    }
}

/// <summary>Fixed state for a compute pipeline.</summary>
public sealed class GraphicsComputePipelineDescription
{
    /// <summary>
    /// Creates a compute pipeline description with an explicit threadgroup size. The dimensions
    /// are pipeline state because the common dispatch command specifies only the number of groups.
    /// </summary>
    public GraphicsComputePipelineDescription(
        IGraphicsPipelineLayout layout,
        IGraphicsShader computeShader,
        GraphicsComputeThreadGroupSize threadGroupSize)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ComputeShader = computeShader ?? throw new ArgumentNullException(nameof(computeShader));
        ThreadGroupSize = threadGroupSize;
        Validate();
    }

    /// <summary>Pipeline layout used by the compute shader.</summary>
    public IGraphicsPipelineLayout Layout { get; }

    /// <summary>Compiled compute-stage shader.</summary>
    public IGraphicsShader ComputeShader { get; }

    /// <summary>Number of shader threads in each workgroup along X, Y, and Z.</summary>
    public GraphicsComputeThreadGroupSize ThreadGroupSize { get; }

    /// <summary>
    /// Validates ownership, shader stage, enabled compute support, pipeline layout limits,
    /// and the device-reported workgroup dimensions and invocation count. Backend implementations
    /// must additionally validate pipeline-specific native limits before creating the pipeline.
    /// </summary>
    public void Validate()
    {
        if (!ReferenceEquals(Layout.Device, ComputeShader.Device))
            throw new ArgumentException("Pipeline layout and compute shader must belong to the same graphics device.");
        if (ComputeShader.IsDisposed || Layout.IsDisposed || Layout.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(ComputeShader));
        Layout.Device.Capabilities.ValidatePipelineLayout(Layout);
        ComputeShader.Description.Validate();
        if (ComputeShader.Description.Stage != GraphicsShaderStage.Compute)
            throw new ArgumentException("A compute pipeline requires a Compute-stage shader.", nameof(ComputeShader));
        if (!Layout.Device.Capabilities.Features.Supports(GraphicsFeature.ComputeShaders))
            throw new GraphicsFeatureNotSupportedException(
                GraphicsFeature.ComputeShaders,
                GraphicsFeature.ComputeShaders);

        Layout.Device.Capabilities.ValidateComputeThreadGroupSize(ThreadGroupSize);
    }
}
