using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Devices;

/// <summary>Implementation-reported hardware limits. Zero means that a value is not reported.</summary>
public readonly record struct GraphicsDeviceLimits(
    ulong MaxBufferSizeInBytes,
    uint MaxTextureDimension1D,
    uint MaxTextureDimension2D,
    uint MaxTextureDimension3D,
    uint MaxTextureArrayLayers,
    uint MaxVertexInputAttributes,
    uint MaxVertexInputBindings,
    uint MaxBoundDescriptorSets,
    uint MaxComputeWorkGroupInvocations,
    uint MaxComputeWorkGroupSizeX,
    uint MaxComputeWorkGroupSizeY,
    uint MaxComputeWorkGroupSizeZ,
    float MaxSamplerAnisotropy,
    uint MaxTessellationPatchSize = 0,
    uint MaxRayRecursionDepth = 0,
    uint MaxRayPayloadSizeInBytes = 0,
    uint MaxRayHitAttributeSizeInBytes = 0,
    ulong MinUniformBufferOffsetAlignment = 0,
    ulong MinStorageBufferOffsetAlignment = 0,
    uint MaxMultiviewViewCount = 0,
    /// <summary>Maximum cube-map face dimension; zero means this limit is not reported.</summary>
    uint MaxCubeTextureDimension = 0,
    /// <summary>Maximum simultaneous color attachments; zero means this limit is not reported.</summary>
    uint MaxColorAttachments = 0)
{
    /// <summary>Validates optional alignment limits. Zero denotes an unreported limit.</summary>
    public void Validate()
    {
        if (MinUniformBufferOffsetAlignment != 0 &&
            (MinUniformBufferOffsetAlignment & (MinUniformBufferOffsetAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(MinUniformBufferOffsetAlignment),
                "Uniform-buffer dynamic-offset alignment must be zero or a power of two.");
        if (MinStorageBufferOffsetAlignment != 0 &&
            (MinStorageBufferOffsetAlignment & (MinStorageBufferOffsetAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(MinStorageBufferOffsetAlignment),
                "Storage-buffer dynamic-offset alignment must be zero or a power of two.");
    }
}

/// <summary>Describes the operations supported for a particular texture format.</summary>
public readonly record struct GraphicsFormatCapabilities(
    GraphicsFormat Format,
    GraphicsFormatSupport Support,
    GraphicsSampleCountFlags SampleCounts,
    ReadOnlyMemory<GraphicsFormat> ViewCompatibleFormats = default)
{
    public bool SupportsViewFormat(GraphicsFormat viewFormat)
    {
        if (viewFormat == Format)
            return true;
        ReadOnlySpan<GraphicsFormat> compatibleFormats = ViewCompatibleFormats.Span;
        for (int i = 0; i < compatibleFormats.Length; i++)
        {
            if (compatibleFormats[i] == viewFormat)
                return true;
        }
        return false;
    }
    public bool Supports(GraphicsFormatSupport support) =>
        support != GraphicsFormatSupport.None && (Support & support) == support;

    public bool SupportsSampleCount(uint sampleCount)
    {
        GraphicsSampleCountFlags flag = sampleCount switch
        {
            1 => GraphicsSampleCountFlags.One,
            2 => GraphicsSampleCountFlags.Two,
            4 => GraphicsSampleCountFlags.Four,
            8 => GraphicsSampleCountFlags.Eight,
            16 => GraphicsSampleCountFlags.Sixteen,
            32 => GraphicsSampleCountFlags.ThirtyTwo,
            64 => GraphicsSampleCountFlags.SixtyFour,
            _ => GraphicsSampleCountFlags.None
        };

        return flag != GraphicsSampleCountFlags.None && (SampleCounts & flag) != 0;
    }
}

/// <summary>
/// Immutable, backend-reported feature and limit snapshot. No capability is inferred from the backend name.
/// </summary>
public sealed class GraphicsDeviceCapabilities
{
    private readonly GraphicsFormatCapabilities[] _formats;

    public GraphicsFeatureSet Features { get; }

    public GraphicsQueueCapabilities QueueCapabilities { get; }

    public GraphicsDeviceLimits Limits { get; }

    public GraphicsDescriptorIndexingCapabilities DescriptorIndexing { get; }

    public ReadOnlySpan<GraphicsFormatCapabilities> Formats => _formats;

    public GraphicsDeviceCapabilities(
        GraphicsFeatureSet features,
        GraphicsQueueCapabilities queueCapabilities,
        GraphicsDeviceLimits limits,
        ReadOnlySpan<GraphicsFormatCapabilities> formats,
        GraphicsDescriptorIndexingCapabilities descriptorIndexing = default)
    {
        const GraphicsFeature allFeatures =
            GraphicsFeature.ComputeShaders | GraphicsFeature.IndirectDrawing |
            GraphicsFeature.MultiDrawIndirect | GraphicsFeature.DescriptorIndexing |
            GraphicsFeature.SeparateComputeQueue | GraphicsFeature.SeparateTransferQueue |
            GraphicsFeature.TimestampQueries | GraphicsFeature.SamplerAnisotropy |
            GraphicsFeature.TessellationShaders | GraphicsFeature.GeometryShaders |
            GraphicsFeature.MeshShaders | GraphicsFeature.TaskShaders | GraphicsFeature.RayTracing |
            GraphicsFeature.VariableRateShading | GraphicsFeature.ConservativeRasterization |
            GraphicsFeature.TimelineSynchronization | GraphicsFeature.SparseResources |
            GraphicsFeature.MultiViewport | GraphicsFeature.DepthBoundsTest |
            GraphicsFeature.LogicOperations | GraphicsFeature.WideLines |
            GraphicsFeature.DepthClamp | GraphicsFeature.FillModeNonSolid |
            GraphicsFeature.DeviceGroups | GraphicsFeature.Multiview;
        const GraphicsQueueCapabilities allQueues =
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute |
            GraphicsQueueCapabilities.Copy | GraphicsQueueCapabilities.Present;
        const GraphicsFormatSupport allFormatSupport =
            GraphicsFormatSupport.Sampled | GraphicsFormatSupport.Storage |
            GraphicsFormatSupport.ColorAttachment | GraphicsFormatSupport.DepthStencilAttachment |
            GraphicsFormatSupport.Blend | GraphicsFormatSupport.VertexBuffer |
            GraphicsFormatSupport.CopySource | GraphicsFormatSupport.CopyDestination |
            GraphicsFormatSupport.LinearFiltering | GraphicsFormatSupport.ShadingRateAttachment |
            GraphicsFormatSupport.AccelerationStructureVertex;
        const GraphicsSampleCountFlags allSampleCounts =
            GraphicsSampleCountFlags.One | GraphicsSampleCountFlags.Two |
            GraphicsSampleCountFlags.Four | GraphicsSampleCountFlags.Eight |
            GraphicsSampleCountFlags.Sixteen | GraphicsSampleCountFlags.ThirtyTwo |
            GraphicsSampleCountFlags.SixtyFour;

        if ((features.Enabled & ~allFeatures) != 0)
            throw new ArgumentOutOfRangeException(nameof(features));
        if ((queueCapabilities & ~allQueues) != 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapabilities));
        limits.Validate();
        descriptorIndexing.Validate();
        if (features.Supports(GraphicsFeature.Multiview) && limits.MaxMultiviewViewCount < 2)
            throw new ArgumentException(
                "An enabled multiview feature must report a maximum view count of at least two.",
                nameof(limits));
        if (limits.MaxSamplerAnisotropy != 0 &&
            (!float.IsFinite(limits.MaxSamplerAnisotropy) || limits.MaxSamplerAnisotropy < 1))
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (features.Supports(GraphicsFeature.SamplerAnisotropy) && limits.MaxSamplerAnisotropy <= 1)
            throw new ArgumentException(
                "An enabled anisotropy feature must report a maximum anisotropy greater than one.",
                nameof(limits));
        if (descriptorIndexing.HasAny && !features.Supports(GraphicsFeature.DescriptorIndexing))
            throw new ArgumentException(
                "Fine-grained descriptor-indexing capabilities require GraphicsFeature.DescriptorIndexing to be enabled.",
                nameof(descriptorIndexing));

        Features = features;
        QueueCapabilities = queueCapabilities;
        Limits = limits;
        DescriptorIndexing = descriptorIndexing;
        _formats = new GraphicsFormatCapabilities[formats.Length];
        for (int i = 0; i < formats.Length; i++)
        {
            ReadOnlyMemory<GraphicsFormat> compatible = formats[i].ViewCompatibleFormats;
            _formats[i] = formats[i] with
            {
                ViewCompatibleFormats = compatible.IsEmpty ? ReadOnlyMemory<GraphicsFormat>.Empty : compatible.ToArray()
            };
        }

        for (int i = 0; i < _formats.Length; i++)
        {
            if (_formats[i].Format == GraphicsFormat.Unknown || !Enum.IsDefined(_formats[i].Format))
                throw new ArgumentException("Format capability entries must identify a concrete format.", nameof(formats));
            if ((_formats[i].Support & ~allFormatSupport) != 0 ||
                (_formats[i].SampleCounts & ~allSampleCounts) != 0)
                throw new ArgumentException("A format capability entry contains unknown flags.", nameof(formats));

            GraphicsTextureAspect aspects = GraphicsFormatInfo.GetAspects(_formats[i].Format);
            if (aspects == GraphicsTextureAspect.Color &&
                (_formats[i].Support & GraphicsFormatSupport.DepthStencilAttachment) != 0)
                throw new ArgumentException(
                    $"Color format {_formats[i].Format} cannot advertise depth/stencil attachment support.",
                    nameof(formats));
            if (aspects != GraphicsTextureAspect.Color &&
                (_formats[i].Support & (GraphicsFormatSupport.ColorAttachment | GraphicsFormatSupport.Blend)) != 0)
                throw new ArgumentException(
                    $"Depth/stencil format {_formats[i].Format} cannot advertise color-attachment or blend support.",
                    nameof(formats));

            ReadOnlySpan<GraphicsFormat> compatibleFormats = _formats[i].ViewCompatibleFormats.Span;
            for (int compatibleIndex = 0; compatibleIndex < compatibleFormats.Length; compatibleIndex++)
            {
                GraphicsFormat compatibleFormat = compatibleFormats[compatibleIndex];
                if (compatibleFormat == GraphicsFormat.Unknown || !Enum.IsDefined(compatibleFormat) ||
                    compatibleFormat == _formats[i].Format)
                    throw new ArgumentException("Compatible view formats must be concrete formats distinct from the resource format.", nameof(formats));
                for (int previousIndex = 0; previousIndex < compatibleIndex; previousIndex++)
                {
                    if (compatibleFormats[previousIndex] == compatibleFormat)
                        throw new ArgumentException("A compatible view format cannot be listed more than once.", nameof(formats));
                }
            }

            for (int j = 0; j < i; j++)
            {
                if (_formats[j].Format == _formats[i].Format)
                    throw new ArgumentException($"Format '{_formats[i].Format}' is listed more than once.", nameof(formats));
            }
        }
    }

    public bool TryGetFormatCapabilities(
        GraphicsFormat format,
        out GraphicsFormatCapabilities capabilities)
    {
        for (int i = 0; i < _formats.Length; i++)
        {
            if (_formats[i].Format == format)
            {
                capabilities = _formats[i];
                return true;
            }
        }

        capabilities = default;
        return false;
    }

    public bool SupportsFormat(GraphicsFormat format, GraphicsFormatSupport support) =>
        TryGetFormatCapabilities(format, out GraphicsFormatCapabilities capabilities) &&
        capabilities.Supports(support);

    public bool SupportsSampleCount(GraphicsFormat format, uint sampleCount) =>
        TryGetFormatCapabilities(format, out GraphicsFormatCapabilities capabilities) &&
        capabilities.SupportsSampleCount(sampleCount);

    /// <summary>
    /// Validates a concrete pipeline layout against the reported binding-set count limit and verifies
    /// that every contained binding layout is live and owned by the pipeline layout's device.
    /// </summary>
    public void ValidatePipelineLayout(IGraphicsPipelineLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.IsDisposed || layout.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(layout));

        ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts = layout.BindingLayouts.Span;
        if (Limits.MaxBoundDescriptorSets != 0 &&
            (uint)bindingLayouts.Length > Limits.MaxBoundDescriptorSets)
            throw new GraphicsException(
                $"Pipeline layout contains {bindingLayouts.Length} binding sets; the device limit is {Limits.MaxBoundDescriptorSets}.");

        for (int i = 0; i < bindingLayouts.Length; i++)
        {
            IGraphicsBindingLayout bindingLayout = bindingLayouts[i] ??
                throw new ArgumentException("Pipeline layouts cannot contain null binding layouts.", nameof(layout));
            if (bindingLayout.IsDisposed || bindingLayout.Device.IsDisposed)
                throw new ObjectDisposedException(nameof(layout));
            if (!ReferenceEquals(bindingLayout.Device, layout.Device))
                throw new ArgumentException("All binding layouts in a pipeline layout must belong to its device.", nameof(layout));
        }
    }

    /// <summary>Validates a buffer descriptor against structural rules and the limits reported by this device.</summary>
    public void ValidateBufferDescription(in GraphicsBufferDescription description)
    {
        description.Validate();

        if (Limits.MaxBufferSizeInBytes != 0 &&
            description.SizeInBytes > Limits.MaxBufferSizeInBytes)
            throw new GraphicsException(
                $"Buffer size {description.SizeInBytes} exceeds the device limit of {Limits.MaxBufferSizeInBytes} bytes.");
    }

    /// <summary>Validates texture structure, implementation limits, and reported format/usage/sample support.</summary>
    public void ValidateTextureDescription(in GraphicsTextureDescription description)
    {
        description.Validate();

        ValidateTextureDimensions(description);

        GraphicsTextureAspect aspects = GraphicsFormatInfo.GetAspects(description.Format);
        if ((description.Usage & GraphicsTextureUsage.ColorAttachment) != 0 &&
            aspects != GraphicsTextureAspect.Color)
            throw new GraphicsException($"Format {description.Format} does not expose a color aspect.");
        if ((description.Usage & GraphicsTextureUsage.DepthStencilAttachment) != 0 &&
            (aspects & (GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil)) == 0)
            throw new GraphicsException($"Format {description.Format} does not expose a depth or stencil aspect.");

        if (!TryGetFormatCapabilities(description.Format, out GraphicsFormatCapabilities format))
            throw new GraphicsException($"The device did not report support information for format {description.Format}.");

        GraphicsFormatSupport required = GetRequiredFormatSupport(description.Usage);
        if (required != GraphicsFormatSupport.None && !format.Supports(required))
            throw new GraphicsException($"Format {description.Format} does not support all requested texture usages: {required}.");
        if (!format.SupportsSampleCount(description.SampleCount))
            throw new GraphicsException($"Format {description.Format} does not support {description.SampleCount} sample(s).");
    }

    /// <summary>
    /// Validates whether a view can be created for the resource: subresource/aspect rules,
    /// mutable-format compatibility, effective-format availability, and sample-count compatibility.
    /// It deliberately does not require the view format to support every usage of its parent resource.
    /// </summary>
    public void ValidateTextureViewDescription(
        in GraphicsTextureDescription texture,
        in GraphicsTextureViewDescription view)
    {
        ValidateTextureDescription(texture);
        view.Validate(texture);

        GraphicsFormat viewFormat = view.Format == GraphicsFormat.Unknown ? texture.Format : view.Format;
        if (!TryGetFormatCapabilities(texture.Format, out GraphicsFormatCapabilities resourceFormat) ||
            !resourceFormat.SupportsViewFormat(viewFormat))
            throw new GraphicsException($"The device does not report {viewFormat} as a compatible view format for {texture.Format}.");
        if (!TryGetFormatCapabilities(viewFormat, out GraphicsFormatCapabilities format))
            throw new GraphicsException($"The device did not report support information for texture-view format {viewFormat}.");
        if (!format.SupportsSampleCount(texture.SampleCount))
            throw new GraphicsException(
                $"Texture-view format {viewFormat} does not support {texture.SampleCount} sample(s).");
    }

    /// <summary>
    /// Validates that an already compatible view can be used for a particular operation. The requested usage
    /// must be present on the parent resource, supported by the view format, and compatible with the selected aspects.
    /// </summary>
    public void ValidateTextureViewUsage(
        in GraphicsTextureDescription texture,
        in GraphicsTextureViewDescription view,
        GraphicsTextureUsage requiredUsage)
    {
        ValidateTextureViewDescription(texture, view);
        const GraphicsTextureUsage allUsage =
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.Storage |
            GraphicsTextureUsage.ColorAttachment | GraphicsTextureUsage.DepthStencilAttachment |
            GraphicsTextureUsage.CopySource | GraphicsTextureUsage.CopyDestination |
            GraphicsTextureUsage.Present | GraphicsTextureUsage.ShadingRateAttachment;
        if (requiredUsage == GraphicsTextureUsage.None || (requiredUsage & ~allUsage) != 0)
            throw new ArgumentOutOfRangeException(nameof(requiredUsage));
        if ((texture.Usage & requiredUsage) != requiredUsage)
            throw new GraphicsException($"The parent texture does not declare required usage {requiredUsage}.");

        GraphicsFormat viewFormat = view.Format == GraphicsFormat.Unknown ? texture.Format : view.Format;
        if (!TryGetFormatCapabilities(viewFormat, out GraphicsFormatCapabilities format))
            throw new GraphicsException($"The device did not report support information for texture-view format {viewFormat}.");

        GraphicsFormatSupport requiredSupport = GetRequiredFormatSupport(requiredUsage);
        if (requiredSupport != GraphicsFormatSupport.None && !format.Supports(requiredSupport))
            throw new GraphicsException(
                $"Texture-view format {viewFormat} does not support the requested operation usage: {requiredUsage}.");

        GraphicsTextureAspect selectedAspects = view.GetEffectiveAspects(texture);
        if ((requiredUsage & GraphicsTextureUsage.ColorAttachment) != 0 &&
            selectedAspects != GraphicsTextureAspect.Color)
            throw new GraphicsException("Color-attachment views must expose only the color aspect.");
        if ((requiredUsage & GraphicsTextureUsage.DepthStencilAttachment) != 0 &&
            (selectedAspects == GraphicsTextureAspect.None ||
             (selectedAspects & GraphicsTextureAspect.Color) != 0 ||
             (selectedAspects & (GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil)) == 0))
            throw new GraphicsException("Depth/stencil attachment views must expose depth and/or stencil aspects.");
    }

    /// <summary>Validates vertex input counts and every attribute format against this device's limits.</summary>
    public void ValidateVertexInputLayout(GraphicsVertexInputLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.Validate();

        if (Limits.MaxVertexInputBindings != 0 &&
            (uint)layout.Buffers.Length > Limits.MaxVertexInputBindings)
            throw new GraphicsException(
                $"Vertex input uses {layout.Buffers.Length} bindings; the device limit is {Limits.MaxVertexInputBindings}.");

        if (Limits.MaxVertexInputAttributes != 0 &&
            (uint)layout.Attributes.Length > Limits.MaxVertexInputAttributes)
            throw new GraphicsException(
                $"Vertex input uses {layout.Attributes.Length} attributes; the device limit is {Limits.MaxVertexInputAttributes}.");

        ReadOnlySpan<GraphicsVertexAttribute> attributes = layout.Attributes;
        for (int i = 0; i < attributes.Length; i++)
        {
            GraphicsFormat format = attributes[i].Format;
            if (!SupportsFormat(format, GraphicsFormatSupport.VertexBuffer))
                throw new GraphicsException(
                    $"The device does not support {format} as a vertex-buffer attribute format.");
        }
    }

    private void ValidateTextureDimensions(in GraphicsTextureDescription description)
    {
        switch (description.Dimension)
        {
            case GraphicsTextureDimension.Texture1D:
                ValidateDimension(description.Width, Limits.MaxTextureDimension1D, "1D texture width");
                break;
            case GraphicsTextureDimension.Texture2D:
                ValidateDimension(description.Width, Limits.MaxTextureDimension2D, "2D texture width");
                ValidateDimension(description.Height, Limits.MaxTextureDimension2D, "2D texture height");
                break;
            case GraphicsTextureDimension.Cube:
                // Cube-map dimensions are independently reported by native APIs; do not constrain
                // this resource by the non-cube 2D image limit.
                ValidateDimension(description.Width, Limits.MaxCubeTextureDimension, "cube texture dimension");
                break;
            case GraphicsTextureDimension.Texture3D:
                ValidateDimension(description.Width, Limits.MaxTextureDimension3D, "3D texture width");
                ValidateDimension(description.Height, Limits.MaxTextureDimension3D, "3D texture height");
                ValidateDimension(description.Depth, Limits.MaxTextureDimension3D, "3D texture depth");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(description));
        }

        ValidateDimension(description.ArrayLayers, Limits.MaxTextureArrayLayers, "texture array layers");
    }

    private static void ValidateDimension(uint requested, uint limit, string name)
    {
        if (limit != 0 && requested > limit)
            throw new GraphicsException($"Requested {name} ({requested}) exceeds the device limit ({limit}).");
    }

    private static GraphicsFormatSupport GetRequiredFormatSupport(GraphicsTextureUsage usage)
    {
        GraphicsFormatSupport required = GraphicsFormatSupport.None;
        if ((usage & GraphicsTextureUsage.Sampled) != 0) required |= GraphicsFormatSupport.Sampled;
        if ((usage & GraphicsTextureUsage.Storage) != 0) required |= GraphicsFormatSupport.Storage;
        if ((usage & GraphicsTextureUsage.ColorAttachment) != 0) required |= GraphicsFormatSupport.ColorAttachment;
        if ((usage & GraphicsTextureUsage.DepthStencilAttachment) != 0) required |= GraphicsFormatSupport.DepthStencilAttachment;
        if ((usage & GraphicsTextureUsage.CopySource) != 0) required |= GraphicsFormatSupport.CopySource;
        if ((usage & GraphicsTextureUsage.CopyDestination) != 0) required |= GraphicsFormatSupport.CopyDestination;
        if ((usage & GraphicsTextureUsage.ShadingRateAttachment) != 0) required |= GraphicsFormatSupport.ShadingRateAttachment;
        // Presentation compatibility is surface/queue-specific, not a per-format capability.
        return required;
    }

    public void ValidateSamplerDescription(in GraphicsSamplerDescription description)
    {
        description.Validate();

        if (description.MaxAnisotropy > 1)
        {
            if (!Features.Supports(GraphicsFeature.SamplerAnisotropy))
                throw new GraphicsException("Sampler anisotropy is not supported by this device.");
            if (description.MaxAnisotropy > Limits.MaxSamplerAnisotropy)
                throw new GraphicsException($"Requested anisotropy exceeds the device limit of {Limits.MaxSamplerAnisotropy}.");
        }
    }
}
