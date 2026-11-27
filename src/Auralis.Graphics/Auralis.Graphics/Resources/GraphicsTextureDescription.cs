namespace Auralis.Graphics.Resources;

/// <summary>Describes the dimensions, format, and intended usage of a texture.</summary>
public readonly record struct GraphicsTextureDescription(
    GraphicsTextureDimension Dimension,
    GraphicsFormat Format,
    uint Width,
    uint Height,
    uint Depth,
    uint MipLevels,
    uint ArrayLayers,
    uint SampleCount,
    GraphicsTextureUsage Usage,
    GraphicsResourceCreationOptions CreationOptions = GraphicsResourceCreationOptions.None)
{
    public void Validate()
    {
        const GraphicsTextureUsage allUsage =
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.Storage |
            GraphicsTextureUsage.ColorAttachment | GraphicsTextureUsage.DepthStencilAttachment |
            GraphicsTextureUsage.CopySource | GraphicsTextureUsage.CopyDestination |
            GraphicsTextureUsage.Present | GraphicsTextureUsage.ShadingRateAttachment;
        const GraphicsResourceCreationOptions allOptions =
            GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing |
            GraphicsResourceCreationOptions.MutableFormat | GraphicsResourceCreationOptions.RequireExplicitMemoryBinding |
            GraphicsResourceCreationOptions.SparseResidency;

        if (!Enum.IsDefined(Dimension))
            throw new ArgumentOutOfRangeException(nameof(Dimension));
        if (Format == GraphicsFormat.Unknown || !Enum.IsDefined(Format))
            throw new ArgumentOutOfRangeException(nameof(Format));
        if (Width == 0 || Height == 0 || Depth == 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Texture dimensions must be greater than zero.");
        if (MipLevels == 0)
            throw new ArgumentOutOfRangeException(nameof(MipLevels));
        if (ArrayLayers == 0)
            throw new ArgumentOutOfRangeException(nameof(ArrayLayers));
        if (Usage == GraphicsTextureUsage.None || (Usage & ~allUsage) != 0)
            throw new ArgumentOutOfRangeException(nameof(Usage));

        GraphicsTextureAspect formatAspects = GraphicsFormatInfo.GetAspects(Format);
        const GraphicsTextureUsage colorAspectUsages =
            GraphicsTextureUsage.ColorAttachment |
            GraphicsTextureUsage.Present |
            GraphicsTextureUsage.ShadingRateAttachment;
        if ((Usage & colorAspectUsages) != 0 && formatAspects != GraphicsTextureAspect.Color)
            throw new ArgumentException(
                "Color, presentation, and shading-rate usages require a color-aspect texture format.",
                nameof(Usage));
        if ((Usage & GraphicsTextureUsage.DepthStencilAttachment) != 0 &&
            (formatAspects & (GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil)) == 0)
            throw new ArgumentException(
                "Depth/stencil attachment usage requires a depth or stencil texture format.",
                nameof(Usage));
        const GraphicsResourceCreationOptions conflictingOptions =
            GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing;
        const GraphicsResourceCreationOptions sparseConflicts =
            GraphicsResourceCreationOptions.DedicatedAllocation |
            GraphicsResourceCreationOptions.AllowAliasing |
            GraphicsResourceCreationOptions.RequireExplicitMemoryBinding;
        if ((CreationOptions & ~allOptions) != 0 ||
            (CreationOptions & conflictingOptions) == conflictingOptions ||
            ((CreationOptions & GraphicsResourceCreationOptions.SparseResidency) != 0 &&
             (CreationOptions & sparseConflicts) != 0))
            throw new ArgumentOutOfRangeException(nameof(CreationOptions));

        switch (Dimension)
        {
            case GraphicsTextureDimension.Texture1D when Height != 1 || Depth != 1:
                throw new ArgumentException("A 1D texture must have height and depth equal to one.");
            case GraphicsTextureDimension.Texture2D when Depth != 1:
                throw new ArgumentException("A 2D texture must have depth equal to one.");
            case GraphicsTextureDimension.Texture3D when ArrayLayers != 1:
                throw new ArgumentException("A 3D texture cannot have array layers.");
            case GraphicsTextureDimension.Cube when Width != Height || Depth != 1 || ArrayLayers < 6 || ArrayLayers % 6 != 0:
                throw new ArgumentException("Cube textures must be square and use a multiple of six array layers.");
        }

        if (SampleCount is 0 or > 64 || (SampleCount & (SampleCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(SampleCount), "Sample count must be a power of two from 1 through 64.");

        if (SampleCount > 1 && (Dimension != GraphicsTextureDimension.Texture2D || MipLevels != 1))
            throw new ArgumentException("Multisampled textures must be 2D textures with a single mip level.");

        if ((CreationOptions & GraphicsResourceCreationOptions.SparseResidency) != 0 &&
            Dimension == GraphicsTextureDimension.Texture1D)
            throw new ArgumentException("Sparse residency is not available for 1D textures.", nameof(Dimension));

        if ((Usage & GraphicsTextureUsage.ShadingRateAttachment) != 0 &&
            (Dimension != GraphicsTextureDimension.Texture2D || SampleCount != 1))
            throw new ArgumentException("Shading-rate attachment textures must be single-sample 2D textures.", nameof(Usage));

        uint maximumDimension = Dimension switch
        {
            GraphicsTextureDimension.Texture1D => Width,
            GraphicsTextureDimension.Texture2D or GraphicsTextureDimension.Cube => Math.Max(Width, Height),
            GraphicsTextureDimension.Texture3D => Math.Max(Width, Math.Max(Height, Depth)),
            _ => 1
        };

        uint maximumMipLevels = 1;
        while (maximumDimension > 1)
        {
            maximumDimension >>= 1;
            maximumMipLevels++;
        }

        if (MipLevels > maximumMipLevels)
            throw new ArgumentOutOfRangeException(nameof(MipLevels), "Mip count exceeds the dimensions of the texture.");
    }
}

/// <summary>Dimensionality of a texture view.</summary>
public enum GraphicsTextureViewType
{
    Texture1D,
    Texture1DArray,
    Texture2D,
    Texture2DArray,
    Texture2DMultisample,
    Texture2DMultisampleArray,
    Texture3D,
    Cube,
    CubeArray
}

/// <summary>
/// Subresource, format, and aspect selection for a texture view. Unknown format inherits the texture format.
/// None aspects selects every aspect exposed by the effective view format. Reinterpretation requires MutableFormat;
/// exact compatibility is device-reported through GraphicsFormatCapabilities.
/// </summary>
public readonly record struct GraphicsTextureViewDescription(
    GraphicsFormat Format,
    GraphicsTextureViewType ViewType,
    uint BaseMipLevel,
    uint MipLevelCount,
    uint BaseArrayLayer,
    uint ArrayLayerCount,
    GraphicsTextureAspect Aspects = GraphicsTextureAspect.None)
{
    /// <summary>
    /// Returns the selected aspects. None means all aspects exposed by the effective view format.
    /// </summary>
    public GraphicsTextureAspect GetEffectiveAspects(in GraphicsTextureDescription texture)
    {
        GraphicsFormat viewFormat = Format == GraphicsFormat.Unknown ? texture.Format : Format;
        return Aspects == GraphicsTextureAspect.None
            ? GraphicsFormatInfo.GetAspects(viewFormat)
            : Aspects;
    }

    public void Validate(in GraphicsTextureDescription texture)
    {
        texture.Validate();

        if (!Enum.IsDefined(ViewType))
            throw new ArgumentOutOfRangeException(nameof(ViewType));
        if (Format != GraphicsFormat.Unknown && !Enum.IsDefined(Format))
            throw new ArgumentOutOfRangeException(nameof(Format));
        const GraphicsTextureAspect allAspects =
            GraphicsTextureAspect.Color | GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil;
        if ((Aspects & ~allAspects) != 0)
            throw new ArgumentOutOfRangeException(nameof(Aspects));
        if (MipLevelCount == 0 || BaseMipLevel >= texture.MipLevels ||
            MipLevelCount > texture.MipLevels - BaseMipLevel)
            throw new ArgumentOutOfRangeException(nameof(MipLevelCount));
        if (ArrayLayerCount == 0 || BaseArrayLayer >= texture.ArrayLayers ||
            ArrayLayerCount > texture.ArrayLayers - BaseArrayLayer)
            throw new ArgumentOutOfRangeException(nameof(ArrayLayerCount));

        GraphicsFormat viewFormat = Format == GraphicsFormat.Unknown ? texture.Format : Format;
        GraphicsTextureAspect parentAspects = GraphicsFormatInfo.GetAspects(texture.Format);
        GraphicsTextureAspect viewFormatAspects = GraphicsFormatInfo.GetAspects(viewFormat);
        GraphicsTextureAspect selectedAspects = Aspects == GraphicsTextureAspect.None
            ? viewFormatAspects
            : Aspects;
        if (selectedAspects == GraphicsTextureAspect.None ||
            (selectedAspects & ~viewFormatAspects) != 0 ||
            (selectedAspects & ~parentAspects) != 0)
            throw new ArgumentException(
                "A texture view must select one or more aspects present in both its resource format and effective view format.",
                nameof(Aspects));

        if (viewFormat != texture.Format)
        {
            if ((texture.CreationOptions & GraphicsResourceCreationOptions.MutableFormat) == 0)
                throw new ArgumentException(
                    "A texture must be created with MutableFormat before a view can reinterpret its format.",
                    nameof(Format));
            // Exact mutable-format compatibility is backend/device specific and is checked by
            // GraphicsDeviceCapabilities.ValidateTextureViewDescription.
        }

        bool compatible = ViewType switch
        {
            GraphicsTextureViewType.Texture1D =>
                texture.Dimension == GraphicsTextureDimension.Texture1D && ArrayLayerCount == 1,
            GraphicsTextureViewType.Texture1DArray =>
                texture.Dimension == GraphicsTextureDimension.Texture1D,
            GraphicsTextureViewType.Texture2D =>
                (texture.Dimension is GraphicsTextureDimension.Texture2D or GraphicsTextureDimension.Cube) &&
                texture.SampleCount == 1 && ArrayLayerCount == 1,
            GraphicsTextureViewType.Texture2DArray =>
                (texture.Dimension is GraphicsTextureDimension.Texture2D or GraphicsTextureDimension.Cube) &&
                texture.SampleCount == 1,
            GraphicsTextureViewType.Texture2DMultisample =>
                texture.Dimension == GraphicsTextureDimension.Texture2D &&
                texture.SampleCount > 1 && ArrayLayerCount == 1,
            GraphicsTextureViewType.Texture2DMultisampleArray =>
                texture.Dimension == GraphicsTextureDimension.Texture2D && texture.SampleCount > 1,
            GraphicsTextureViewType.Texture3D =>
                texture.Dimension == GraphicsTextureDimension.Texture3D,
            GraphicsTextureViewType.Cube =>
                texture.Dimension == GraphicsTextureDimension.Cube && ArrayLayerCount == 6,
            GraphicsTextureViewType.CubeArray =>
                texture.Dimension == GraphicsTextureDimension.Cube && ArrayLayerCount >= 6,
            _ => false
        };

        if (!compatible)
            throw new ArgumentException("The requested view type is incompatible with the source texture.", nameof(ViewType));

        if ((ViewType is GraphicsTextureViewType.Cube or GraphicsTextureViewType.CubeArray) &&
            (BaseArrayLayer % 6 != 0 || ArrayLayerCount % 6 != 0))
            throw new ArgumentException("Cube texture view layers must be aligned to six faces.");
    }


}
