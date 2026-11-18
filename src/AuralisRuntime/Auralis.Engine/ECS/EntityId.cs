namespace Auralis.Engine.ECS;

/// <summary>
/// Compact, world-local runtime identity for an entity. The low 32 bits contain
/// the slot index and the high 32 bits contain its generation.
/// </summary>
/// <remarks>
/// EntityId values are meaningful only in the <see cref="World"/> that created them.
/// Use the <see cref="Entity"/> object when an API needs to enforce world ownership.
/// Persistent identity belongs to <see cref="ComponentBase.Id"/>, not this handle.
/// </remarks>
public readonly struct EntityId : IEquatable<EntityId>
{
    public ulong Value { get; }

    public uint Index => (uint)Value;
    public uint Generation => (uint)(Value >> 32);
    public bool IsValid => Generation != 0;

    internal EntityId(uint index, uint generation)
    {
        Value = ((ulong)generation << 32) | index;
    }

    public bool Equals(EntityId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);
    public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);
    public override string ToString() => IsValid ? $"EntityId({Index}:{Generation})" : "EntityId(Invalid)";
}
