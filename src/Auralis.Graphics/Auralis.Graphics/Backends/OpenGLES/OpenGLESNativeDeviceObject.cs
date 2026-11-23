using Auralis.Graphics.Abstractions;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>Internal lifetime contract shared by native GLES objects retained by commands and dependent views.</summary>
internal interface IOpenGLESNativeResource
{
    /// <summary>Attempts a new public/command retain while the owner reference remains open.</summary>
    bool TryRetain();
    /// <summary>Attempts a dependent retain while an existing reference keeps the native object alive.</summary>
    bool TryRetainDependency();
    /// <summary>Releases one acquired reference and schedules native deletion when the count reaches zero.</summary>
    void ReleaseReference();
    /// <summary>Releases the native handle while the owning GLES context is current.</summary>
    void ReleaseNative(GL gl);
}

/// <summary>
/// Native device object with one owner reference plus references held by views and recorded/submitted
/// commands. Dispose closes public retains immediately; native deletion occurs after the final use ends.
/// </summary>
internal abstract class OpenGLESNativeDeviceObject : GraphicsDeviceObject, IOpenGLESNativeResource
{
    private readonly OpenGLESGraphicsDevice _device;
    private readonly OpenGLESResourceReferenceState _references = new();

    protected OpenGLESNativeDeviceObject(OpenGLESGraphicsDevice device) : base(device) =>
        _device = device;

    /// <inheritdoc />
    public bool TryRetain() => _references.TryRetain();

    /// <inheritdoc />
    public bool TryRetainDependency() => _references.TryRetainDependency();

    /// <inheritdoc />
    public void ReleaseReference()
    {
        if (_references.ReleaseReference())
            _device.ReleaseResource(this);
    }

    /// <summary>Closes new owner-side retains and releases the native object's initial owner reference.</summary>
    /// <remarks>Dependent views and recorded/submitted commands can keep native storage alive after this call.</remarks>
    protected void ReleaseOwnerReference()
    {
        if (_references.CloseOwner())
            _device.ReleaseResource(this);
    }

    /// <inheritdoc />
    public abstract void ReleaseNative(GL gl);
}
