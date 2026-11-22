using Silk.NET.OpenGL;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Stack-only ownership of the device context gate and one reentrant context activation.
/// Dispose on the acquiring thread, including when the guarded operation throws.
/// </summary>
internal ref struct OpenGLContextScope
{
    private OpenGLGraphicsDevice? _device;

    internal OpenGLContextScope(OpenGLGraphicsDevice device, GL gl)
    {
        _device = device;
        Gl = gl;
    }

    internal GL Gl { get; }

    public void Dispose()
    {
        OpenGLGraphicsDevice? device = _device;
        _device = null;
        device?.ReleaseContext();
    }
}
