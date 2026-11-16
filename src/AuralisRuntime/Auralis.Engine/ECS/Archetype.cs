namespace Auralis.Engine.ECS;

internal sealed class Archetype : IDisposable
{
    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<int, Archetype> _addTransitions = new();
    private readonly Dictionary<int, Archetype> _removeTransitions = new();
    private readonly Func<ComponentDescriptor, IComponentColumn> _columnFactory;
    internal ArchetypeSignature Signature { get; }
    internal IReadOnlyList<Chunk> Chunks => _chunks;
    internal int EntityCount { get; private set; }

    internal bool TryGetAddTransition(int componentTypeId, out Archetype target) =>
        _addTransitions.TryGetValue(componentTypeId, out target!);
    internal void CacheAddTransition(int componentTypeId, Archetype target) => _addTransitions[componentTypeId] = target;
    internal bool TryGetRemoveTransition(int componentTypeId, out Archetype target) =>
        _removeTransitions.TryGetValue(componentTypeId, out target!);
    internal void CacheRemoveTransition(int componentTypeId, Archetype target) => _removeTransitions[componentTypeId] = target;

    internal Archetype(ArchetypeSignature signature, Func<ComponentDescriptor, IComponentColumn> columnFactory)
    {
        Signature = signature;
        _columnFactory = columnFactory;
    }

    internal (Chunk Chunk, int Index) Add(EntityId entity, int chunkCapacity)
    {
        Chunk? chunk = null;
        for (var i = _chunks.Count - 1; i >= 0; i--)
        {
            if (_chunks[i].Count < _chunks[i].Capacity)
            {
                chunk = _chunks[i];
                break;
            }
        }
        chunk ??= CreateChunk(chunkCapacity);
        var index = chunk.Add(entity);
        EntityCount++;
        return (chunk, index);
    }

    internal void Reserve(int entityCapacity, int chunkCapacity)
    {
        if (entityCapacity < 0) throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        var requiredChunks = (int)(((long)entityCapacity + chunkCapacity - 1) / chunkCapacity);
        while (_chunks.Count < requiredChunks)
            CreateChunk(chunkCapacity);
    }

    internal void Remove(Chunk chunk, int index, out EntityId movedEntity)
    {
        chunk.RemoveAtSwapBack(index, out movedEntity);
        EntityCount--;
        if (chunk.Count == 0 && _chunks.Count > 1 && ReferenceEquals(_chunks[^1], chunk))
        {
            _chunks.RemoveAt(_chunks.Count - 1);
            chunk.Dispose();
        }
    }

    private Chunk CreateChunk(int capacity)
    {
        var chunk = new Chunk(this, capacity, _columnFactory);
        _chunks.Add(chunk);
        return chunk;
    }

    public void Dispose()
    {
        foreach (var chunk in _chunks) chunk.Dispose();
        _chunks.Clear();
        _addTransitions.Clear();
        _removeTransitions.Clear();
        EntityCount = 0;
    }
}
