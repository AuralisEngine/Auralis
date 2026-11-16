namespace Auralis.Engine.ECS;

/// <summary>
/// Records structural changes for deterministic playback after system iteration.
/// Commands are currently object-backed; this is a structural-change path, not a hot data path.
/// </summary>
public sealed class EntityCommandBuffer
{
    private interface ICommand { void Apply(World world); }
    private sealed record CreateCommand(Entity Instance, string? Name) : ICommand
    {
        public void Apply(World world) => world.CreateEntity(Instance, Name);
    }
    private sealed record DestroyCommand(EntityId Entity) : ICommand
    {
        public void Apply(World world) => world.DestroyEntity(Entity);
    }
    private sealed record RemoveCommand<T>(EntityId Entity) : ICommand where T : notnull
    {
        public void Apply(World world)
        {
            if (world.IsAlive(Entity)) world.RemoveComponent<T>(Entity);
        }
    }
    private sealed record AddCommand<T>(EntityId Entity, T Component) : ICommand where T : notnull
    {
        public void Apply(World world)
        {
            if (world.IsAlive(Entity)) world.AddComponent(Entity, Component);
        }
    }
    private sealed record SetCommand<T>(EntityId Entity, T Component) : ICommand where T : notnull
    {
        public void Apply(World world)
        {
            if (world.IsAlive(Entity)) world.SetComponent(Entity, Component);
        }
    }

    private readonly List<ICommand> _commands = new();
    private bool _playedBack;

    public int Count => _commands.Count;

    public void Create(Entity entity, string? name = null) { EnsureWritable(); _commands.Add(new CreateCommand(entity, name)); }
    public void Destroy(EntityId entity) { EnsureWritable(); _commands.Add(new DestroyCommand(entity)); }
    public void Remove<T>(EntityId entity) where T : notnull { EnsureWritable(); _commands.Add(new RemoveCommand<T>(entity)); }
    public void Add<T>(EntityId entity, T component) where T : notnull { EnsureWritable(); _commands.Add(new AddCommand<T>(entity, component)); }
    public void Set<T>(EntityId entity, T component) where T : notnull { EnsureWritable(); _commands.Add(new SetCommand<T>(entity, component)); }

    public void Playback(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsureWritable();
        if (world.IsSystemExecutionActive)
            throw new InvalidOperationException("Command buffers can only be played back outside system execution.");
        foreach (var command in _commands) command.Apply(world);
        _commands.Clear();
        _playedBack = true;
    }

    public void Clear()
    {
        _commands.Clear();
        _playedBack = false;
    }

    private void EnsureWritable()
    {
        if (_playedBack) throw new InvalidOperationException("Call Clear before reusing a played-back command buffer.");
    }
}
