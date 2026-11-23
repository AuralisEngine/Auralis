using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Stack-only ownership of the device context gate and one reentrant context activation.
/// Dispose on the acquiring thread, including when the guarded operation throws.
/// </summary>
internal ref struct OpenGLESContextScope
{
    private OpenGLESGraphicsDevice? _device;

    /// <summary>Creates a stack-only scope for a GLES context gate and activation already acquired by the device.</summary>
    /// <param name="device">Device whose gate and activation this scope releases.</param>
    /// <param name="gl">GLES dispatch object valid while the activation remains held.</param>
    internal OpenGLESContextScope(OpenGLESGraphicsDevice device, GL gl)
    {
        _device = device;
        Gl = gl;
    }

    /// <summary>Gets the GLES dispatch object for the lifetime of this scope.</summary>
    internal GL Gl { get; }

    /// <summary>Releases one GLES context activation depth and the device context gate exactly once.</summary>
    /// <remarks>Must be called on the acquiring thread, including while unwinding an exception.</remarks>
    public void Dispose()
    {
        OpenGLESGraphicsDevice? device = _device;
        _device = null;
        device?.ReleaseContext();
    }
}
