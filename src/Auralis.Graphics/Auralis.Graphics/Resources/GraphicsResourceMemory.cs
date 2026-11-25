using Auralis.Graphics.Commands;

namespace Auralis.Graphics.Resources;

/// <summary>Memory requirements reported for a resource created with RequireExplicitMemoryBinding.</summary>
public readonly record struct GraphicsMemoryRequirements(
    ulong SizeInBytes,
    ulong Alignment,
    uint MemoryTypeBits,
    bool RequiresDedicatedAllocation,
    bool PrefersDedicatedAllocation)
{
    public void Validate()
    {
        if (SizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(SizeInBytes));
        if (Alignment == 0 || (Alignment & (Alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(Alignment), "Resource alignment must be a non-zero power of two.");
        if (MemoryTypeBits == 0)
            throw new ArgumentOutOfRangeException(nameof(MemoryTypeBits));
        if (RequiresDedicatedAllocation && !PrefersDedicatedAllocation)
            throw new ArgumentException("A required dedicated allocation is necessarily preferred.");
    }

    public void Validate(in GraphicsBufferDescription buffer)
    {
        Validate();
        buffer.Validate();
        if (buffer.MinimumAlignment != 0 &&
            (Alignment < buffer.MinimumAlignment || Alignment % buffer.MinimumAlignment != 0))
            throw new ArgumentException("Resource memory alignment does not satisfy the buffer's MinimumAlignment.", nameof(Alignment));
    }
}

/// <summary>
/// Optional device extension for explicit resource-memory binding. Resources must be created with
/// RequireExplicitMemoryBinding and bound exactly once before they are used by GPU commands.
/// </summary>
public interface IGraphicsResourceMemoryDevice
{
    GraphicsMemoryRequirements GetMemoryRequirements(IGraphicsBuffer buffer);
    GraphicsMemoryRequirements GetMemoryRequirements(IGraphicsTexture texture);
    void BindMemory(IGraphicsResource resource, IGraphicsMemoryAllocation allocation, ulong offsetInBytes = 0);
}

/// <summary>
/// Describes one resource's placement in an allocation. The allocation must outlive the resource
/// and all submitted work that references it.
/// </summary>
public readonly record struct GraphicsResourceMemoryBinding(
    IGraphicsResource Resource,
    IGraphicsMemoryAllocation Allocation,
    ulong OffsetInBytes)
{
    public void Validate(in GraphicsMemoryRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(Resource);
        ArgumentNullException.ThrowIfNull(Allocation);
        if (Resource is IGraphicsBuffer resourceBuffer)
            requirements.Validate(resourceBuffer.Description);
        else
            requirements.Validate();

        if (Resource.IsDisposed || Resource.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(Resource));
        if (Allocation.IsDisposed || Allocation.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(Allocation));
        if (!ReferenceEquals(Resource.Device, Allocation.Device))
            throw new ArgumentException("Resource and memory allocation must belong to the same device.");

        bool requiresExplicitBinding = Resource switch
        {
            IGraphicsBuffer buffer =>
                (buffer.Description.CreationOptions & GraphicsResourceCreationOptions.RequireExplicitMemoryBinding) != 0,
            IGraphicsTexture texture =>
                (texture.Description.CreationOptions & GraphicsResourceCreationOptions.RequireExplicitMemoryBinding) != 0,
            _ => false
        };
        if (!requiresExplicitBinding)
            throw new ArgumentException("Only buffers or textures created with RequireExplicitMemoryBinding can use explicit placement.");
        if (OffsetInBytes % requirements.Alignment != 0)
            throw new ArgumentOutOfRangeException(nameof(OffsetInBytes), "Binding offset does not satisfy the resource alignment.");
        if (OffsetInBytes > Allocation.SizeInBytes ||
            requirements.SizeInBytes > Allocation.SizeInBytes - OffsetInBytes)
            throw new ArgumentOutOfRangeException(nameof(OffsetInBytes), "Resource placement exceeds the allocation bounds.");
        if (requirements.RequiresDedicatedAllocation && !Allocation.IsDedicated)
            throw new ArgumentException("The resource requires a dedicated allocation.");
        if (Allocation.MemoryTypeIndex >= 32 ||
            (requirements.MemoryTypeBits & (1u << (int)Allocation.MemoryTypeIndex)) == 0)
            throw new ArgumentException("The allocation memory type is not compatible with the resource.");
    }
}

/// <summary>
/// Optional command-list extension for explicit memory aliasing dependencies. A null before-resource
/// means the memory was not previously used by an alias; a null after-resource ends the prior alias use.
/// The caller must ensure both resources occupy overlapping memory and that prior GPU accesses have
/// completed or are ordered before the barrier.
/// </summary>
public interface IGraphicsAliasingCommandList : IGraphicsCommandList
{
    void AliasingBarrier(IGraphicsResource? before, IGraphicsResource? after);
}
