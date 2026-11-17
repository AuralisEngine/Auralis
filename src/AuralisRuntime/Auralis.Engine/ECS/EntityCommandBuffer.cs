using System.Collections.Concurrent;

namespace Auralis.Engine.ECS;

/// <summary>
/// Records structural changes for playback on the World owner thread.
/// Multiple workers may record concurrently using a lock-free producer path
/// backed by ConcurrentQueue and Interlocked state coordination.
/// </summary>
/// <remarks>
/// World mutation is not thread-safe. Workers may record commands into this buffer,
/// but only the World owner thread may call <see cref="Playback"/>.
/// Concurrent producer ordering is not deterministic. Use one buffer per worker
/// and play those buffers back in a fixed order when deterministic ordering matters.
///
/// Playback is not transactional. Commands applied before a failure remain applied;
/// the failing command and all remaining commands are discarded and the buffer is
/// consumed to prevent accidental replay.
/// Add throws when the target already has that component. Create throws when the
/// entity instance is already attached or has been destroyed. Set adds the component
/// when missing; Remove and Destroy ignore stale entity IDs.
/// </remarks>
public sealed class EntityCommandBuffer
{
    private const int Recording = 0;
    private const int PlayingBack = 1;
    private const int Consumed = 2;
    private const int Clearing = 3;

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

    private readonly ConcurrentQueue<ICommand> _commands = new();
    private int _state;
    private int _activeWriters;
    private int _pendingCount;

    /// <summary>Number of commands currently queued; a concurrent read is a snapshot.</summary>
    public int Count => Math.Max(0, Volatile.Read(ref _pendingCount));

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
    /// Applies queued commands in queue order. Must run on the World owner thread.
    /// An empty playback is a no-op and leaves the buffer ready for recording.
    /// A non-empty playback consumes the buffer, even when a command throws.
    /// </summary>
    public void Playback(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.EnsureCommandBufferPlaybackAllowed();

        var observedState = Interlocked.CompareExchange(ref _state, PlayingBack, Recording);
        if (observedState != Recording)
            ThrowForState(observedState);

        WaitForWriters();

        var hadCommands = false;
        try
        {
            while (_commands.TryDequeue(out var command))
            {
                hadCommands = true;
                Interlocked.Decrement(ref _pendingCount);
                command.Apply(world);
            }
        }
        catch
        {
            // The failed command was already dequeued. Discard every command after it.
            DrainPendingCommands();
            Volatile.Write(ref _state, Consumed);
            throw;
        }

        // Do not force callers to Clear an empty buffer every frame.
        Volatile.Write(ref _state, hadCommands ? Consumed : Recording);
    }

    /// <summary>
    /// Discards queued commands and makes the buffer available for recording again.
    /// Commands already executing in Playback cannot be cleared.
    /// </summary>
    public void Clear()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is PlayingBack or Clearing)
                throw new InvalidOperationException("A command buffer cannot be cleared during playback or another clear operation.");

            if (Interlocked.CompareExchange(ref _state, Clearing, state) == state)
                break;
        }

        WaitForWriters();
        DrainPendingCommands();
        Volatile.Write(ref _state, Recording);
    }

    private void Enqueue(ICommand command)
    {
        if (Volatile.Read(ref _state) != Recording)
            ThrowForState(Volatile.Read(ref _state));

        Interlocked.Increment(ref _activeWriters);
        try
        {
            // Playback/Clear may have started between the first state check and
            // registering this writer. Recheck before publishing the command.
            if (Volatile.Read(ref _state) != Recording)
                ThrowForState(Volatile.Read(ref _state));

            _commands.Enqueue(command);
            Interlocked.Increment(ref _pendingCount);
        }
        finally
        {
            Interlocked.Decrement(ref _activeWriters);
        }
    }

    private void WaitForWriters()
    {
        var spinner = new SpinWait();
        while (Volatile.Read(ref _activeWriters) != 0)
            spinner.SpinOnce();
    }

    private void DrainPendingCommands()
    {
        while (_commands.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingCount);
    }

    private static void ThrowForState(int state)
    {
        throw state switch
        {
            PlayingBack => new InvalidOperationException("A command buffer cannot be modified or played back while playback is in progress."),
            Clearing => new InvalidOperationException("A command buffer cannot be modified while it is being cleared."),
            Consumed => new InvalidOperationException("Call Clear before reusing a played-back command buffer."),
            _ => new InvalidOperationException("The command buffer is not available for recording.")
        };
    }
}
