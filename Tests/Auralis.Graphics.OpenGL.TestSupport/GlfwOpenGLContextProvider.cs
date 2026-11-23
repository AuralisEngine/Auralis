using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.Presentation;
using Silk.NET.GLFW;
using Silk.NET.OpenGL;

namespace Auralis.Graphics.OpenGL.TestSupport;

/// <summary>
/// Owns a hidden GLFW window and a desktop OpenGL 4.5 Core context for opt-in hardware tests
/// and hardware benchmarks. This helper is test/benchmark infrastructure, not a backend dependency.
/// </summary>
public sealed unsafe class GlfwOpenGLContextProvider : IOpenGLContextProvider
{
    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly object _contextIdentity = new();
    private readonly ThreadLocal<int> _currentDepth = new();
    private int _disposed;
    private int _failNextSwapBuffers;

    private GlfwOpenGLContextProvider(Glfw glfw, WindowHandle* window)
    {
        _glfw = glfw;
        _window = window;
    }

    /// <summary>Creates a hidden GLFW window and a desktop OpenGL 4.5 Core Profile context for hardware tests and benchmarks.</summary>
    /// <returns>An owned provider with exclusive context-state ownership and no presentation drawable.</returns>
    /// <exception cref="HardwareContextUnavailableException">The GLFW native library, display, context creation, or required OpenGL context is unavailable.</exception>
    public static GlfwOpenGLContextProvider Create()
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
                "The GLFW native library required for desktop OpenGL integration tests could not be loaded.",
                exception);
        }
        WindowHandle* window = null;
        bool initialized = false;

        try
        {
            if (!glfw.Init())
                throw new HardwareContextUnavailableException(
                    "GLFW initialization failed. A usable desktop session/display and GLFW native library are required.");

            initialized = true;
            glfw.DefaultWindowHints();
            glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.OpenGL);
            glfw.WindowHint(WindowHintInt.ContextVersionMajor, 4);
            glfw.WindowHint(WindowHintInt.ContextVersionMinor, 5);
            glfw.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);
            glfw.WindowHint(WindowHintBool.Visible, false);
            glfw.WindowHint(WindowHintBool.Resizable, true);

            window = glfw.CreateWindow(64, 64, "Auralis OpenGL Hardware Tests", null, null);
            if (window is null)
                throw new HardwareContextUnavailableException(
                    "GLFW could not create a hidden OpenGL 4.5 Core window/context. The driver may not expose OpenGL 4.5.");

            glfw.MakeContextCurrent(window);
            if (glfw.GetCurrentContext() != window)
                throw new HardwareContextUnavailableException("GLFW did not make the requested OpenGL context current.");

            return new GlfwOpenGLContextProvider(glfw, window);
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
    public object ContextIdentity => _contextIdentity;
    /// <inheritdoc />
    public OpenGLContextOwnership Ownership => OpenGLContextOwnership.Owned;
    /// <inheritdoc />
    public OpenGLContextStateOwnership StateOwnership => OpenGLContextStateOwnership.Exclusive;
    /// <inheritdoc />
    public bool IsAvailable => Volatile.Read(ref _disposed) == 0;
    /// <inheritdoc />
    public string? UnavailableReason => IsAvailable ? null : "The GLFW context provider has been disposed.";
    /// <inheritdoc />
    /// <inheritdoc />
    public bool SupportsPresentation => IsAvailable;

    /// <inheritdoc />
    public bool IsPresentationTargetCompatible(in GraphicsPresentationTargetSnapshot target) =>
        IsAvailable &&
        target.NativeSurface.Platform == GraphicsNativePlatform.Custom &&
        target.NativeSurface.TryGetHandle(
            GraphicsNativeHandleKind.CreateCustom(GlfwPresentationTarget.NativeWindowHandleKind),
            out GraphicsNativeHandle handle) &&
        handle.Value == (nint)_window &&
        handle.Ownership == GraphicsNativeHandleOwnership.Borrowed;

    /// <inheritdoc />
    public bool SupportsPresentMode(GraphicsPresentMode presentMode) =>
        presentMode is GraphicsPresentMode.Fifo or GraphicsPresentMode.Immediate;

    /// <summary>
    /// Requests a one-shot SwapBuffers failure after the backend has already submitted its blit and
    /// presentation fence. This exercises the real Present ownership transition in hardware tests.
    /// </summary>
    public bool FailNextSwapBuffers
    {
        get => Volatile.Read(ref _failNextSwapBuffers) != 0;
        set => Volatile.Write(ref _failNextSwapBuffers, value ? 1 : 0);
    }

    /// <summary>Creates a target adapter for this provider's own GLFW drawable.</summary>
    public GlfwPresentationTarget CreatePresentationTarget()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _glfw.GetFramebufferSize(_window, out int width, out int height);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("The GLFW drawable has no non-zero framebuffer extent.");
        return new GlfwPresentationTarget((nint)_window, (uint)width, (uint)height);
    }

    /// <summary>
    /// Resizes this provider's drawable and advances the target revision to the actual framebuffer
    /// extent reported by GLFW (which may differ from logical window units on scaled displays).
    /// </summary>
    public GraphicsPresentationTargetSnapshot ResizePresentationTarget(
        GlfwPresentationTarget target,
        uint width,
        uint height)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (target.WindowHandle != (nint)_window)
            throw new ArgumentException("The presentation target belongs to another GLFW context provider.", nameof(target));
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width));

        _glfw.SetWindowSize(_window, (int)width, (int)height);
        _glfw.PollEvents();
        _glfw.GetFramebufferSize(_window, out int framebufferWidth, out int framebufferHeight);
        if (framebufferWidth <= 0 || framebufferHeight <= 0)
            throw new InvalidOperationException("GLFW did not produce a non-zero framebuffer extent after resizing.");
        target.UpdateExtent((uint)framebufferWidth, (uint)framebufferHeight);
        return target.GetSnapshot();
    }

    /// <inheritdoc />
    public void SetPresentMode(GraphicsPresentMode presentMode)
    {
        if (!SupportsPresentMode(presentMode))
            throw new NotSupportedException($"The GLFW test drawable cannot honor present mode {presentMode}.");

        bool activatedHere = _currentDepth.Value == 0;
        if (activatedHere)
            MakeCurrent();

        try
        {
            _glfw.SwapInterval(presentMode == GraphicsPresentMode.Fifo ? 1 : 0);
        }
        finally
        {
            if (activatedHere)
                ReleaseCurrent();
        }
    }

    public void MakeCurrent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        int depth = _currentDepth.Value;
        if (depth != 0)
        {
            if (_glfw.GetCurrentContext() != _window)
                throw new InvalidOperationException("The nested desktop GL context is no longer current.");
            _currentDepth.Value = checked(depth + 1);
            return;
        }

        _glfw.MakeContextCurrent(_window);
        if (_glfw.GetCurrentContext() != _window)
            throw new InvalidOperationException("GLFW failed to activate the hardware-test context.");
        _currentDepth.Value = 1;
    }

    /// <inheritdoc />
    public void ReleaseCurrent()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        int depth = _currentDepth.Value;
        if (depth <= 0)
            throw new InvalidOperationException("The desktop GL test context was released without a matching activation.");
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

    /// <summary>
    /// Drains only the GL error flag after a completed test operation. It does not mutate GL state
    /// and is intentionally exposed only by this opt-in test-support provider.
    /// </summary>
    public IReadOnlyList<GLEnum> DrainErrors()
    {
        MakeCurrent();
        try
        {
            using GL gl = GL.GetApi(GetProcAddress);
            List<GLEnum> errors = new(2);
            for (int i = 0; i < 32; i++)
            {
                GLEnum error = gl.GetError();
                if (error == GLEnum.NoError)
                    break;
                errors.Add(error);
            }

            return errors;
        }
        finally
        {
            ReleaseCurrent();
        }
    }

    /// <inheritdoc />
    public void SwapBuffers()
    {
        if (Interlocked.Exchange(ref _failNextSwapBuffers, 0) != 0)
            throw new InvalidOperationException("Injected GLFW SwapBuffers failure after presentation sync publication.");

        bool activatedHere = _currentDepth.Value == 0;
        if (activatedHere)
            MakeCurrent();

        try
        {
            _glfw.SwapBuffers(_window);
        }
        finally
        {
            if (activatedHere)
                ReleaseCurrent();
        }
    }

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
