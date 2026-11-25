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
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination;
        const GraphicsResourceCreationOptions allOptions =
            GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing |
            GraphicsResourceCreationOptions.RequireExplicitMemoryBinding | GraphicsResourceCreationOptions.Sparse;

        if (SizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(SizeInBytes), "Buffer size must be greater than zero.");

        if (Usage == GraphicsBufferUsage.None || (Usage & ~allUsage) != 0)
            throw new ArgumentOutOfRangeException(nameof(Usage));

        if (!Enum.IsDefined(MemoryAccess))
            throw new ArgumentOutOfRangeException(nameof(MemoryAccess));

        if (!Enum.IsDefined(MemoryPreference))
            throw new ArgumentOutOfRangeException(nameof(MemoryPreference));

        if ((CreationOptions & ~allOptions) != 0 ||
            (CreationOptions & (GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing)) ==
                (GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.AllowAliasing) ||
            ((CreationOptions & GraphicsResourceCreationOptions.RequireExplicitMemoryBinding) != 0 &&
             (CreationOptions & (GraphicsResourceCreationOptions.DedicatedAllocation | GraphicsResourceCreationOptions.Sparse)) != 0) ||
            ((CreationOptions & GraphicsResourceCreationOptions.Sparse) != 0 &&
             (CreationOptions & GraphicsResourceCreationOptions.DedicatedAllocation) != 0))
            throw new ArgumentOutOfRangeException(nameof(CreationOptions));
    }
}
