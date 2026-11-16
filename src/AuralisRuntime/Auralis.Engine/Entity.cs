using Auralis.Engine.ECS;

namespace Auralis.Engine;

/// <summary>
/// Object-oriented entity facade. Derive from Entity for domain-specific behavior.
/// Component data is owned by World archetypes rather than by this object.
/// </summary>
public class Entity : ComponentBase
{
    private World? _world;
    private EntityId _entityId;

    public EntityId EntityId => _entityId;
    public World? World => _world;
    public bool IsAttached => _world is not null;
    public bool IsDestroyed { get; private set; }
    public string? Description { get; set; }

    public T AddComponent<T>(T component) where T : notnull
    {
        RequireWorld().AddComponent(this, component);
        return component;
    }

    public T AddScript<T>(T script) where T : Script
    {
        ArgumentNullException.ThrowIfNull(script);
        AddComponent<Script>(script);
        return script;
    }

    public T GetComponent<T>() where T : notnull => RequireWorld().GetComponent<T>(this);

    public bool TryGetComponent<T>(out T component) where T : notnull
    {
        if (_world is not null)
            return _world.TryGetComponent(_entityId, out component);

        component = default!;
        return false;
    }

    public ref T GetComponentReference<T>() where T : notnull =>
        ref RequireWorld().GetComponentReference<T>(_entityId);

    public bool HasComponent<T>() where T : notnull =>
        _world is not null && _world.HasComponent<T>(this);

    public bool RemoveComponent<T>() where T : notnull =>
        RequireWorld().RemoveComponent<T>(this);

    public void SetComponent<T>(T component) where T : notnull =>
        RequireWorld().SetComponent(this, component);

    public bool Destroy() => RequireWorld().DestroyEntity(this);

    internal void Attach(World world, EntityId id, string? name)
    {
        _world = world;
        _entityId = id;
        IsDestroyed = false;

        if (name is not null)
            Name = name;
    }

    internal void Detach()
    {
        _world = null;
        _entityId = default;
        IsDestroyed = true;
    }

    internal void InvokeDestroy() => OnDestroy();

    private World RequireWorld() =>
        _world ?? throw new InvalidOperationException("Entity is not attached to a live World.");
}
