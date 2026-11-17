namespace Auralis.Engine.ECS;

internal sealed class Chunk : IDisposable
{
    private readonly IComponentColumn[] _columns;

    internal EntityId[] Entities { get; }
    internal int Count { get; private set; }
    internal int Capacity => Entities.Length;
    internal Archetype Archetype { get; }

    internal Chunk(Archetype archetype, int capacity)
    {
        Archetype = archetype;
        Entities = new EntityId[capacity];
        var ids = archetype.Signature.ComponentIds;
        _columns = new IComponentColumn[ids.Length];
        var createdColumns = 0;
        try
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var descriptor = ComponentTypeRegistry.GetById(ids[i]);
                _columns[i] = archetype.CreateColumn(descriptor);
                createdColumns = i + 1;
            }
        }
        catch
        {
            for (var i = 0; i < createdColumns; i++)
                _columns[i].Dispose();
            throw;
        }
    }

    internal int Add(EntityId entity)
    {
        if (Count == Capacity)
            throw new InvalidOperationException("Chunk is full.");

        var index = Count++;
        Entities[index] = entity;
        return index;
    }

    internal void RemoveAtSwapBack(int index, out EntityId movedEntity)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var last = --Count;
        movedEntity = default;
        if (index != last)
        {
            movedEntity = Entities[last];
            Entities[index] = movedEntity;
            for (var i = 0; i < _columns.Length; i++)
                _columns[i].CopyElementTo(last, _columns[i], index);
        }

        Entities[last] = default;
        for (var i = 0; i < _columns.Length; i++)
            _columns[i].Clear(last);
    }

    internal IComponentColumn GetColumn(int componentTypeId)
    {
        var index = Archetype.Signature.IndexOf(componentTypeId);
        if (index >= 0)
            return _columns[index];

        var componentType = ComponentTypeRegistry.GetById(componentTypeId).Type;
        throw new InvalidOperationException(
            $"Archetype does not contain component type '{componentType.FullName}'.");
    }

    internal bool TryGetColumn(int componentTypeId, out IComponentColumn column)
    {
        var index = Archetype.Signature.IndexOf(componentTypeId);
        if (index >= 0)
        {
            column = _columns[index];
            return true;
        }

        column = null!;
        return false;
    }

    internal void CopyCommonComponentsTo(int sourceIndex, Chunk destination, int destinationIndex)
    {
        for (var i = 0; i < _columns.Length; i++)
        {
            var sourceColumn = _columns[i];
            if (destination.TryGetColumn(sourceColumn.ComponentTypeId, out var destinationColumn))
                sourceColumn.CopyElementTo(sourceIndex, destinationColumn, destinationIndex);
        }
    }

    public void Dispose()
    {
        foreach (var column in _columns)
            column.Dispose();

        Array.Clear(Entities);
        Count = 0;
    }
}
