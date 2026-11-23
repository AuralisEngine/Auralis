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

    /// <summary>Releases one activation depth and the device context gate exactly once.</summary>
    /// <remarks>Must be called on the thread that acquired this stack-only scope, including during exception unwinding.</remarks>
    public void Dispose()
    {
        OpenGLGraphicsDevice? device = _device;
        _device = null;
        device?.ReleaseContext();
    }
}
