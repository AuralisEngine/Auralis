namespace Auralis.Engine.ECS;

internal sealed class Archetype : IDisposable
{
    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<int, Archetype> _addTransitions = new();
    private readonly Dictionary<int, Archetype> _removeTransitions = new();
    private readonly World _world;
    private readonly int _chunkCapacity;

    internal ArchetypeSignature Signature { get; }
    internal IReadOnlyList<Chunk> Chunks => _chunks;
    internal int EntityCount { get; private set; }
    internal int ChunkCapacity => _chunkCapacity;
    internal World World => _world;

    internal bool TryGetAddTransition(int componentTypeId, out Archetype target) =>
        _addTransitions.TryGetValue(componentTypeId, out target!);

    internal void CacheAddTransition(int componentTypeId, Archetype target) =>
        _addTransitions[componentTypeId] = target;

    internal bool TryGetRemoveTransition(int componentTypeId, out Archetype target) =>
        _removeTransitions.TryGetValue(componentTypeId, out target!);

    internal void CacheRemoveTransition(int componentTypeId, Archetype target) =>
        _removeTransitions[componentTypeId] = target;

    internal Archetype(ArchetypeSignature signature, World world)
    {
        Signature = signature;
        _world = world;
        // A signature never changes, so chunk capacity is invariant for this archetype.
        // Compute it once rather than taking the global component-registry lock on every
        // entity add/remove/reserve operation.
        _chunkCapacity = world.GetChunkCapacity(signature);
    }

    internal IComponentColumn CreateColumn(ComponentDescriptor descriptor) =>
        _world.CreateColumn(descriptor, _chunkCapacity);

    internal (Chunk Chunk, int Index) Add(EntityId entity)
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

        chunk ??= CreateChunk();
        var index = chunk.Add(entity);
        EntityCount++;
        _world.MarkStructuralChange();
        return (chunk, index);
    }

    internal void Reserve(int entityCapacity)
    {
        if (entityCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(entityCapacity));

        var requiredChunks = (int)(((long)entityCapacity + _chunkCapacity - 1) / _chunkCapacity);
        while (_chunks.Count < requiredChunks)
            CreateChunk();
    }

    internal void Remove(Chunk chunk, int index, out EntityId movedEntity)
    {
        chunk.RemoveAtSwapBack(index, out movedEntity);
        EntityCount--;
        _world.MarkStructuralChange();

        // Empty chunks are no longer useful. Remove them wherever they are in the
        // list so they cannot retain native allocations or fragment archetype storage.
        if (chunk.Count == 0)
        {
            var chunkIndex = _chunks.IndexOf(chunk);
            if (chunkIndex >= 0)
            {
                _chunks.RemoveAt(chunkIndex);
                chunk.Dispose();
                _world.MarkStructuralChange();
            }
        }
    }

    private Chunk CreateChunk()
    {
        var chunk = new Chunk(this, _chunkCapacity);
        _chunks.Add(chunk);
        _world.MarkStructuralChange();
        return chunk;
    }

    public void Dispose()
    {
        foreach (var chunk in _chunks)
            chunk.Dispose();

        _chunks.Clear();
        _addTransitions.Clear();
        _removeTransitions.Clear();
        EntityCount = 0;
    }
}
