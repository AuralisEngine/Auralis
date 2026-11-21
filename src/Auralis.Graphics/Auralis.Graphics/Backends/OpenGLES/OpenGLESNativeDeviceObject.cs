using Auralis.Graphics.Abstractions;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

internal interface IOpenGLESNativeResource
{
    bool TryRetain();
    bool TryRetainDependency();
    void ReleaseReference();
    void ReleaseNative(GL gl);
}

/// <summary>
/// Native device object with an owner reference and references held by recorded/submitted commands.
/// Dispose closes new retains immediately; native deletion occurs only after the final recorded use ends.
/// </summary>
internal abstract class OpenGLESNativeDeviceObject : GraphicsDeviceObject, IOpenGLESNativeResource
{
    private readonly OpenGLESGraphicsDevice _device;
    private int _referenceState = 1; // one owner reference; sign bit means no new retains

    protected OpenGLESNativeDeviceObject(OpenGLESGraphicsDevice device) : base(device) =>
        _device = device;

    public bool TryRetain()
    {
        while (true)
        {
            int state = Volatile.Read(ref _referenceState);
            int refs = state & int.MaxValue;
            if (state < 0 || refs == 0)
                return false;
            if (refs == int.MaxValue)
                throw new InvalidOperationException("The GLES resource reference count overflowed.");
            if (Interlocked.CompareExchange(ref _referenceState, state + 1, state) == state)
                return true;
        }
    }

    /// <summary>
    /// Acquires a command-held reference through an already-live dependent object (for example a texture
    /// view). Unlike a public retain, this remains valid after owner disposal has closed new public retains.
    /// </summary>
    public bool TryRetainDependency()
    {
        while (true)
        {
            int state = Volatile.Read(ref _referenceState);
            int refs = state & int.MaxValue;
            if (refs == 0)
                return false;
            if (refs == int.MaxValue)
                throw new InvalidOperationException("The GLES resource reference count overflowed.");
            int next = (state & int.MinValue) | (refs + 1);
            if (Interlocked.CompareExchange(ref _referenceState, next, state) == state)
                return true;
        }
    }

    public void ReleaseReference()
    {
        while (true)
        {
            int state = Volatile.Read(ref _referenceState);
            int refs = state & int.MaxValue;
            if (refs == 0)
                throw new InvalidOperationException("A GLES resource reference was released more than once.");

            int next = (state & int.MinValue) | (refs - 1);
            if (Interlocked.CompareExchange(ref _referenceState, next, state) != state)
                continue;
            if (refs == 1)
                _device.ReleaseResource(this);
            return;
        }
    }

    protected void ReleaseOwnerReference()
    {
        while (true)
        {
            int state = Volatile.Read(ref _referenceState);
            if (state < 0)
                return;
            if (Interlocked.CompareExchange(ref _referenceState, state | int.MinValue, state) != state)
                continue;
            ReleaseReference();
            return;
        }
    }

    public abstract void ReleaseNative(GL gl);
}
