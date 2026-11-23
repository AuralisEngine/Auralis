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
    private int _presentMode = (int)GraphicsPresentMode.Fifo;
    private int _failNextSwapBuffers;

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
            glfw.WindowHint(WindowHintBool.Resizable, true);

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

    /// <summary>Gets the last presentation mode successfully applied to the GLFW drawable.</summary>
    public GraphicsPresentMode CurrentPresentMode =>
        (GraphicsPresentMode)Volatile.Read(ref _presentMode);

    /// <summary>
    /// Requests a one-shot SwapBuffers failure after the backend has already submitted its blit and
    /// presentation fence. This exercises the real Present ownership transition in hardware tests.
    /// </summary>
    public bool FailNextSwapBuffers
    {
        get => Volatile.Read(ref _failNextSwapBuffers) != 0;
        set => Volatile.Write(ref _failNextSwapBuffers, value ? 1 : 0);
    }

    /// <summary>Runs on the current context immediately before GLFW swaps the host drawable.</summary>
    public Action? BeforeSwapBuffers { get; set; }

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
            Volatile.Write(ref _presentMode, (int)presentMode);
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
    public void SwapBuffers()
    {
        if (Interlocked.Exchange(ref _failNextSwapBuffers, 0) != 0)
            throw new InvalidOperationException("Injected GLFW SwapBuffers failure after presentation sync publication.");

        bool activatedHere = _currentDepth.Value == 0;
        if (activatedHere)
            MakeCurrent();

        try
        {
            BeforeSwapBuffers?.Invoke();
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
