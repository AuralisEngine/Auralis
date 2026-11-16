namespace Auralis.Engine.ECS;

/// <summary>Allocation-free enumerator over matching chunks containing T.</summary>
public readonly struct ComponentQuery<T> where T : notnull
{
    private readonly QueryCache _cache;
    internal ComponentQuery(QueryCache cache) => _cache = cache;
    public Enumerator GetEnumerator() => new(_cache);

    public ref struct Enumerator
    {
        private readonly QueryCache _cache;
        private IReadOnlyList<Archetype> _archetypes;
        private int _archetypeIndex;
        private int _chunkIndex;
        public ComponentChunk<T> Current { get; private set; }

        internal Enumerator(QueryCache cache)
        {
            _cache = cache;
            _archetypes = cache.Matches;
            _archetypeIndex = 0;
            _chunkIndex = -1;
            Current = default;
        }

        public bool MoveNext()
        {
            _archetypes = _cache.Matches;
            while (_archetypeIndex < _archetypes.Count)
            {
                var chunks = _archetypes[_archetypeIndex].Chunks;
                if (++_chunkIndex < chunks.Count)
                {
                    var chunk = chunks[_chunkIndex];
                    if (chunk.Count == 0) continue;
                    Current = new ComponentChunk<T>(chunk);
                    return true;
                }
                _archetypeIndex++;
                _chunkIndex = -1;
            }
            return false;
        }
    }
}

/// <summary>Allocation-free enumerator over matching chunks containing T1 and T2.</summary>
public readonly struct ComponentQuery<T1, T2>
    where T1 : notnull where T2 : notnull
{
    private readonly QueryCache _cache;
    internal ComponentQuery(QueryCache cache) => _cache = cache;
    public Enumerator GetEnumerator() => new(_cache);

    public ref struct Enumerator
    {
        private readonly QueryCache _cache;
        private IReadOnlyList<Archetype> _archetypes;
        private int _archetypeIndex;
        private int _chunkIndex;
        public ComponentChunk<T1, T2> Current { get; private set; }

        internal Enumerator(QueryCache cache)
        {
            _cache = cache;
            _archetypes = cache.Matches;
            _archetypeIndex = 0;
            _chunkIndex = -1;
            Current = default;
        }

        public bool MoveNext()
        {
            _archetypes = _cache.Matches;
            while (_archetypeIndex < _archetypes.Count)
            {
                var chunks = _archetypes[_archetypeIndex].Chunks;
                if (++_chunkIndex < chunks.Count)
                {
                    var chunk = chunks[_chunkIndex];
                    if (chunk.Count == 0) continue;
                    Current = new ComponentChunk<T1, T2>(chunk);
                    return true;
                }
                _archetypeIndex++;
                _chunkIndex = -1;
            }
            return false;
        }
    }
}

/// <summary>Allocation-free enumerator over matching entity chunks.</summary>
public readonly struct EntityQuery
{
    private readonly QueryCache _cache;
    internal EntityQuery(QueryCache cache) => _cache = cache;
    public Enumerator GetEnumerator() => new(_cache);

    public ref struct Enumerator
    {
        private readonly QueryCache _cache;
        private IReadOnlyList<Archetype> _archetypes;
        private int _archetypeIndex;
        private int _chunkIndex;
        public EntityChunk Current { get; private set; }

        internal Enumerator(QueryCache cache)
        {
            _cache = cache;
            _archetypes = cache.Matches;
            _archetypeIndex = 0;
            _chunkIndex = -1;
            Current = default;
        }

        public bool MoveNext()
        {
            _archetypes = _cache.Matches;
            while (_archetypeIndex < _archetypes.Count)
            {
                var chunks = _archetypes[_archetypeIndex].Chunks;
                if (++_chunkIndex < chunks.Count)
                {
                    var chunk = chunks[_chunkIndex];
                    if (chunk.Count == 0) continue;
                    Current = new EntityChunk(chunk);
                    return true;
                }
                _archetypeIndex++;
                _chunkIndex = -1;
            }
            return false;
        }
    }
}
