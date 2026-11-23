using Silk.NET.OpenGL;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Stack-only ownership of the device context gate and one reentrant context activation.
/// Dispose on the acquiring thread, including when the guarded operation throws.
/// </summary>
internal ref struct OpenGLContextScope
{
    private OpenGLGraphicsDevice? _device;

    /// <summary>Creates a stack-only scope for a context gate and activation already acquired by the device.</summary>
    /// <param name="device">Device whose gate and activation this scope releases.</param>
    /// <param name="gl">Dispatch object valid while the activation remains held.</param>
    {
        _device = device;
        Gl = gl;
    }

    /// <summary>Gets the GL dispatch object for the duration of this scope.</summary>

    /// <summary>Releases one activation depth and the device context gate exactly once.</summary>
    /// <remarks>Must be called on the thread that acquired this stack-only scope, including during exception unwinding.</remarks>
    public void Dispose()
    {
        OpenGLGraphicsDevice? device = _device;
        _device = null;
        device?.ReleaseContext();
    }
}
