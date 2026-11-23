namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Atomic owner/command/dependent reference state for a GLES native object. A signed state uses the high
/// bit to prevent new public retains after owner disposal; already-live dependents may still retain it.
/// </summary>
internal sealed class OpenGLESResourceReferenceState
{
    private int _state = 1;

    /// <summary>Gets the current owner-plus-dependent reference count, excluding the closed-state flag.</summary>
    internal int ReferenceCount => Volatile.Read(ref _state) & int.MaxValue;

    /// <summary>Gets whether public owner retention has been closed.</summary>
    internal bool IsClosed => Volatile.Read(ref _state) < 0;

    /// <summary>Attempts to acquire a new public/command reference while the owner remains open.</summary>
    internal bool TryRetain() => TryRetain(allowClosed: false);

    /// <summary>Attempts to acquire a dependency reference, even after owner disposal, while an existing reference keeps storage alive.</summary>
    internal bool TryRetainDependency() => TryRetain(allowClosed: true);

    /// <summary>Atomically increments the count, subject to the owner-close policy selected by the caller.</summary>
    private bool TryRetain(bool allowClosed)
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            int references = state & int.MaxValue;
            if (references == 0 || (!allowClosed && state < 0))
                return false;
            if (references == int.MaxValue)
                throw new InvalidOperationException("The GLES resource reference count overflowed.");

            if (Interlocked.CompareExchange(ref _state, state + 1, state) == state)
                return true;
        }
    }

    /// <summary>Releases a command/dependent reference. Returns true when the native object has no users.</summary>
    internal bool ReleaseReference()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            int references = state & int.MaxValue;
            if (references == 0)
                throw new InvalidOperationException("A GLES resource reference was released more than once.");

            int next = (state & int.MinValue) | (references - 1);
            if (Interlocked.CompareExchange(ref _state, next, state) == state)
                return references == 1;
        }
    }

    /// <summary>
    /// Closes public retains and releases the owner reference exactly once. Returns true when the
    /// native object is immediately eligible for deletion.
    /// </summary>
    internal bool CloseOwner()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if (state < 0)
                return false;

            int references = state & int.MaxValue;
            if (references == 0)
                throw new InvalidOperationException("The GLES resource owner reference was already released.");

            int next = int.MinValue | (references - 1);
            if (Interlocked.CompareExchange(ref _state, next, state) == state)
                return references == 1;
        }
    }
}
