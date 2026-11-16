namespace Auralis.Engine.ECS;

/// <summary>
/// Owns entity lifetimes and archetype storage for one simulation world.
/// Structural changes are owner-thread-only and are deferred while systems execute.
/// </summary>
public sealed class World : IDisposable
{
    private readonly struct QuerySignature : IEquatable<QuerySignature>
    {
        private readonly int[] _include;
        private readonly int[] _exclude;
        private readonly int _hashCode;

        internal QuerySignature(int[] include, int[] exclude)
        {
            _include = include;
            _exclude = exclude;
            var hash = new HashCode();
            foreach (var id in include) hash.Add(id);
            hash.Add(-1);
            foreach (var id in exclude) hash.Add(id);
            _hashCode = hash.ToHashCode();
        }

        public bool Equals(QuerySignature other) =>
            _include.AsSpan().SequenceEqual(other._include) &&
            _exclude.AsSpan().SequenceEqual(other._exclude);

        public override bool Equals(object? obj) => obj is QuerySignature other && Equals(other);
        public override int GetHashCode() => _hashCode;
    }

    private struct EntitySlot
    {
        internal uint Generation;
        internal Entity? Entity;
        internal EntityLocation Location;
        internal bool Retired;
        internal bool IsDestroying;
    }

    private readonly List<EntitySlot> _slots = new();
    private readonly Stack<uint> _freeSlots = new();
    private readonly Dictionary<ArchetypeSignature, Archetype> _archetypes = new();
    private readonly Dictionary<int, ComponentStorageKind> _storageKinds = new();
    private readonly Dictionary<int, int> _nativeAlignmentOverrides = new();
    private readonly Dictionary<Type, QueryCache> _singleQueryCaches = new();
    private readonly Dictionary<(Type First, Type Second), QueryCache> _pairQueryCaches = new();
    private readonly Dictionary<QuerySignature, QueryCache> _builderQueryCaches = new();
    private readonly WorldConfiguration _configuration;
    private readonly int _ownerThreadId;
    private readonly Archetype _emptyArchetype;
    private int _archetypeVersion;
    private bool _disposed;
    private bool _isDisposing;
    private int _activeSystemDepth;
    private int _lifecycleDepth;

    public string Name { get; }
    public int EntityCount { get; private set; }
    public int ArchetypeCount => _archetypes.Count;
    public bool IsDisposed => _disposed;

    public World(string name = "World", WorldConfiguration? configuration = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "World" : name;
        _configuration = configuration ?? new WorldConfiguration();
        _configuration.Validate();
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _emptyArchetype = GetOrCreateArchetype(ReadOnlySpan<int>.Empty);
    }

    public Entity CreateEntity(string? name = null) => CreateEntity(new Entity(), name);

    public T CreateEntity<T>(string? name = null) where T : Entity, new() =>
        (T)CreateEntity(new T(), name);

    public Entity CreateEntity(Entity entity, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        EnsureStructuralMutationAllowed();
        if (entity.World is not null)
            throw new InvalidOperationException("Entity already belongs to a World.");
        if (entity.IsDestroyed)
            throw new InvalidOperationException("A destroyed Entity instance cannot be reused.");

        var index = AllocateSlot();
        var slot = _slots[index];
        var id = new EntityId((uint)index, slot.Generation);
        var location = _emptyArchetype.Add(id, GetChunkCapacity(_emptyArchetype.Signature));
        slot.Entity = entity;
        slot.Location = new EntityLocation(location.Chunk.Archetype, location.Chunk, location.Index);
        _slots[index] = slot;
        entity.Attach(this, id, name);
        EntityCount++;
        return entity;
    }

    public bool IsAlive(EntityId id) =>
        !_disposed && id.IsValid && id.Index < (uint)_slots.Count &&
        _slots[(int)id.Index].Generation == id.Generation &&
        _slots[(int)id.Index].Entity is not null;

    public bool TryGetEntity(EntityId id, out Entity? entity)
    {
        if (IsAlive(id))
        {
            entity = _slots[(int)id.Index].Entity;
            return true;
        }
        entity = null;
        return false;
    }

    public Entity GetEntity(EntityId id) =>
        TryGetEntity(id, out var entity)
            ? entity!
            : throw new KeyNotFoundException($"Entity handle {id} is stale or does not belong to this World.");

    public bool DestroyEntity(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!ReferenceEquals(entity.World, this) || !IsAlive(entity.EntityId))
            return false;
        return DestroyEntity(entity.EntityId);
    }

    public bool DestroyEntity(EntityId id)
    {
        if (!IsAlive(id)) return false;
        var slotIndex = (int)id.Index;
        var slot = _slots[slotIndex];
        if (slot.IsDestroying) return false;
        EnsureStructuralMutationAllowed();

        var entity = slot.Entity!;
        var location = slot.Location;
        slot.IsDestroying = true;
        _slots[slotIndex] = slot;
        _lifecycleDepth++;
        try
        {
            entity.InvokeDestroy();
        }
        catch
        {
            slot.IsDestroying = false;
            _slots[slotIndex] = slot;
            throw;
        }
        finally
        {
            _lifecycleDepth--;
        }
        location.Archetype.Remove(location.Chunk, location.Index, out var moved);
        UpdateMovedLocation(moved, location.Chunk, location.Index);

        slot.IsDestroying = false;
        slot.Entity = null;
        slot.Location = default;
        if (slot.Generation == uint.MaxValue)
            slot.Retired = true;
        else
        {
            slot.Generation++;
            _freeSlots.Push(id.Index);
        }
        _slots[slotIndex] = slot;

        entity.Detach();
        EntityCount--;
        return true;
    }

    public void AddComponent<T>(Entity entity, T component) where T : notnull
    {
        EnsureEntity(entity);
        AddComponent(entity.EntityId, component);
    }

    public void AddComponent<T>(EntityId id, T component) where T : notnull
    {
        if (component is null) throw new ArgumentNullException(nameof(component));
        EnsureStructuralMutationAllowed();
        var slot = GetSlot(id);
        var typeId = ComponentType<T>.Id;
        var source = slot.Location;
        if (source.Archetype.Signature.Contains(typeId))
            throw new InvalidOperationException($"Entity already has component {typeof(T).Name}.");

        if (!source.Archetype.TryGetAddTransition(typeId, out var target))
        {
            target = GetOrCreateArchetype(source.Archetype.Signature.With(typeId));
            source.Archetype.CacheAddTransition(typeId, target);
        }
        var destination = target.Add(id, GetChunkCapacity(target.Signature));
        source.Chunk.CopyCommonComponentsTo(source.Index, destination.Chunk, destination.Index);
        ((IComponentColumn<T>)destination.Chunk.GetColumn(typeId)).Span[destination.Index] = component;

        source.Archetype.Remove(source.Chunk, source.Index, out var moved);
        UpdateMovedLocation(moved, source.Chunk, source.Index);
        slot.Location = new EntityLocation(target, destination.Chunk, destination.Index);
        _slots[(int)id.Index] = slot;
    }

    public void SetComponent<T>(Entity entity, T component) where T : notnull
    {
        EnsureEntity(entity);
        SetComponent(entity.EntityId, component);
    }

    public void SetComponent<T>(EntityId id, T component) where T : notnull
    {
        if (component is null) throw new ArgumentNullException(nameof(component));
        EnsureStructuralMutationAllowed();
        var slot = GetSlot(id);
        var typeId = ComponentType<T>.Id;
        if (slot.Location.Chunk.TryGetColumn(typeId, out var column))
            ((IComponentColumn<T>)column).Span[slot.Location.Index] = component;
        else
            AddComponent(id, component);
    }

    public T GetComponent<T>(Entity entity) where T : notnull
    {
        EnsureEntity(entity);
        return GetComponent<T>(entity.EntityId);
    }

    public T GetComponent<T>(EntityId id) where T : notnull
    {
        var slot = GetSlot(id);
        return ((IComponentColumn<T>)slot.Location.Chunk.GetColumn(ComponentType<T>.Id)).Span[slot.Location.Index];
    }

    public ref T GetComponentReference<T>(EntityId id) where T : notnull
    {
        var slot = GetSlot(id);
        return ref ((IComponentColumn<T>)slot.Location.Chunk.GetColumn(ComponentType<T>.Id)).GetReference(slot.Location.Index);
    }

    public bool TryGetComponent<T>(EntityId id, out T component) where T : notnull
    {
        if (IsAlive(id))
        {
            var slot = _slots[(int)id.Index];
            if (slot.Location.Chunk.TryGetColumn(ComponentType<T>.Id, out var column))
            {
                component = ((IComponentColumn<T>)column).Span[slot.Location.Index];
                return true;
            }
        }
        component = default!;
        return false;
    }

    public bool HasComponent<T>(EntityId id) where T : notnull =>
        IsAlive(id) && _slots[(int)id.Index].Location.Chunk.TryGetColumn(ComponentType<T>.Id, out _);

    public bool HasComponent<T>(Entity entity) where T : notnull =>
        ReferenceEquals(entity.World, this) && HasComponent<T>(entity.EntityId);

    public bool RemoveComponent<T>(Entity entity) where T : notnull
    {
        EnsureEntity(entity);
        return RemoveComponent<T>(entity.EntityId);
    }

    public bool RemoveComponent<T>(EntityId id) where T : notnull
    {
        EnsureStructuralMutationAllowed();
        var slot = GetSlot(id);
        var source = slot.Location;
        var typeId = ComponentType<T>.Id;
        if (!source.Archetype.Signature.Contains(typeId)) return false;

        if (!source.Archetype.TryGetRemoveTransition(typeId, out var target))
        {
            target = GetOrCreateArchetype(source.Archetype.Signature.Without(typeId));
            source.Archetype.CacheRemoveTransition(typeId, target);
        }
        var destination = target.Add(id, GetChunkCapacity(target.Signature));
        source.Chunk.CopyCommonComponentsTo(source.Index, destination.Chunk, destination.Index);

        source.Archetype.Remove(source.Chunk, source.Index, out var moved);
        UpdateMovedLocation(moved, source.Chunk, source.Index);
        slot.Location = new EntityLocation(target, destination.Chunk, destination.Index);
        _slots[(int)id.Index] = slot;
        return true;
    }

    /// <summary>Registers T for aligned native storage in this World. Must be called before T is first used.</summary>
    public void RegisterUnmanagedComponent<T>(int? alignment = null) where T : unmanaged
    {
        EnsureStructuralMutationAllowed();
        var requestedAlignment = alignment ?? _configuration.NativeColumnAlignment;
        if (requestedAlignment < IntPtr.Size || (requestedAlignment & (requestedAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a power of two and at least pointer-sized.");

        var typeId = ComponentType<T>.Id;
        foreach (var archetype in _archetypes.Values)
            if (archetype.Signature.Contains(typeId))
                throw new InvalidOperationException("Register native storage before the component type is first used in this World.");

        ComponentTypeRegistry.RegisterNative<T>();
        _storageKinds[typeId] = ComponentStorageKind.Native;
        _nativeAlignmentOverrides[typeId] = requestedAlignment;
    }

    /// <summary>Preallocates empty-entity chunks for the archetype with the specified component signature.</summary>
    public void ReserveEntities(int capacity)
    {
        EnsureStructuralMutationAllowed();
        _emptyArchetype.Reserve(capacity, GetChunkCapacity(_emptyArchetype.Signature));
    }

    public void ReserveArchetype<T1>(int entityCapacity) where T1 : notnull
    {
        EnsureStructuralMutationAllowed();
        Span<int> ids = stackalloc int[1] { ComponentType<T1>.Id };
        var archetype = GetOrCreateArchetype(ids);
        archetype.Reserve(entityCapacity, GetChunkCapacity(archetype.Signature));
    }

    public void ReserveArchetype<T1, T2>(int entityCapacity)
        where T1 : notnull where T2 : notnull
    {
        EnsureStructuralMutationAllowed();
        Span<int> ids = stackalloc int[2] { ComponentType<T1>.Id, ComponentType<T2>.Id };
        var archetype = GetOrCreateArchetype(ids);
        archetype.Reserve(entityCapacity, GetChunkCapacity(archetype.Signature));
    }

    public void ReserveArchetype<T1, T2, T3>(int entityCapacity)
        where T1 : notnull where T2 : notnull where T3 : notnull
    {
        EnsureStructuralMutationAllowed();
        Span<int> ids = stackalloc int[3] { ComponentType<T1>.Id, ComponentType<T2>.Id, ComponentType<T3>.Id };
        var archetype = GetOrCreateArchetype(ids);
        archetype.Reserve(entityCapacity, GetChunkCapacity(archetype.Signature));
    }

    public ComponentQuery<T> Query<T>() where T : notnull
    {
        ThrowIfDisposed();
        var type = typeof(T);
        if (!_singleQueryCaches.TryGetValue(type, out var cache))
        {
            cache = new QueryCache(this, new[] { ComponentType<T>.Id }, Array.Empty<int>());
            _singleQueryCaches.Add(type, cache);
        }
        return new ComponentQuery<T>(cache);
    }

    public ComponentQuery<T1, T2> Query<T1, T2>() where T1 : notnull where T2 : notnull
    {
        ThrowIfDisposed();
        var key = (typeof(T1), typeof(T2));
        if (!_pairQueryCaches.TryGetValue(key, out var cache))
        {
            var include = new[] { ComponentType<T1>.Id, ComponentType<T2>.Id };
            Array.Sort(include);
            cache = new QueryCache(this, include, Array.Empty<int>());
            _pairQueryCaches.Add(key, cache);
        }
        return new ComponentQuery<T1, T2>(cache);
    }

    public QueryBuilder Query()
    {
        ThrowIfDisposed();
        return new QueryBuilder(this);
    }

    internal EntityQuery BuildQuery(int[] include, int[] exclude)
    {
        ThrowIfDisposed();
        Array.Sort(include);
        Array.Sort(exclude);
        var key = new QuerySignature(include, exclude);
        if (!_builderQueryCaches.TryGetValue(key, out var cache))
        {
            cache = new QueryCache(this, include, exclude);
            _builderQueryCaches.Add(key, cache);
        }
        return new EntityQuery(cache);
    }

    private int AllocateSlot()
    {
        while (_freeSlots.TryPop(out var free))
        {
            if (!_slots[(int)free].Retired)
                return (int)free;
        }
        _slots.Add(new EntitySlot { Generation = 1 });
        return _slots.Count - 1;
    }

    private EntitySlot GetSlot(EntityId id)
    {
        if (!IsAlive(id))
            throw new InvalidOperationException($"Entity handle {id} is invalid, stale, or belongs to another World.");
        return _slots[(int)id.Index];
    }

    private void EnsureEntity(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!ReferenceEquals(entity.World, this) || !IsAlive(entity.EntityId))
            throw new InvalidOperationException("Entity does not belong to this World or has been destroyed.");
    }

    private void EnsureStructuralMutationAllowed()
    {
        ThrowIfDisposed();
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Structural World changes must be performed on the World owner thread. Use an EntityCommandBuffer from worker threads.");
        if (_activeSystemDepth != 0)
            throw new InvalidOperationException("Structural changes are deferred while systems are executing.");
        if (_isDisposing)
            throw new InvalidOperationException("World is currently disposing.");
        if (_lifecycleDepth != 0)
            throw new InvalidOperationException("Structural changes are not allowed from Entity lifecycle callbacks.");
    }

    internal void BeginSystemExecution()
    {
        ThrowIfDisposed();
        if (_isDisposing || _lifecycleDepth != 0)
            throw new InvalidOperationException("Systems cannot execute during World or Entity lifecycle callbacks.");
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("System execution must be coordinated by the World owner thread.");
        _activeSystemDepth++;
    }

    internal void EndSystemExecution()
    {
        if (_activeSystemDepth <= 0) throw new InvalidOperationException("System execution scope is unbalanced.");
        _activeSystemDepth--;
    }

    private void UpdateMovedLocation(EntityId moved, Chunk chunk, int index)
    {
        if (moved.IsValid && IsAlive(moved))
        {
            var slotIndex = (int)moved.Index;
            var slot = _slots[slotIndex];
            slot.Location = new EntityLocation(chunk.Archetype, chunk, index);
            _slots[slotIndex] = slot;
        }
    }

    private Archetype GetOrCreateArchetype(ReadOnlySpan<int> ids) =>
        GetOrCreateArchetype(new ArchetypeSignature(ids));

    private Archetype GetOrCreateArchetype(ArchetypeSignature signature)
    {
        if (_archetypes.TryGetValue(signature, out var existing)) return existing;
        var created = new Archetype(signature, this);
        _archetypes.Add(signature, created);
        _archetypeVersion++;
        return created;
    }

    internal IComponentColumn CreateColumn(ComponentDescriptor descriptor, ArchetypeSignature signature)
    {
        var capacity = GetChunkCapacity(signature);
        if (_storageKinds.TryGetValue(descriptor.Id, out var kind) && kind == ComponentStorageKind.Native)
        {
            if (descriptor.ContainsManagedReferences || descriptor.NativeColumnFactory is null)
                throw new InvalidOperationException($"Component {descriptor.Type} cannot use native storage.");
            var alignment = _nativeAlignmentOverrides.TryGetValue(descriptor.Id, out var requestedAlignment)
                ? requestedAlignment : _configuration.NativeColumnAlignment;
            return descriptor.NativeColumnFactory(capacity, alignment);
        }
        return descriptor.ManagedColumnFactory(capacity);
    }

    private int GetChunkCapacity(ArchetypeSignature signature) =>
        ChunkLayout.CalculateCapacity(signature.ComponentIds, _configuration.ChunkTargetBytes, _configuration.MaximumEntitiesPerChunk);

    internal IReadOnlyCollection<Archetype> Archetypes => _archetypes.Values;
    internal int ArchetypeVersion => _archetypeVersion;
    internal bool IsSystemExecutionActive => _activeSystemDepth != 0;

    internal void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_isDisposing) throw new InvalidOperationException("World disposal is already in progress.");
        if (_lifecycleDepth != 0)
            throw new InvalidOperationException("World cannot be disposed from an Entity lifecycle callback.");
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("World must be disposed on its owner thread.");
        if (_activeSystemDepth != 0)
            throw new InvalidOperationException("Cannot dispose a World while systems are executing.");
        _isDisposing = true;
        List<Exception>? errors = null;
        foreach (var slot in _slots)
        {
            if (slot.Entity is not { } entity) continue;
            _lifecycleDepth++;
            try { entity.InvokeDestroy(); }
            catch (Exception exception) { (errors ??= new()).Add(exception); }
            finally { _lifecycleDepth--; }
            entity.Detach();
        }
        foreach (var archetype in _archetypes.Values) archetype.Dispose();
        _archetypes.Clear();
        _slots.Clear();
        _freeSlots.Clear();
        _singleQueryCaches.Clear();
        _pairQueryCaches.Clear();
        _builderQueryCaches.Clear();
        EntityCount = 0;
        _disposed = true;
        _isDisposing = false;
        if (errors is not null)
            throw new AggregateException("One or more Entity OnDestroy callbacks failed.", errors);
    }
}
