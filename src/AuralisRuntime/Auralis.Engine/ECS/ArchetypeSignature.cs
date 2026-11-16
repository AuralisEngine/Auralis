namespace Auralis.Engine.ECS;

internal sealed class ArchetypeSignature : IEquatable<ArchetypeSignature>
{
    private readonly int[] _componentIds;
    private readonly int _hashCode;
    internal ReadOnlySpan<int> ComponentIds => _componentIds;

    internal ArchetypeSignature(ReadOnlySpan<int> componentIds)
    {
        _componentIds = componentIds.ToArray();
        Array.Sort(_componentIds);
        for (var i = 1; i < _componentIds.Length; i++)
            if (_componentIds[i] == _componentIds[i - 1])
                throw new ArgumentException("Duplicate component type in archetype signature.");
        var hash = new HashCode();
        foreach (var id in _componentIds) hash.Add(id);
        _hashCode = hash.ToHashCode();
    }

    internal bool Contains(int componentId) => Array.BinarySearch(_componentIds, componentId) >= 0;

    internal int[] With(int componentId)
    {
        var result = new int[_componentIds.Length + 1];
        _componentIds.CopyTo(result, 0);
        result[^1] = componentId;
        return result;
    }

    internal int[] Without(int componentId)
    {
        var result = new int[_componentIds.Length - 1];
        var target = 0;
        foreach (var id in _componentIds)
            if (id != componentId) result[target++] = id;
        return result;
    }

    public bool Equals(ArchetypeSignature? other) =>
        other is not null && _componentIds.AsSpan().SequenceEqual(other._componentIds);
    public override bool Equals(object? obj) => obj is ArchetypeSignature other && Equals(other);
    public override int GetHashCode() => _hashCode;
}
