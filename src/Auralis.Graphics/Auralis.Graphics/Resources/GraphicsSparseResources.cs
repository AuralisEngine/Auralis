using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Synchronization;

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
        if ((texture.CreationOptions & GraphicsResourceCreationOptions.SparseResidency) == 0)
            throw new ArgumentException("The texture must be created with SparseResidency.", nameof(texture));
        if (TileWidth == 0 || TileHeight == 0 || TileDepth == 0 || TileSizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(TileWidth), "Sparse tile dimensions and tile size must be non-zero.");
        if (texture.Dimension == GraphicsTextureDimension.Texture1D || texture.SampleCount != 1)
            throw new ArgumentException("Sparse texture requirements are valid only for non-1D, single-sample textures.");
        if (FirstMipTailLevel > texture.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(FirstMipTailLevel));
        if (MipTailSizeInBytes == 0 && FirstMipTailLevel != texture.MipLevels)
            throw new ArgumentException("When no mip tail exists, FirstMipTailLevel must equal the texture mip count.");
        if (MipTailSizeInBytes == 0 && (MipTailOffsetInBytes != 0 || MipTailStrideInBytes != 0))
            throw new ArgumentException("Mip-tail offset and stride must be zero when no mip tail is present.");
        if (MipTailSizeInBytes != 0 && FirstMipTailLevel == texture.MipLevels)
            throw new ArgumentException("A mip tail must begin before the final mip level.");
        if (SingleMipTail && MipTailStrideInBytes != 0)
            throw new ArgumentException("A single mip tail cannot declare a per-layer stride.");
        if (!SingleMipTail && MipTailSizeInBytes != 0 && texture.ArrayLayers > 1 &&
            MipTailStrideInBytes < MipTailSizeInBytes)
            throw new ArgumentException("Per-layer mip-tail stride cannot be smaller than the mip-tail size.");
        if (MipTailSizeInBytes != 0 && texture.ArrayLayers > 1 && !SingleMipTail)
            _ = checked(MipTailOffsetInBytes + MipTailStrideInBytes * (texture.ArrayLayers - 1UL) + MipTailSizeInBytes);
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
        if (TileWidth == 0)
            throw new ArgumentOutOfRangeException(nameof(TileWidth));
        if (TileHeight == 0)
            throw new ArgumentOutOfRangeException(nameof(TileHeight));
        if (TileDepth == 0)
            throw new ArgumentOutOfRangeException(nameof(TileDepth));
        uint mipWidth = Math.Max(1u, texture.Width >> (int)MipLevel);
        uint mipHeight = Math.Max(1u, texture.Height >> (int)MipLevel);
        uint mipDepth = Math.Max(1u, texture.Depth >> (int)MipLevel);
        uint tilesX = (mipWidth - 1) / requirements.TileWidth + 1;
        uint tilesY = (mipHeight - 1) / requirements.TileHeight + 1;
        uint tilesZ = (mipDepth - 1) / requirements.TileDepth + 1;
        if (TileX >= tilesX || TileWidth > tilesX - TileX ||
            TileY >= tilesY || TileHeight > tilesY - TileY ||
            TileZ >= tilesZ || TileDepth > tilesZ - TileZ)
            throw new ArgumentOutOfRangeException(nameof(TileWidth), "Sparse tile region exceeds the mip's tile grid.");
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
        if (AllocationOffsetInBytes % requirements.TileSizeInBytes != 0)
            throw new ArgumentException("Sparse mapping allocation offsets must satisfy tile-size alignment.", nameof(AllocationOffsetInBytes));
        ulong requiredSize = IsMipTail
            ? requirements.MipTailSizeInBytes
            : checked((ulong)Region.TileWidth * Region.TileHeight * Region.TileDepth * requirements.TileSizeInBytes);
        if (requiredSize > Allocation.SizeInBytes - AllocationOffsetInBytes)
            throw new ArgumentException("The allocation does not contain the complete sparse mapping range.");
    }
}

/// <summary>Byte range in a sparse buffer, expressed in bytes and aligned to the backend-reported tile size.</summary>
public readonly record struct GraphicsSparseBufferRegion(ulong OffsetInBytes, ulong SizeInBytes)
{
    public void Validate(in GraphicsBufferDescription buffer, ulong tileSizeInBytes)
    {
        buffer.Validate();
        if (tileSizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(tileSizeInBytes));
        if (SizeInBytes == 0 || OffsetInBytes > buffer.SizeInBytes ||
            SizeInBytes > buffer.SizeInBytes - OffsetInBytes)
            throw new ArgumentOutOfRangeException(nameof(SizeInBytes));
        if (OffsetInBytes % tileSizeInBytes != 0 || SizeInBytes % tileSizeInBytes != 0)
            throw new ArgumentException("Sparse buffer ranges must be aligned to whole tiles.");
    }
}

/// <summary>Backend-reported sparse buffer page size and allocation alignment.</summary>
public readonly record struct GraphicsSparseBufferRequirements(ulong TileSizeInBytes, ulong Alignment)
{
    public void Validate(in GraphicsBufferDescription buffer)
    {
        buffer.Validate();
        if ((buffer.CreationOptions & GraphicsResourceCreationOptions.SparseResidency) == 0)
            throw new ArgumentException("The buffer must be created with SparseResidency.", nameof(buffer));
        if (TileSizeInBytes == 0 || Alignment == 0 ||
            (Alignment & (Alignment - 1)) != 0)
            throw new ArgumentException("Sparse buffer tile size and alignment are invalid.");
    }
}

/// <summary>One sparse buffer mapping. A null allocation unmaps the selected byte range.</summary>
public readonly record struct GraphicsSparseBufferBinding(
    IGraphicsBuffer Buffer,
    GraphicsSparseBufferRegion Region,
    IGraphicsMemoryAllocation? Allocation,
    ulong AllocationOffsetInBytes)
{
    public void Validate(in GraphicsSparseBufferRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(Buffer);
        if (Buffer.IsDisposed || Buffer.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(Buffer));
        requirements.Validate(Buffer.Description);
        Region.Validate(Buffer.Description, requirements.TileSizeInBytes);
        if (Allocation is null)
        {
            if (AllocationOffsetInBytes != 0)
                throw new ArgumentException("Unmapping cannot specify a memory offset.");
            return;
        }
        if (Allocation.IsDisposed || Allocation.Device.IsDisposed ||
            !ReferenceEquals(Allocation.Device, Buffer.Device))
            throw new ArgumentException("Sparse buffer and allocation must belong to the same live device.");
        if (AllocationOffsetInBytes > Allocation.SizeInBytes ||
            Region.SizeInBytes > Allocation.SizeInBytes - AllocationOffsetInBytes)
            throw new ArgumentException("The allocation does not contain the sparse buffer mapping range.");
        if (AllocationOffsetInBytes % requirements.Alignment != 0)
            throw new ArgumentException("The allocation offset does not satisfy sparse buffer alignment.");
    }
}

/// <summary>Optional sparse-resource extension. Binding operations are ordered on the supplied queue.</summary>
public interface IGraphicsSparseResourceDevice
{
    IGraphicsDevice Device { get; }
    GraphicsSparseTextureRequirements GetSparseTextureRequirements(IGraphicsTexture texture);
    GraphicsSparseBufferRequirements GetSparseBufferRequirements(IGraphicsBuffer buffer);
    void BindSparseTextures(
        IGraphicsCommandQueue queue,
        ReadOnlySpan<GraphicsSparseTextureBinding> bindings,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0);
    void BindSparseBuffers(
        IGraphicsCommandQueue queue,
        ReadOnlySpan<GraphicsSparseBufferBinding> bindings,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0);
}
