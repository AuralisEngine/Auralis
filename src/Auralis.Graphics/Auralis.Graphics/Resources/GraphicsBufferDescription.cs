namespace Auralis.Graphics.Resources;

/// <summary>Describes a buffer allocation without prescribing a backend's physical memory model.</summary>
public readonly record struct GraphicsBufferDescription(
    ulong SizeInBytes,
    GraphicsBufferUsage Usage,
    GraphicsMemoryAccess MemoryAccess = GraphicsMemoryAccess.GpuOnly,
    GraphicsMemoryPreference MemoryPreference = GraphicsMemoryPreference.Default,
    ulong MinimumAlignment = 0,
    GraphicsResourceCreationOptions CreationOptions = GraphicsResourceCreationOptions.None)
{
    public void Validate()
    {
        const GraphicsBufferUsage allUsage =
            GraphicsBufferUsage.Vertex | GraphicsBufferUsage.Index | GraphicsBufferUsage.Constant |
            GraphicsBufferUsage.Storage | GraphicsBufferUsage.IndirectArguments |
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination |
            GraphicsBufferUsage.ShaderBindingTable | GraphicsBufferUsage.AccelerationStructureBuildInput;
        const GraphicsResourceCreationOptions allOptions =
            GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing |
            GraphicsResourceCreationOptions.RequireExplicitMemoryBinding |
            GraphicsResourceCreationOptions.SparseResidency;

        if (SizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(SizeInBytes), "Buffer size must be greater than zero.");

        if (Usage == GraphicsBufferUsage.None || (Usage & ~allUsage) != 0)
            throw new ArgumentOutOfRangeException(nameof(Usage));

        if (!Enum.IsDefined(MemoryAccess))
            throw new ArgumentOutOfRangeException(nameof(MemoryAccess));

        if (!Enum.IsDefined(MemoryPreference))
            throw new ArgumentOutOfRangeException(nameof(MemoryPreference));
        if (MinimumAlignment != 0 && (MinimumAlignment & (MinimumAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumAlignment), "Minimum alignment must be zero or a power of two.");

        const GraphicsResourceCreationOptions sparseConflicts =
            GraphicsResourceCreationOptions.DedicatedAllocation |
            GraphicsResourceCreationOptions.AllowAliasing |
            GraphicsResourceCreationOptions.RequireExplicitMemoryBinding;

        if ((CreationOptions & ~allOptions) != 0 ||
            (CreationOptions & (GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing)) ==
                (GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing) ||
            ((CreationOptions & GraphicsResourceCreationOptions.RequireExplicitMemoryBinding) != 0 &&
             (CreationOptions & GraphicsResourceCreationOptions.DedicatedAllocation) != 0) ||
            ((CreationOptions & GraphicsResourceCreationOptions.SparseResidency) != 0 &&
             (CreationOptions & sparseConflicts) != 0))
            throw new ArgumentOutOfRangeException(nameof(CreationOptions));
    }
}
