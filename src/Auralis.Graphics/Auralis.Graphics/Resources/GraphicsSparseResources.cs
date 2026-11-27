using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Synchronization;

namespace Auralis.Graphics.Resources;

/// <summary>
/// Backend-reported, aspect-specific sparse texture tile and mip-tail requirements. Tile extents are
/// opaque backend granularities and are not constrained to powers of two; Vulkan can report non-standard
/// sparse block dimensions. Opaque metadata has a separate mip-tail contract. Multisample sparse image
/// support is backend/adapter-specific; structural validation does not assume every sparse implementation
/// supports every sample count.
/// </summary>
public readonly record struct GraphicsSparseTextureRequirements(
    uint TileWidth,
    uint TileHeight,
    uint TileDepth,
    ulong TileSizeInBytes,
    uint FirstMipTailLevel,
    ulong MipTailSizeInBytes,
    ulong MipTailOffsetInBytes,
    ulong MipTailStrideInBytes,
    bool SingleMipTail,
    GraphicsTextureAspect Aspect = GraphicsTextureAspect.Color)
{
    public void Validate(in GraphicsTextureDescription texture)
    {
        texture.Validate();
        if ((texture.CreationOptions & GraphicsResourceCreationOptions.SparseResidency) == 0)
            throw new ArgumentException("The texture must be created with SparseResidency.", nameof(texture));
        if (TileWidth == 0 || TileHeight == 0 || TileDepth == 0 || TileSizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(TileWidth), "Sparse tile dimensions and tile size must be non-zero.");
        if (texture.Dimension == GraphicsTextureDimension.Texture1D)
            throw new ArgumentException("Sparse texture requirements are not available for 1D textures.");
        if (!Enum.IsDefined(Aspect) || Aspect == GraphicsTextureAspect.None)
            throw new ArgumentOutOfRangeException(nameof(Aspect),
                "Sparse texture requirements must identify exactly one color, depth, or stencil aspect.");
        GraphicsTextureAspect availableAspects = GraphicsFormatInfo.GetAspects(texture.Format);
        if ((Aspect & ~availableAspects) != 0)
            throw new ArgumentException(
                $"Texture format {texture.Format} does not contain sparse aspect(s) {Aspect}.",
                nameof(Aspect));
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
        {
            ulong lastLayer = texture.ArrayLayers - 1UL;
            if (MipTailStrideInBytes != 0 &&
                lastLayer > (ulong.MaxValue - MipTailOffsetInBytes) / MipTailStrideInBytes)
                throw new ArgumentOutOfRangeException(nameof(MipTailStrideInBytes),
                    "Per-layer mip-tail offsets exceed the addressable byte range.");

            ulong lastLayerOffset = MipTailOffsetInBytes + MipTailStrideInBytes * lastLayer;
            if (MipTailSizeInBytes > ulong.MaxValue - lastLayerOffset)
                throw new ArgumentOutOfRangeException(nameof(MipTailSizeInBytes),
                    "The final mip-tail range exceeds the addressable byte range.");
        }
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
    uint TileDepth,
    GraphicsTextureAspect Aspect = GraphicsTextureAspect.Color)
{
    public void Validate(in GraphicsTextureDescription texture, in GraphicsSparseTextureRequirements requirements)
    {
        if (Aspect != requirements.Aspect)
            throw new ArgumentException("Sparse texture region aspect must match the requirements used to validate it.", nameof(Aspect));
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
        if (Region.Aspect != requirements.Aspect)
            throw new ArgumentException("Sparse texture binding aspect must match the backend requirements.", nameof(Region));
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
        ulong requiredSize;
        if (IsMipTail)
        {
            requiredSize = requirements.MipTailSizeInBytes;
        }
        else
        {
            ulong tileCount = (ulong)Region.TileWidth * Region.TileHeight * Region.TileDepth;
            if (requirements.TileSizeInBytes != 0 &&
                tileCount > ulong.MaxValue / requirements.TileSizeInBytes)
                throw new ArgumentOutOfRangeException(nameof(Region),
                    "The sparse tile region byte size exceeds the addressable range.");
            requiredSize = tileCount * requirements.TileSizeInBytes;
        }
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
    GraphicsSparseTextureRequirements GetSparseTextureRequirements(
        IGraphicsTexture texture,
        GraphicsTextureAspect aspect = GraphicsTextureAspect.Color);
    GraphicsSparseBufferRequirements GetSparseBufferRequirements(IGraphicsBuffer buffer);
    /// <summary>Implementations validate the queue and each binding using GraphicsSparseResourceValidation before scheduling.</summary>
    void BindSparseTextures(
        IGraphicsSparseBindingQueue queue,
        ReadOnlySpan<GraphicsSparseTextureBinding> bindings,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0);
    /// <summary>Implementations validate the queue and each binding using GraphicsSparseResourceValidation before scheduling.</summary>
    void BindSparseBuffers(
        IGraphicsSparseBindingQueue queue,
        ReadOnlySpan<GraphicsSparseBufferBinding> bindings,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0);
}


/// <summary>
/// Backend-reported opaque sparse image metadata mip-tail range. Metadata is not addressed by texel
/// coordinates and therefore uses a separate contract from color/depth/stencil sparse tiles.
/// </summary>
public readonly record struct GraphicsSparseTextureMetadataRequirements(
    ulong SizeInBytes,
    ulong OffsetInBytes,
    ulong StrideInBytes,
    ulong Alignment,
    bool SingleMipTail)
{
    public void Validate(in GraphicsTextureDescription texture)
    {
        texture.Validate();
        if ((texture.CreationOptions & GraphicsResourceCreationOptions.SparseResidency) == 0)
            throw new ArgumentException("The texture must be created with SparseResidency.", nameof(texture));
        if (texture.Dimension == GraphicsTextureDimension.Texture1D)
            throw new ArgumentException("Sparse metadata is not available for 1D textures.", nameof(texture));
        if (SizeInBytes == 0 || Alignment == 0 || (Alignment & (Alignment - 1)) != 0)
            throw new ArgumentException("Sparse metadata size and alignment must be non-zero, with power-of-two alignment.");
        if (SingleMipTail && StrideInBytes != 0)
            throw new ArgumentException("A single metadata mip tail cannot declare a per-layer stride.", nameof(StrideInBytes));
        if (!SingleMipTail && texture.ArrayLayers > 1 && StrideInBytes < SizeInBytes)
            throw new ArgumentException("Per-layer metadata mip-tail stride cannot be smaller than its size.", nameof(StrideInBytes));
        ulong lastLayer = SingleMipTail ? 0 : texture.ArrayLayers - 1UL;
        if (StrideInBytes != 0 && lastLayer > (ulong.MaxValue - OffsetInBytes) / StrideInBytes)
            throw new ArgumentOutOfRangeException(nameof(StrideInBytes),
                "Sparse metadata layer offsets exceed the addressable byte range.");
        ulong tailOffset = OffsetInBytes + StrideInBytes * lastLayer;
        if (SizeInBytes > ulong.MaxValue - tailOffset)
            throw new ArgumentOutOfRangeException(nameof(SizeInBytes),
                "The sparse metadata tail exceeds the addressable byte range.");
    }
}

/// <summary>Maps or unmaps one opaque metadata mip tail (per-layer or single-tail as reported by the backend).</summary>
public readonly record struct GraphicsSparseTextureMetadataBinding(
    IGraphicsTexture Texture,
    uint ArrayLayer,
    IGraphicsMemoryAllocation? Allocation,
    ulong AllocationOffsetInBytes)
{
    public void Validate(in GraphicsSparseTextureMetadataRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(Texture);
        if (Texture.IsDisposed || Texture.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(Texture));
        requirements.Validate(Texture.Description);
        if (requirements.SingleMipTail)
        {
            if (ArrayLayer != 0)
                throw new ArgumentOutOfRangeException(nameof(ArrayLayer),
                    "A single metadata mip tail is bound through array layer zero.");
        }
        else if (ArrayLayer >= Texture.Description.ArrayLayers)
        {
            throw new ArgumentOutOfRangeException(nameof(ArrayLayer));
        }

        if (Allocation is null)
        {
            if (AllocationOffsetInBytes != 0)
                throw new ArgumentException("Unmapping metadata cannot specify an allocation offset.", nameof(AllocationOffsetInBytes));
            return;
        }

        if (Allocation.IsDisposed || Allocation.Device.IsDisposed ||
            !ReferenceEquals(Allocation.Device, Texture.Device))
            throw new ArgumentException("Metadata texture and allocation must belong to the same live device.", nameof(Allocation));
        if (AllocationOffsetInBytes > Allocation.SizeInBytes ||
            requirements.SizeInBytes > Allocation.SizeInBytes - AllocationOffsetInBytes)
            throw new ArgumentOutOfRangeException(nameof(AllocationOffsetInBytes),
                "The allocation does not contain the complete metadata mip-tail range.");
        if (AllocationOffsetInBytes % requirements.Alignment != 0)
            throw new ArgumentException("The metadata allocation offset does not satisfy backend alignment.", nameof(AllocationOffsetInBytes));
    }
}

/// <summary>
/// Optional extension for backends exposing separate sparse image metadata requirements and bindings.
/// Vulkan-style metadata is opaque and resides in its own mip-tail requirement; it is not represented as
/// a Color/Depth/Stencil texel aspect. Backends without a distinct metadata mapping model do not implement
/// this interface.
/// </summary>
public interface IGraphicsSparseTextureMetadataDevice : IGraphicsSparseResourceDevice
{
    GraphicsSparseTextureMetadataRequirements GetSparseTextureMetadataRequirements(IGraphicsTexture texture);

    void BindSparseTextureMetadata(
        IGraphicsSparseBindingQueue queue,
        ReadOnlySpan<GraphicsSparseTextureMetadataBinding> bindings,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0);
}

/// <summary>Shared validation for queue-specific sparse operations.</summary>
public static class GraphicsSparseResourceValidation
{
    /// <summary>
    /// Ensures that the optional sparse device is live/enabled and the selected queue is a sparse-capable
    /// queue from that exact device. Implementations should call this before every sparse binding operation.
    /// </summary>
    public static void ValidateBindingQueue(IGraphicsSparseResourceDevice sparseDevice, IGraphicsQueue queue)
    {
        ArgumentNullException.ThrowIfNull(sparseDevice);
        ArgumentNullException.ThrowIfNull(queue);
        IGraphicsDevice device = sparseDevice.Device;
        ArgumentNullException.ThrowIfNull(device);
        if (device.IsDisposed)
            throw new ObjectDisposedException(nameof(device));
        if (!device.Capabilities.Features.Supports(GraphicsFeature.SparseResources))
            throw new GraphicsFeatureNotSupportedException(GraphicsFeature.SparseResources, GraphicsFeature.SparseResources);
        if (!ReferenceEquals(queue.Device, device))
            throw new ArgumentException("The sparse-binding queue must belong to the sparse resource device.", nameof(queue));
        if (queue is not IGraphicsSparseBindingQueue)
            throw new ArgumentException("The selected queue does not report sparse-binding capability.", nameof(queue));
    }

    /// <summary>Validates every texture mapping against the backend-reported requirement for its aspect.</summary>
    public static void ValidateTextureBindings(
        IGraphicsSparseResourceDevice sparseDevice,
        IGraphicsQueue queue,
        ReadOnlySpan<GraphicsSparseTextureBinding> bindings)
    {
        ValidateBindingQueue(sparseDevice, queue);
        for (int i = 0; i < bindings.Length; i++)
        {
            IGraphicsTexture texture = bindings[i].Texture ??
                throw new ArgumentException("Sparse texture bindings cannot contain a null texture.", nameof(bindings));
            if (!ReferenceEquals(texture.Device, sparseDevice.Device))
                throw new ArgumentException("All sparse textures must belong to the sparse resource device.", nameof(bindings));
            if (!Enum.IsDefined(bindings[i].Region.Aspect) ||
                bindings[i].Region.Aspect == GraphicsTextureAspect.None)
                throw new ArgumentOutOfRangeException(nameof(bindings),
                    "Each sparse texture binding must identify exactly one image aspect.");
            GraphicsSparseTextureRequirements requirements =
                sparseDevice.GetSparseTextureRequirements(texture, bindings[i].Region.Aspect);
            bindings[i].Validate(requirements);
        }
    }

    /// <summary>Validates every buffer mapping against the backend-reported page/alignment requirements.</summary>
    public static void ValidateBufferBindings(
        IGraphicsSparseResourceDevice sparseDevice,
        IGraphicsQueue queue,
        ReadOnlySpan<GraphicsSparseBufferBinding> bindings)
    {
        ValidateBindingQueue(sparseDevice, queue);
        for (int i = 0; i < bindings.Length; i++)
        {
            IGraphicsBuffer buffer = bindings[i].Buffer ??
                throw new ArgumentException("Sparse buffer bindings cannot contain a null buffer.", nameof(bindings));
            if (!ReferenceEquals(buffer.Device, sparseDevice.Device))
                throw new ArgumentException("All sparse buffers must belong to the sparse resource device.", nameof(bindings));
            GraphicsSparseBufferRequirements requirements = sparseDevice.GetSparseBufferRequirements(buffer);
            bindings[i].Validate(requirements);
        }
    }

    public static void ValidateTextureMetadataBindings(
        IGraphicsSparseTextureMetadataDevice sparseDevice,
        IGraphicsQueue queue,
        ReadOnlySpan<GraphicsSparseTextureMetadataBinding> bindings,
        in GraphicsSparseTextureMetadataRequirements requirements)
    {
        ValidateBindingQueue(sparseDevice, queue);
        for (int i = 0; i < bindings.Length; i++)
        {
            bindings[i].Validate(requirements);
            if (!ReferenceEquals(bindings[i].Texture.Device, sparseDevice.Device))
                throw new ArgumentException("All metadata textures must belong to the sparse resource device.", nameof(bindings));
        }
    }
}
