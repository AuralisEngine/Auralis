using System.Collections.Concurrent;

namespace Auralis.Engine.ECS;

/// <summary>
/// Records structural changes for playback on the World owner thread.
/// Multiple workers may record concurrently through ConcurrentQueue and an
/// atomic multi-producer registration protocol; no application-level lock is used.
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
    private const int ModeMask = unchecked((int)0xC0000000);
    private const int WriterCountMask = 0x3FFFFFFF;

    private const int Recording = 0;
    private const int PlayingBack = 0x40000000;
    private const int Consumed = unchecked((int)0x80000000);
    private const int Clearing = unchecked((int)0xC0000000);

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
    // The high two bits encode lifecycle state; the low bits atomically count
    // producers that have registered permission to enqueue.
    private int _state;
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

        TransitionTo(PlayingBack, allowConsumed: false);
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
        TransitionTo(Clearing, allowConsumed: true);
        WaitForWriters();
        DrainPendingCommands();
        Volatile.Write(ref _state, Recording);
    }

    private void Enqueue(ICommand command)
    {
        RegisterWriter();
        try
        {
            _commands.Enqueue(command);
            Interlocked.Increment(ref _pendingCount);
        }
        finally
        {
            // Decrement only the writer counter, preserving the lifecycle mode bits.
            Interlocked.Decrement(ref _state);
        }
    }

    private void RegisterWriter()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if ((state & ModeMask) != Recording)
                ThrowForState(state & ModeMask);

            if ((state & WriterCountMask) == WriterCountMask)
                throw new InvalidOperationException("Too many concurrent command-buffer writers.");

            // State transition and writer registration are one CAS. Clear/Playback
            // cannot pass this writer without either rejecting it or waiting for it.
            if (Interlocked.CompareExchange(ref _state, state + 1, state) == state)
                return;
        }
    }

    private void TransitionTo(int targetMode, bool allowConsumed)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            var currentMode = state & ModeMask;

            if (currentMode == PlayingBack || currentMode == Clearing ||
                (!allowConsumed && currentMode == Consumed))
                ThrowForState(currentMode);

            if (currentMode == Consumed && !allowConsumed)
                ThrowForState(currentMode);

            var nextState = targetMode | (state & WriterCountMask);
            if (Interlocked.CompareExchange(ref _state, nextState, state) == state)
                return;
        }
    }

    private void WaitForWriters()
    {
        var spinner = new SpinWait();
        while ((Volatile.Read(ref _state) & WriterCountMask) != 0)
            spinner.SpinOnce();
    }

    private void DrainPendingCommands()
    {
        while (_commands.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingCount);
    }

    private static void ThrowForState(int mode)
    {
        throw mode switch
        {
            PlayingBack => new InvalidOperationException("A command buffer cannot be modified or played back while playback is in progress."),
            Clearing => new InvalidOperationException("A command buffer cannot be modified while it is being cleared."),
            Consumed => new InvalidOperationException("Call Clear before reusing a played-back command buffer."),
            _ => new InvalidOperationException("The command buffer is not available for recording.")
        };
    }
}
