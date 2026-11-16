using System.Runtime.CompilerServices;

namespace Auralis.Engine.ECS;

internal sealed class ComponentDescriptor
{
    internal int Id { get; }
    internal Type Type { get; }
    internal int SizeInBytes { get; }
    internal bool ContainsManagedReferences { get; }
    internal Func<int, IComponentColumn> ManagedColumnFactory { get; }
    internal volatile Func<int, int, IComponentColumn>? NativeColumnFactory;

    internal ComponentDescriptor(int id, Type type, int sizeInBytes, bool containsManagedReferences,
        Func<int, IComponentColumn> managedColumnFactory)
    {
        Id = id;
        Type = type;
        SizeInBytes = Math.Max(1, sizeInBytes);
        ContainsManagedReferences = containsManagedReferences;
        ManagedColumnFactory = managedColumnFactory;
    }
}

/// <summary>Static metadata for a component type, registered on first use without assembly scanning.</summary>
public static class ComponentType<T> where T : notnull
{
    private static readonly ComponentDescriptor s_descriptor = ComponentTypeRegistry.GetOrRegister<T>();
    public static int Id => s_descriptor.Id;
    public static int SizeInBytes => s_descriptor.SizeInBytes;
    public static bool ContainsManagedReferences => s_descriptor.ContainsManagedReferences;
}

internal static class ComponentTypeRegistry
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<Type, ComponentDescriptor> s_byType = new();
    private static readonly Dictionary<int, ComponentDescriptor> s_byId = new();
    private static int s_nextId;

    internal static ComponentDescriptor GetOrRegister<T>() where T : notnull
    {
        lock (s_gate)
        {
            if (s_byType.TryGetValue(typeof(T), out var existing))
                return existing;

            var descriptor = new ComponentDescriptor(++s_nextId, typeof(T), Unsafe.SizeOf<T>(),
                RuntimeHelpers.IsReferenceOrContainsReferences<T>(),
                static capacity => new ManagedComponentColumn<T>(capacity));
            s_byType.Add(typeof(T), descriptor);
            s_byId.Add(descriptor.Id, descriptor);
            return descriptor;
        }
    }

    internal static ComponentDescriptor GetById(int id)
    {
        lock (s_gate)
            return s_byId.TryGetValue(id, out var descriptor)
                ? descriptor
                : throw new InvalidOperationException($"Component type id {id} is not registered.");
    }

    internal static void RegisterNative<T>() where T : unmanaged
    {
        lock (s_gate)
        {
            var descriptor = GetOrRegister<T>();
            descriptor.NativeColumnFactory ??= static (capacity, alignment) =>
                new NativeComponentColumn<T>(capacity, alignment);
        }
    }
}
