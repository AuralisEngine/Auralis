namespace Auralis.Engine.ECS;

/// <summary>Non-owning view over one chunk's entity IDs and one component column.</summary>
public readonly ref struct ComponentChunk<T> where T : notnull
{
    private readonly Chunk _chunk;
    internal ComponentChunk(Chunk chunk) => _chunk = chunk;
    public ReadOnlySpan<EntityId> Entities => _chunk.Entities.AsSpan(0, _chunk.Count);
    public Span<T> Components => ((IComponentColumn<T>)_chunk.GetColumn(ComponentType<T>.Id)).Span[.._chunk.Count];
    public ReadOnlySpan<T> ReadOnlyComponents => Components;
    public int Count => _chunk.Count;
}

/// <summary>Non-owning view over one chunk and two component columns.</summary>
public readonly ref struct ComponentChunk<T1, T2> where T1 : notnull where T2 : notnull
{
    private readonly Chunk _chunk;
    internal ComponentChunk(Chunk chunk) => _chunk = chunk;
    public ReadOnlySpan<EntityId> Entities => _chunk.Entities.AsSpan(0, _chunk.Count);
    public Span<T1> First => ((IComponentColumn<T1>)_chunk.GetColumn(ComponentType<T1>.Id)).Span[.._chunk.Count];
    public Span<T2> Second => ((IComponentColumn<T2>)_chunk.GetColumn(ComponentType<T2>.Id)).Span[.._chunk.Count];
    public ReadOnlySpan<T1> ReadOnlyFirst => First;
    public ReadOnlySpan<T2> ReadOnlySecond => Second;
    public int Count => _chunk.Count;
}

/// <summary>Entity IDs in one matching chunk, without exposing component columns.</summary>
public readonly ref struct EntityChunk
{
    private readonly Chunk _chunk;
    internal EntityChunk(Chunk chunk) => _chunk = chunk;
    public ReadOnlySpan<EntityId> Entities => _chunk.Entities.AsSpan(0, _chunk.Count);
    public int Count => _chunk.Count;
}
