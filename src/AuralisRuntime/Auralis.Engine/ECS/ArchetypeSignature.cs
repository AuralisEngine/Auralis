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
        _hashCode = ValidateAndCalculateHash();
    }

    private ArchetypeSignature(int[] componentIds)
    {
        _componentIds = componentIds;
        _hashCode = ValidateAndCalculateHash();
    }

    private int ValidateAndCalculateHash()
    {
        for (var i = 1; i < _componentIds.Length; i++)
            if (_componentIds[i] == _componentIds[i - 1])
                throw new ArgumentException("Duplicate component type in archetype signature.");

        var hash = new HashCode();
        foreach (var id in _componentIds) hash.Add(id);
        return hash.ToHashCode();
    }

    internal bool Contains(int componentId) => Array.BinarySearch(_componentIds, componentId) >= 0;

    internal ArchetypeSignature With(int componentId)
    {
        var insertionIndex = Array.BinarySearch(_componentIds, componentId);
        if (insertionIndex >= 0)
            throw new ArgumentException("Component type already exists in archetype signature.", nameof(componentId));
        insertionIndex = ~insertionIndex;

        var result = new int[_componentIds.Length + 1];
        _componentIds.AsSpan(0, insertionIndex).CopyTo(result);
        result[insertionIndex] = componentId;
        _componentIds.AsSpan(insertionIndex).CopyTo(result.AsSpan(insertionIndex + 1));
        return new ArchetypeSignature(result);
    }

    internal ArchetypeSignature Without(int componentId)
    {
        var removalIndex = Array.BinarySearch(_componentIds, componentId);
        if (removalIndex < 0)
            throw new ArgumentException("Component type does not exist in archetype signature.", nameof(componentId));

        var result = new int[_componentIds.Length - 1];
        _componentIds.AsSpan(0, removalIndex).CopyTo(result);
        _componentIds.AsSpan(removalIndex + 1).CopyTo(result.AsSpan(removalIndex));
        return new ArchetypeSignature(result);
    }

    internal int IndexOf(int componentId) => Array.BinarySearch(_componentIds, componentId);

    public bool Equals(ArchetypeSignature? other) =>
        other is not null && _componentIds.AsSpan().SequenceEqual(other._componentIds);
    public override bool Equals(object? obj) => obj is ArchetypeSignature other && Equals(other);
    public override int GetHashCode() => _hashCode;
}
