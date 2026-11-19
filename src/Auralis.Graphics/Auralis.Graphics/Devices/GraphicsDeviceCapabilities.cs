using Auralis.Graphics.Abstractions;
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
    float MaxSamplerAnisotropy);

/// <summary>Describes the operations supported for a particular texture format.</summary>
public readonly record struct GraphicsFormatCapabilities(
    GraphicsFormat Format,
    GraphicsFormatSupport Support,
    GraphicsSampleCountFlags SampleCounts)
{
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

    public ReadOnlySpan<GraphicsFormatCapabilities> Formats => _formats;

    public GraphicsDeviceCapabilities(
        GraphicsFeatureSet features,
        GraphicsQueueCapabilities queueCapabilities,
        GraphicsDeviceLimits limits,
        ReadOnlySpan<GraphicsFormatCapabilities> formats)
    {
        const GraphicsFeature allFeatures =
            GraphicsFeature.ComputeShaders | GraphicsFeature.IndirectDrawing |
            GraphicsFeature.MultiDrawIndirect | GraphicsFeature.DescriptorIndexing |
            GraphicsFeature.SeparateComputeQueue | GraphicsFeature.SeparateTransferQueue |
            GraphicsFeature.TimestampQueries | GraphicsFeature.SamplerAnisotropy |
            GraphicsFeature.TessellationShaders | GraphicsFeature.GeometryShaders |
            GraphicsFeature.MeshShaders | GraphicsFeature.RayTracing |
            GraphicsFeature.VariableRateShading | GraphicsFeature.ConservativeRasterization |
            GraphicsFeature.TimelineSynchronization | GraphicsFeature.SparseResources |
            GraphicsFeature.MultiViewport | GraphicsFeature.DepthBoundsTest |
            GraphicsFeature.LogicOperations | GraphicsFeature.WideLines |
            GraphicsFeature.DepthClamp | GraphicsFeature.FillModeNonSolid;
        const GraphicsQueueCapabilities allQueues =
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute |
            GraphicsQueueCapabilities.Copy | GraphicsQueueCapabilities.Present;
        const GraphicsFormatSupport allFormatSupport =
            GraphicsFormatSupport.Sampled | GraphicsFormatSupport.Storage |
            GraphicsFormatSupport.ColorAttachment | GraphicsFormatSupport.DepthStencilAttachment |
            GraphicsFormatSupport.Blend | GraphicsFormatSupport.VertexBuffer |
            GraphicsFormatSupport.CopySource | GraphicsFormatSupport.CopyDestination |
            GraphicsFormatSupport.LinearFiltering;
        const GraphicsSampleCountFlags allSampleCounts =
            GraphicsSampleCountFlags.One | GraphicsSampleCountFlags.Two |
            GraphicsSampleCountFlags.Four | GraphicsSampleCountFlags.Eight |
            GraphicsSampleCountFlags.Sixteen | GraphicsSampleCountFlags.ThirtyTwo |
            GraphicsSampleCountFlags.SixtyFour;

        if ((features.Enabled & ~allFeatures) != 0)
            throw new ArgumentOutOfRangeException(nameof(features));
        if ((queueCapabilities & ~allQueues) != 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapabilities));
        if (limits.MaxSamplerAnisotropy != 0 &&
            (!float.IsFinite(limits.MaxSamplerAnisotropy) || limits.MaxSamplerAnisotropy < 1))
            throw new ArgumentOutOfRangeException(nameof(limits));

        Features = features;
        QueueCapabilities = queueCapabilities;
        Limits = limits;
        _formats = formats.ToArray();

        for (int i = 0; i < _formats.Length; i++)
        {
            if (_formats[i].Format == GraphicsFormat.Unknown || !Enum.IsDefined(_formats[i].Format))
                throw new ArgumentException("Format capability entries must identify a concrete format.", nameof(formats));
            if ((_formats[i].Support & ~allFormatSupport) != 0 ||
                (_formats[i].SampleCounts & ~allSampleCounts) != 0)
                throw new ArgumentException("A format capability entry contains unknown flags.", nameof(formats));

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

    /// <summary>Validates structural constraints and the format/usage/sample support reported by this device.</summary>
    public void ValidateTextureDescription(in GraphicsTextureDescription description)
    {
        description.Validate();

        if (!TryGetFormatCapabilities(description.Format, out GraphicsFormatCapabilities format))
            throw new GraphicsException($"The device did not report support information for format {description.Format}.");

        GraphicsFormatSupport required = GraphicsFormatSupport.None;
        if ((description.Usage & GraphicsTextureUsage.Sampled) != 0) required |= GraphicsFormatSupport.Sampled;
        if ((description.Usage & GraphicsTextureUsage.Storage) != 0) required |= GraphicsFormatSupport.Storage;
        if ((description.Usage & GraphicsTextureUsage.ColorAttachment) != 0) required |= GraphicsFormatSupport.ColorAttachment;
        if ((description.Usage & GraphicsTextureUsage.DepthStencilAttachment) != 0) required |= GraphicsFormatSupport.DepthStencilAttachment;
        if ((description.Usage & GraphicsTextureUsage.CopySource) != 0) required |= GraphicsFormatSupport.CopySource;
        if ((description.Usage & GraphicsTextureUsage.CopyDestination) != 0) required |= GraphicsFormatSupport.CopyDestination;

        if (!format.Supports(required))
            throw new GraphicsException($"Format {description.Format} does not support all requested texture usages: {required}.");
        if (!format.SupportsSampleCount(description.SampleCount))
            throw new GraphicsException($"Format {description.Format} does not support {description.SampleCount} sample(s).");
    }

    public void ValidateSamplerDescription(in GraphicsSamplerDescription description)
    {
        description.Validate();

        if (description.MaxAnisotropy > 1)
        {
            if (!Features.Supports(GraphicsFeature.SamplerAnisotropy))
                throw new GraphicsException("Sampler anisotropy is not supported by this device.");
            if (Limits.MaxSamplerAnisotropy > 0 && description.MaxAnisotropy > Limits.MaxSamplerAnisotropy)
                throw new GraphicsException($"Requested anisotropy exceeds the device limit of {Limits.MaxSamplerAnisotropy}.");
        }
    }
}
