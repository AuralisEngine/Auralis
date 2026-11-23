using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Stack-only ownership of the device context gate and one reentrant context activation.
/// Dispose on the acquiring thread, including when the guarded operation throws.
/// </summary>
internal ref struct OpenGLESContextScope
{
    private OpenGLESGraphicsDevice? _device;

    internal OpenGLESContextScope(OpenGLESGraphicsDevice device, GL gl)
    {
        _device = device;
        Gl = gl;
    }

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
