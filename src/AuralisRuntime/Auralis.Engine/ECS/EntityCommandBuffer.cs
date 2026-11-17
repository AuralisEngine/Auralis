namespace Auralis.Engine.ECS;

/// <summary>
/// Records structural changes for playback on the World owner thread.
/// Recording is thread-safe; concurrent commands are ordered by the order in which
/// writers acquire the internal lock, which is not deterministic across worker runs.
/// Playback is exclusive and consumes the buffer even when a command throws.
/// </summary>
/// <remarks>
/// World mutation is not thread-safe. Workers may record commands into this buffer,
/// but only the World owner thread may call <see cref="Playback"/>.
/// If deterministic cross-worker ordering is required, use one buffer per worker
/// and play those buffers back in a fixed order.
/// Playback is not transactional: commands applied before a failure remain applied.
/// The failed command and all remaining commands are discarded to prevent replay.
/// </remarks>
public sealed class EntityCommandBuffer
{
    private enum BufferState : byte
    {
        Recording,
        PlayingBack,
        Consumed
    }

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
            if (world.IsAlive(Entity))
                world.RemoveComponent<T>(Entity);
        }
    }

    private sealed record AddCommand<T>(EntityId Entity, T Component) : ICommand where T : notnull
    {
        public void Apply(World world)
        {
            if (world.IsAlive(Entity))
                world.AddComponent(Entity, Component);
        }
    }

    private sealed record SetCommand<T>(EntityId Entity, T Component) : ICommand where T : notnull
    {
        public void Apply(World world)
        {
            if (world.IsAlive(Entity))
                world.SetComponent(Entity, Component);
        }
    }

    private readonly object _gate = new();
    private readonly List<ICommand> _commands = new();
    private BufferState _state;

    public int Count
    {
        get
        {
            lock (_gate)
                return _commands.Count;
        }
    }

    public void Create(Entity entity, string? name = null) =>
        Enqueue(new CreateCommand(entity, name));

    public void Destroy(EntityId entity) =>
        Enqueue(new DestroyCommand(entity));

    public void Remove<T>(EntityId entity) where T : notnull =>
        Enqueue(new RemoveCommand<T>(entity));

    public void Add<T>(EntityId entity, T component) where T : notnull =>
        Enqueue(new AddCommand<T>(entity, component));

    public void Set<T>(EntityId entity, T component) where T : notnull =>
        Enqueue(new SetCommand<T>(entity, component));

    /// <summary>
    /// Applies commands in recorded order on the World owner thread.
    /// The buffer is consumed whether playback succeeds or throws.
    /// </summary>
    public void Playback(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        // Keep the lock for the complete playback. Other writers cannot race the
        // command list and will observe the consumed state after playback finishes.
        lock (_gate)
        {
            EnsureRecording();

            if (world.IsSystemExecutionActive)
                throw new InvalidOperationException("Command buffers can only be played back outside system execution.");

            _state = BufferState.PlayingBack;
            try
            {
                for (var i = 0; i < _commands.Count; i++)
                    _commands[i].Apply(world);
            }
            finally
            {
                // Playback is deliberately not retryable: earlier commands may
                // already have changed the World when a later command fails.
                _commands.Clear();
                _state = BufferState.Consumed;
            }
        }
    }

    /// <summary>
    /// Discards pending commands and makes the buffer available for recording again.
    /// Cannot be called reentrantly while playback is in progress.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_state == BufferState.PlayingBack)
                throw new InvalidOperationException("A command buffer cannot be cleared during playback.");

            _commands.Clear();
            _state = BufferState.Recording;
        }
    }

    private void Enqueue(ICommand command)
    {
        lock (_gate)
        {
            EnsureRecording();
            _commands.Add(command);
        }
    }

    // Must be called while _gate is held.
    private void EnsureRecording()
    {
        if (_state == BufferState.PlayingBack)
            throw new InvalidOperationException("A command buffer cannot be modified during playback.");

        if (_state == BufferState.Consumed)
            throw new InvalidOperationException("Call Clear before reusing a played-back command buffer.");
    }
}
