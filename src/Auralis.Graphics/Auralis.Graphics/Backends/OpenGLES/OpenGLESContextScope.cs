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

    public void Dispose()
    {
        OpenGLESGraphicsDevice? device = _device;
        _device = null;
        device?.ReleaseContext();
    }
}
