namespace Auralis.Engine.ECS;

/// <summary>Configuration for a simulation World.</summary>
public sealed class WorldConfiguration
{
    public int ChunkTargetBytes { get; init; } = ChunkLayout.DefaultTargetBytes;
    public int MaximumEntitiesPerChunk { get; init; } = ChunkLayout.DefaultMaximumEntities;
    public int NativeColumnAlignment { get; init; } = 64;

    internal void Validate()
    {
        if (ChunkTargetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(ChunkTargetBytes));
        if (MaximumEntitiesPerChunk <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumEntitiesPerChunk));
        if (NativeColumnAlignment < IntPtr.Size || (NativeColumnAlignment & (NativeColumnAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(NativeColumnAlignment));
    }
}
