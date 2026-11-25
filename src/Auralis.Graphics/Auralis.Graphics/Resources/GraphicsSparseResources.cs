using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Resources;

/// <summary>Backend-reported sparse texture tile and mip-tail requirements.</summary>
public readonly record struct GraphicsSparseTextureRequirements(
    uint TileWidth,
    uint TileHeight,
    uint TileDepth,
    ulong TileSizeInBytes,
    uint FirstMipTailLevel,
    ulong MipTailSizeInBytes,
    ulong MipTailOffsetInBytes,
    ulong MipTailStrideInBytes,
    bool SingleMipTail)
{
    public void Validate(in GraphicsTextureDescription texture)
    {
        texture.Validate();
        if (TileWidth == 0 || TileHeight == 0 || TileDepth == 0 || TileSizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(TileWidth), "Sparse tile dimensions must be non-zero.");
        if (FirstMipTailLevel > texture.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(FirstMipTailLevel));
        if (MipTailSizeInBytes == 0 && (MipTailOffsetInBytes != 0 || MipTailStrideInBytes != 0))
            throw new ArgumentException("Mip-tail offset and stride must be zero when no mip tail is present.");
        if (MipTailSizeInBytes != 0 && FirstMipTailLevel == texture.MipLevels)
            throw new ArgumentException("A mip tail must begin before the final mip level.");
        if (SingleMipTail && MipTailStrideInBytes != 0)
            throw new ArgumentException("A single mip tail cannot declare a per-layer stride.");
    }
}

/// <summary>Coordinates and extent of a sparse texture tile region, expressed in tile units.</summary>
public readonly record struct GraphicsSparseTextureRegion(
    uint MipLevel,
    uint ArrayLayer,
    uint TileX,
    uint TileY,
    uint TileZ,
    uint TileWidth,
    uint TileHeight,
    uint TileDepth)
{
    public void Validate(in GraphicsTextureDescription texture, in GraphicsSparseTextureRequirements requirements)
    {
        if (MipLevel >= texture.MipLevels || MipLevel >= requirements.FirstMipTailLevel)
            throw new ArgumentOutOfRangeException(nameof(MipLevel), "Regular tile regions must precede the mip tail.");
        if (ArrayLayer >= texture.ArrayLayers)
            throw new ArgumentOutOfRangeException(nameof(ArrayLayer));
        if (TileWidth == 0 || TileHeight == 0 || TileDepth == 0)
            throw new ArgumentOutOfRangeException(nameof(TileWidth));
        if (TileX > uint.MaxValue - TileWidth || TileY > uint.MaxValue - TileHeight ||
            TileZ > uint.MaxValue - TileDepth)
            throw new ArgumentOutOfRangeException(nameof(TileWidth), "Tile region coordinate overflow.");
    }
}

/// <summary>One sparse texture mapping. A null allocation unmaps the selected tiles or mip tail.</summary>
public readonly record struct GraphicsSparseTextureBinding(
    IGraphicsTexture Texture,
    GraphicsSparseTextureRegion Region,
    IGraphicsMemoryAllocation? Allocation,
    ulong AllocationOffsetInBytes,
    bool IsMipTail = false)
{
    public void Validate(in GraphicsSparseTextureRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(Texture);
        if (Texture.IsDisposed || Texture.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(Texture));
        requirements.Validate(Texture.Description);
        if (IsMipTail)
        {
            if (requirements.MipTailSizeInBytes == 0)
                throw new ArgumentException("This texture has no mip tail.");
            if (Region.MipLevel != requirements.FirstMipTailLevel ||
                Region.TileX != 0 || Region.TileY != 0 || Region.TileZ != 0 ||
                Region.TileWidth != 0 || Region.TileHeight != 0 || Region.TileDepth != 0)
                throw new ArgumentException("Mip-tail bindings identify the first tail mip and use an empty tile region.");
            if (requirements.SingleMipTail && Region.ArrayLayer != 0)
                throw new ArgumentOutOfRangeException(nameof(Region), "A single mip tail is bound through array layer zero.");
            if (!requirements.SingleMipTail && Region.ArrayLayer >= Texture.Description.ArrayLayers)
                throw new ArgumentOutOfRangeException(nameof(Region));
        }
        else
        {
            Region.Validate(Texture.Description, requirements);
        }

        if (Allocation is null)
        {
            if (AllocationOffsetInBytes != 0)
                throw new ArgumentException("Unmapping cannot specify a memory offset.");
            return;
        }

        if (Allocation.IsDisposed || Allocation.Device.IsDisposed ||
            !ReferenceEquals(Allocation.Device, Texture.Device))
            throw new ArgumentException("Sparse texture and allocation must belong to the same live device.");
        if (AllocationOffsetInBytes > Allocation.SizeInBytes)
            throw new ArgumentOutOfRangeException(nameof(AllocationOffsetInBytes));
        ulong requiredSize = IsMipTail
            ? requirements.MipTailSizeInBytes
            : checked((ulong)Region.TileWidth * Region.TileHeight * Region.TileDepth * requirements.TileSizeInBytes);
        if (requiredSize > Allocation.SizeInBytes - AllocationOffsetInBytes)
            throw new ArgumentException("The allocation does not contain the complete sparse mapping range.");
    }
}

/// <summary>Optional sparse-resource extension. Binding operations are ordered on the supplied queue.</summary>
public interface IGraphicsSparseResourceDevice
{
    IGraphicsDevice Device { get; }
    GraphicsSparseTextureRequirements GetSparseTextureRequirements(IGraphicsTexture texture);
    void BindSparseTextures(
        IGraphicsCommandQueue queue,
        ReadOnlySpan<GraphicsSparseTextureBinding> bindings,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0);
}
