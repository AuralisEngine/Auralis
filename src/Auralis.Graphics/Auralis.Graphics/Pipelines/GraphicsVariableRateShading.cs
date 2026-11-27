using Auralis.Graphics.Commands;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Pipelines;

/// <summary>Per-draw or per-region shading rate. Unsupported rates must be rejected through capabilities.</summary>
public enum GraphicsShadingRate
{
    Rate1x1,
    Rate1x2,
    Rate2x1,
    Rate2x2,
    Rate2x4,
    Rate4x2,
    Rate4x4
}

/// <summary>Combines pipeline, primitive, and attachment shading-rate sources.</summary>
public enum GraphicsShadingRateCombiner
{
    Passthrough,
    Override,
    Min,
    Max,
    Multiply
}

/// <summary>Shading-rate limits and supported rates reported by a device.</summary>
public readonly record struct GraphicsShadingRateCapabilities(
    uint AttachmentTileWidth,
    uint AttachmentTileHeight,
    uint MaximumFragmentSizeWidth,
    uint MaximumFragmentSizeHeight,
    ulong SupportedRatesMask)
{
    private const ulong AllSupportedRatesMask = (1UL << 7) - 1;

    public bool Supports(GraphicsShadingRate rate) =>
        Enum.IsDefined(rate) && (SupportedRatesMask & (1UL << (int)rate)) != 0;

    /// <summary>
    /// Validates a requested per-draw shading-rate state against the reported rate mask and
    /// rejects undefined combiner enum values. Backends must still reject defined combiners that
    /// their native API cannot represent with the common semantics.
    /// </summary>
    public void ValidateRequest(
        GraphicsShadingRate rate,
        GraphicsShadingRateCombiner primitiveCombiner,
        GraphicsShadingRateCombiner attachmentCombiner)
    {
        Validate();
        if (!Enum.IsDefined(rate))
            throw new ArgumentOutOfRangeException(nameof(rate));
        if (!Supports(rate))
            throw new ArgumentException($"Shading rate {rate} is not supported by this device.", nameof(rate));
        if (!Enum.IsDefined(primitiveCombiner))
            throw new ArgumentOutOfRangeException(nameof(primitiveCombiner));
        if (!Enum.IsDefined(attachmentCombiner))
            throw new ArgumentOutOfRangeException(nameof(attachmentCombiner));
    }

    /// <summary>Validates internal consistency of reported shading-rate capabilities.</summary>
    public void Validate()
    {
        if (AttachmentTileWidth == 0 || AttachmentTileHeight == 0 ||
            MaximumFragmentSizeWidth == 0 || MaximumFragmentSizeHeight == 0)
            throw new ArgumentOutOfRangeException(nameof(AttachmentTileWidth));
        if ((MaximumFragmentSizeWidth & (MaximumFragmentSizeWidth - 1)) != 0 ||
            (MaximumFragmentSizeHeight & (MaximumFragmentSizeHeight - 1)) != 0)
            throw new ArgumentException("Maximum fragment dimensions must be powers of two.");
        if ((AttachmentTileWidth & (AttachmentTileWidth - 1)) != 0 ||
            (AttachmentTileHeight & (AttachmentTileHeight - 1)) != 0)
            throw new ArgumentException("Shading-rate attachment tile dimensions must be powers of two.");
        if (SupportedRatesMask == 0 || (SupportedRatesMask & ~AllSupportedRatesMask) != 0)
            throw new ArgumentOutOfRangeException(nameof(SupportedRatesMask));
        if (!Supports(GraphicsShadingRate.Rate1x1))
            throw new ArgumentException("Devices must report support for the 1x1 baseline shading rate.");
    }
}

/// <summary>Optional device extension for variable-rate shading capability queries.</summary>
public interface IGraphicsVariableRateShadingDevice
{
    GraphicsShadingRateCapabilities ShadingRateCapabilities { get; }
}

/// <summary>
/// Optional command-list extension for dynamic shading-rate state and a shading-rate attachment.
/// The attachment contents are application-authored; this API does not prescribe a foveation policy.
/// </summary>
public interface IGraphicsShadingRateCommandList : IGraphicsCommandList
{
    /// <summary>
    /// Sets the dynamic rate and combiners. Implementations must validate the request against
    /// <see cref="GraphicsShadingRateCapabilities.ValidateRequest"/> and enforce native combiner limitations.
    /// </summary>
    void SetShadingRate(
        GraphicsShadingRate rate,
        GraphicsShadingRateCombiner primitiveCombiner = GraphicsShadingRateCombiner.Passthrough,
        GraphicsShadingRateCombiner attachmentCombiner = GraphicsShadingRateCombiner.Passthrough);

    /// <summary>
    /// Sets the optional shading-rate image. Non-null views must belong to this device and expose
    /// the shading-rate-attachment usage and aspect supported by the active backend.
    /// </summary>
    void SetShadingRateImage(IGraphicsTextureView? view);
}
