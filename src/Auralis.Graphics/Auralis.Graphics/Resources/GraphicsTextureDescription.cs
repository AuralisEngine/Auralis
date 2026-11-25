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
            GraphicsTextureUsage.Present;
        const GraphicsResourceCreationOptions allOptions =
            GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing |
            GraphicsResourceCreationOptions.MutableFormat | GraphicsResourceCreationOptions.RequireExplicitMemoryBinding |
            GraphicsResourceCreationOptions.Sparse;

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
        const GraphicsResourceCreationOptions conflictingOptions =
            GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing;
        if ((CreationOptions & ~allOptions) != 0 ||
            (CreationOptions & conflictingOptions) == conflictingOptions ||
            ((CreationOptions & GraphicsResourceCreationOptions.RequireExplicitMemoryBinding) != 0 &&
             (CreationOptions & (GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.Sparse)) != 0) ||
            ((CreationOptions & GraphicsResourceCreationOptions.Sparse) != 0 &&
             (CreationOptions & GraphicsResourceCreationOptions.DedicatedAllocation) != 0))
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
/// Subresource and format selection for a texture view. Unknown format inherits the texture format.
/// Reinterpretation is limited to the explicitly documented compatibility pairs and requires MutableFormat.
/// </summary>
public readonly record struct GraphicsTextureViewDescription(
    GraphicsFormat Format,
    GraphicsTextureViewType ViewType,
    uint BaseMipLevel,
    uint MipLevelCount,
    uint BaseArrayLayer,
    uint ArrayLayerCount)
{
    public void Validate(in GraphicsTextureDescription texture)
    {
        texture.Validate();

        if (!Enum.IsDefined(ViewType))
            throw new ArgumentOutOfRangeException(nameof(ViewType));
        if (Format != GraphicsFormat.Unknown && !Enum.IsDefined(Format))
            throw new ArgumentOutOfRangeException(nameof(Format));
        if (MipLevelCount == 0 || BaseMipLevel >= texture.MipLevels ||
            MipLevelCount > texture.MipLevels - BaseMipLevel)
            throw new ArgumentOutOfRangeException(nameof(MipLevelCount));
        if (ArrayLayerCount == 0 || BaseArrayLayer >= texture.ArrayLayers ||
            ArrayLayerCount > texture.ArrayLayers - BaseArrayLayer)
            throw new ArgumentOutOfRangeException(nameof(ArrayLayerCount));

        GraphicsFormat viewFormat = Format == GraphicsFormat.Unknown ? texture.Format : Format;
        if (viewFormat != texture.Format)
        {
            if ((texture.CreationOptions & GraphicsResourceCreationOptions.MutableFormat) == 0)
                throw new ArgumentException(
                    "A texture must be created with MutableFormat before a view can reinterpret its format.",
                    nameof(Format));
            if (!AreViewFormatsCompatible(texture.Format, viewFormat))
                throw new ArgumentException(
                    $"Texture format {viewFormat} is not in the view compatibility class of {texture.Format}.",
                    nameof(Format));
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

    private static bool AreViewFormatsCompatible(GraphicsFormat source, GraphicsFormat view) =>
        (source, view) switch
        {
            (GraphicsFormat.RGBA8UNorm, GraphicsFormat.RGBA8UNormSrgb) or
            (GraphicsFormat.RGBA8UNormSrgb, GraphicsFormat.RGBA8UNorm) or
            (GraphicsFormat.BGRA8UNorm, GraphicsFormat.BGRA8UNormSrgb) or
            (GraphicsFormat.BGRA8UNormSrgb, GraphicsFormat.BGRA8UNorm) or
            (GraphicsFormat.BC1UNorm, GraphicsFormat.BC1UNormSrgb) or
            (GraphicsFormat.BC1UNormSrgb, GraphicsFormat.BC1UNorm) or
            (GraphicsFormat.BC3UNorm, GraphicsFormat.BC3UNormSrgb) or
            (GraphicsFormat.BC3UNormSrgb, GraphicsFormat.BC3UNorm) or
            (GraphicsFormat.BC7UNorm, GraphicsFormat.BC7UNormSrgb) or
            (GraphicsFormat.BC7UNormSrgb, GraphicsFormat.BC7UNorm) or
            (GraphicsFormat.ASTC4x4UNorm, GraphicsFormat.ASTC4x4UNormSrgb) or
            (GraphicsFormat.ASTC4x4UNormSrgb, GraphicsFormat.ASTC4x4UNorm) => true,
            _ => false
        };
}
