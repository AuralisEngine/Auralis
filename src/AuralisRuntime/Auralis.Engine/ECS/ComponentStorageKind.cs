namespace Auralis.Engine.ECS;

/// <summary>Describes the backing memory strategy used by a component column.</summary>
public enum ComponentStorageKind
{
    /// <summary>GC-tracked typed arrays; supports all component types, including managed references.</summary>
    Managed,

    /// <summary>Aligned native memory; only valid for unmanaged component types.</summary>
    Native
}
