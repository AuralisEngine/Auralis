namespace Auralis.Engine.ECS;

/// <summary>Version-checked view over one chunk's entity IDs and one component column.</summary>
public readonly ref struct ComponentChunk<T> where T : notnull
{
    private readonly Chunk _chunk;
    private readonly World _world;
    private readonly long _structuralVersion;
    private readonly int _count;

    internal ComponentChunk(Chunk chunk, World world, long structuralVersion)
    {
        _chunk = chunk;
        _world = world;
        _structuralVersion = structuralVersion;
        _count = chunk.Count;
    }

    public EntityIdSpan Entities
    {
        get
        {
            Validate();
            return new EntityIdSpan(_world, _chunk.Entities.AsSpan(0, _count), _structuralVersion);
        }
    }

    public ComponentSpan<T> Components
    {
        get
        {
            Validate();
            return new ComponentSpan<T>(
                _world,
                (IComponentColumn<T>)_chunk.GetColumn(ComponentType<T>.Id),
                _count,
                _structuralVersion);
        }
    }

    public ReadOnlyComponentSpan<T> ReadOnlyComponents
    {
        get
        {
            Validate();
            return new ReadOnlyComponentSpan<T>(
                _world,
                (IComponentColumn<T>)_chunk.GetColumn(ComponentType<T>.Id),
                _count,
                _structuralVersion);
        }
    }

    public int Count
    {
        get
        {
            Validate();
            return _count;
        }
    }

    private void Validate() => _world.ValidateStructuralVersion(_structuralVersion);
}

/// <summary>Version-checked view over one chunk and two component columns.</summary>
public readonly ref struct ComponentChunk<T1, T2>
    where T1 : notnull where T2 : notnull
{
    private readonly Chunk _chunk;
    private readonly World _world;
    private readonly long _structuralVersion;
    private readonly int _count;

    internal ComponentChunk(Chunk chunk, World world, long structuralVersion)
    {
        _chunk = chunk;
        _world = world;
        _structuralVersion = structuralVersion;
        _count = chunk.Count;
    }

    public EntityIdSpan Entities
    {
        get
        {
            Validate();
            return new EntityIdSpan(_world, _chunk.Entities.AsSpan(0, _count), _structuralVersion);
        }
    }

    public ComponentSpan<T1> First
    {
        get
        {
            Validate();
            return new ComponentSpan<T1>(
                _world,
                (IComponentColumn<T1>)_chunk.GetColumn(ComponentType<T1>.Id),
                _count,
                _structuralVersion);
        }
    }

    public ComponentSpan<T2> Second
    {
        get
        {
            Validate();
            return new ComponentSpan<T2>(
                _world,
                (IComponentColumn<T2>)_chunk.GetColumn(ComponentType<T2>.Id),
                _count,
                _structuralVersion);
        }
    }

    public ReadOnlyComponentSpan<T1> ReadOnlyFirst
    {
        get
        {
            Validate();
            return new ReadOnlyComponentSpan<T1>(
                _world,
                (IComponentColumn<T1>)_chunk.GetColumn(ComponentType<T1>.Id),
                _count,
                _structuralVersion);
        }
    }

    public ReadOnlyComponentSpan<T2> ReadOnlySecond
    {
        get
        {
            Validate();
            return new ReadOnlyComponentSpan<T2>(
                _world,
                (IComponentColumn<T2>)_chunk.GetColumn(ComponentType<T2>.Id),
                _count,
                _structuralVersion);
        }
    }

    public int Count
    {
        get
        {
            Validate();
            return _count;
        }
    }

    private void Validate() => _world.ValidateStructuralVersion(_structuralVersion);
}

/// <summary>Version-checked entity IDs in one matching chunk, without component columns.</summary>
public readonly ref struct EntityChunk
{
    private readonly Chunk _chunk;
    private readonly World _world;
    private readonly long _structuralVersion;
    private readonly int _count;

    internal EntityChunk(Chunk chunk, World world, long structuralVersion)
    {
        _chunk = chunk;
        _world = world;
        _structuralVersion = structuralVersion;
        _count = chunk.Count;
    }

    public EntityIdSpan Entities
    {
        get
        {
            Validate();
            return new EntityIdSpan(_world, _chunk.Entities.AsSpan(0, _count), _structuralVersion);
        }
    }

    public int Count
    {
        get
        {
            Validate();
            return _count;
        }
    }

    private void Validate() => _world.ValidateStructuralVersion(_structuralVersion);
}
