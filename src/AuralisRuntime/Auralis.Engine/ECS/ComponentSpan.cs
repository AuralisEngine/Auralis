namespace Auralis.Engine.ECS;

/// <summary>
/// Version-checked view over a component column. The column is only dereferenced after
/// validating that its World has not undergone a structural change.
/// </summary>
public readonly ref struct ComponentSpan<T> where T : notnull
{
    private readonly World _world;
    private readonly IComponentColumn<T> _column;
    private readonly int _count;
    private readonly long _structuralVersion;

    internal ComponentSpan(World world, IComponentColumn<T> column, int count, long structuralVersion)
    {
        _world = world;
        _column = column;
        _count = count;
        _structuralVersion = structuralVersion;
    }

    public int Length
    {
        get
        {
            _world.ValidateStructuralVersion(_structuralVersion);
            return _count;
        }
    }

    public ref T this[int index]
    {
        get
        {
            _world.ValidateStructuralVersion(_structuralVersion);
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref _column.GetReference(index);
        }
    }
}

/// <summary>Read-only version-checked view over a component column.</summary>
public readonly ref struct ReadOnlyComponentSpan<T> where T : notnull
{
    private readonly World _world;
    private readonly IComponentColumn<T> _column;
    private readonly int _count;
    private readonly long _structuralVersion;

    internal ReadOnlyComponentSpan(World world, IComponentColumn<T> column, int count, long structuralVersion)
    {
        _world = world;
        _column = column;
        _count = count;
        _structuralVersion = structuralVersion;
    }

    public int Length
    {
        get
        {
            _world.ValidateStructuralVersion(_structuralVersion);
            return _count;
        }
    }

    public ref readonly T this[int index]
    {
        get
        {
            _world.ValidateStructuralVersion(_structuralVersion);
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref _column.GetReadOnlyReference(index);
        }
    }
}

/// <summary>
/// Version-checked component accessor. It does not expose a raw managed ref or native pointer.
/// </summary>
public readonly ref struct ComponentReference<T> where T : notnull
{
    private readonly World _world;
    private readonly EntityId _entity;
    private readonly long _structuralVersion;

    internal ComponentReference(World world, EntityId entity, long structuralVersion)
    {
        _world = world;
        _entity = entity;
        _structuralVersion = structuralVersion;
    }

    public T Value
    {
        get
        {
            _world.ValidateStructuralVersion(_structuralVersion);
            return _world.GetComponent<T>(_entity);
        }
        set
        {
            _world.ValidateStructuralVersion(_structuralVersion);
            _world.SetComponent(_entity, value);
        }
    }
}

/// <summary>Version-checked view over the entity IDs in one query chunk.</summary>
public readonly ref struct EntityIdSpan
{
    private readonly World _world;
    private readonly ReadOnlySpan<EntityId> _entities;
    private readonly long _structuralVersion;

    internal EntityIdSpan(World world, ReadOnlySpan<EntityId> entities, long structuralVersion)
    {
        _world = world;
        _entities = entities;
        _structuralVersion = structuralVersion;
    }

    public int Length
    {
        get
        {
            _world.ValidateStructuralVersion(_structuralVersion);
            return _entities.Length;
        }
    }

    public EntityId this[int index]
    {
        get
        {
            _world.ValidateStructuralVersion(_structuralVersion);
            return _entities[index];
        }
    }

    public Enumerator GetEnumerator() => new(this);

    public ref struct Enumerator
    {
        private readonly EntityIdSpan _span;
        private int _index;

        internal Enumerator(EntityIdSpan span)
        {
            _span = span;
            _index = -1;
        }

        public EntityId Current => _span[_index];

        public bool MoveNext()
        {
            var next = _index + 1;
            if (next >= _span.Length)
                return false;

            _index = next;
            return true;
        }
    }
}
