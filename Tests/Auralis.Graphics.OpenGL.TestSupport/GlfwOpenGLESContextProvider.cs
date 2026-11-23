using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Presentation;
using Silk.NET.GLFW;

namespace Auralis.Graphics.OpenGL.TestSupport;

/// <summary>
/// Creates a hidden GLFW OpenGL ES 3.1 context for hardware integration tests and benchmarks. This helper lives in test
/// support; the graphics backend itself receives the already-created context and owns no EGL/Android code.
/// </summary>
public sealed unsafe class GlfwOpenGLESContextProvider : IOpenGLESContextProvider
{
    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly object _identity = new();
    private readonly ThreadLocal<int> _currentDepth = new();
    private int _disposed;

    private GlfwOpenGLESContextProvider(Glfw glfw, WindowHandle* window)
    {
        _glfw = glfw;
        _window = window;
    }

    /// <summary>Creates a hidden GLFW window and an OpenGL ES 3.1 context for hardware tests and benchmarks.</summary>
    /// <returns>An owned provider with exclusive context-state ownership and no presentation drawable.</returns>
    /// <exception cref="HardwareContextUnavailableException">The GLFW native library, display, or required GLES context is unavailable.</exception>
    public static GlfwOpenGLESContextProvider Create()
    {
        Glfw glfw;
        try
        {
            glfw = Glfw.GetApi();
        }
        catch (Exception exception) when (exception is
            DllNotFoundException or FileNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new HardwareContextUnavailableException(
                "The GLFW native library required for OpenGL ES hardware tests and benchmarks could not be loaded.",
                exception);
        }
        WindowHandle* window = null;
        bool initialized = false;
        try
        {
            if (!glfw.Init())
                throw new HardwareContextUnavailableException("GLFW could not initialize. OpenGL ES hardware tests and benchmarks require the GLFW native library and a usable display/session.");

            initialized = true;
            glfw.DefaultWindowHints();
            glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.OpenGLES);
            glfw.WindowHint(WindowHintInt.ContextVersionMajor, 3);
            glfw.WindowHint(WindowHintInt.ContextVersionMinor, 1);
            glfw.WindowHint(WindowHintBool.Visible, false);
            glfw.WindowHint(WindowHintBool.Resizable, false);

            window = glfw.CreateWindow(64, 64, "Auralis OpenGL ES Hardware Tests", null, null);
            if (window is null)
                throw new HardwareContextUnavailableException("GLFW could not create a hidden OpenGL ES 3.1 context. A compatible GLES 3.1+ driver and usable display/session are required.");

            glfw.MakeContextCurrent(window);
            if (glfw.GetCurrentContext() != window)
                throw new HardwareContextUnavailableException("GLFW failed to make the OpenGL ES context current.");

            return new GlfwOpenGLESContextProvider(glfw, window);
        }
        catch
        {
            if (window is not null)
            {
                try { glfw.MakeContextCurrent(null); } catch { }
                try { glfw.DestroyWindow(window); } catch { }
            }

            if (initialized)
            {
                try { glfw.Terminate(); } catch { }
            }

            glfw.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public object ContextIdentity => _identity;
    /// <inheritdoc />
    public OpenGLESContextOwnership Ownership => OpenGLESContextOwnership.Owned;
    /// <inheritdoc />
    public OpenGLESContextStateOwnership StateOwnership => OpenGLESContextStateOwnership.Exclusive;
    /// <inheritdoc />
    public bool IsAvailable => Volatile.Read(ref _disposed) == 0;
    /// <inheritdoc />
    public string? UnavailableReason => IsAvailable ? null : "The GLFW OpenGL ES context has been disposed.";
    /// <inheritdoc />
    public bool SupportsPresentation => false;

    /// <inheritdoc />
    public bool IsPresentationTargetCompatible(in GraphicsPresentationTargetSnapshot target) => false;
    /// <inheritdoc />
    public bool SupportsPresentMode(GraphicsPresentMode presentMode) => false;

    /// <inheritdoc />
    public void SetPresentMode(GraphicsPresentMode presentMode) =>
        throw new NotSupportedException("The hidden GLES test drawable does not support presentation.");

    /// <inheritdoc />
    public void MakeCurrent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        int depth = _currentDepth.Value;
        if (depth != 0)
        {
            if (_glfw.GetCurrentContext() != _window)
                throw new InvalidOperationException("The nested GLES context is no longer current.");
            _currentDepth.Value = checked(depth + 1);
            return;
        }

        _glfw.MakeContextCurrent(_window);
        if (_glfw.GetCurrentContext() != _window)
            throw new InvalidOperationException("GLFW failed to activate the OpenGL ES test context.");
        _currentDepth.Value = 1;
    }

    /// <inheritdoc />
    public void ReleaseCurrent()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        int depth = _currentDepth.Value;
        if (depth <= 0)
            throw new InvalidOperationException("The GLES test context was released without a matching activation.");
        if (depth > 1)
        {
            _currentDepth.Value = depth - 1;
            return;
        }

        _currentDepth.Value = 0;
        _glfw.MakeContextCurrent(null);
    }

    /// <inheritdoc />
    public nint GetProcAddress(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _glfw.GetProcAddress(name);
    }

    /// <inheritdoc />
    public void SwapBuffers() =>
        throw new NotSupportedException("The hidden GLES test drawable does not support presentation.");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _currentDepth.Value = 0;
            if (_glfw.GetCurrentContext() == _window)
                _glfw.MakeContextCurrent(null);
        }
        finally
        {
            try { _glfw.DestroyWindow(_window); }
            finally
            {
                try { _glfw.Terminate(); }
                finally { _glfw.Dispose(); }
            }
        }
    }
}
