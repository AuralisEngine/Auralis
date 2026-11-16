namespace Auralis.Engine.ECS;

/// <summary>Ordered group of systems representing one simulation phase.</summary>
public sealed class SystemGroup
{
    private readonly List<SystemDescriptor> _systems = new();
    public string Name { get; }
    public IReadOnlyList<SystemDescriptor> Systems => _systems;

    public SystemGroup(string name) => Name = string.IsNullOrWhiteSpace(name) ? "Systems" : name;

    public void Add(SystemDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _systems.Add(descriptor);
    }
}
