using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.Devices;
using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Auralis.Graphics.OpenGL.IntegrationTests;

/// <summary>
/// Owns a real desktop OpenGL context and an Auralis device for hardware-backed integration tests.
/// This fixture tests backend behavior through the public graphics API; it is not a rendering demo.
/// </summary>
public sealed class OpenGLIntegrationFixture : IAsyncLifetime
{
    private IWindow? _window;
    private SilkOpenGLContextProvider? _contextProvider;

    public IGraphicsDevice Device { get; private set; } = null!;

    public Task InitializeAsync()
    {
        WindowOptions options = WindowOptions.Default;
        options.Title = "Auralis OpenGL Integration Tests";
        options.Size = new Vector2D<int>(64, 64);
        options.IsVisible = false;
        options.VSync = false;
        options.API = new GraphicsAPI(
            ContextAPI.OpenGL,
            ContextProfile.Core,
            ContextFlags.Default,
            new APIVersion(4, 5));

        _window = Window.Create(options);
        _window.Initialize();

        IGLContext context = _window.GLContext
            ?? throw new InvalidOperationException("Silk.NET did not create an OpenGL context.");

        _contextProvider = new SilkOpenGLContextProvider(context);

        var providers = new GraphicsBackendProviderRegistry();
        providers.Register(new OpenGLGraphicsBackendProvider(_contextProvider));

        Device = GraphicsDeviceFactory.CreateAsync(
            providers,
            new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.OpenGL,
                RequiredQueues = GraphicsQueueCapabilities.Graphics |
                                 GraphicsQueueCapabilities.Copy,
                ApplicationName = "Auralis OpenGL Integration Tests"
            }).AsTask().GetAwaiter().GetResult();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Device?.Dispose();
        _contextProvider?.Dispose();
        _window?.Dispose();
        return Task.CompletedTask;
    }

    private sealed class SilkOpenGLContextProvider(IGLContext context) : IOpenGLContextProvider
    {
        public object ContextIdentity => context;

        public OpenGLContextOwnership Ownership => OpenGLContextOwnership.Borrowed;

        public OpenGLContextStateOwnership StateOwnership => OpenGLContextStateOwnership.Exclusive;

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public bool SupportsPresentation => false;

        public bool IsPresentationTargetCompatible(
            in Auralis.Graphics.Presentation.GraphicsPresentationTargetSnapshot target) => false;

        public bool SupportsPresentMode(
            Auralis.Graphics.Presentation.GraphicsPresentMode presentMode) => false;

        public void SetPresentMode(
            Auralis.Graphics.Presentation.GraphicsPresentMode presentMode) =>
            throw new NotSupportedException("Presentation is not part of this integration fixture.");

        public void MakeCurrent() => context.MakeCurrent();

        public void ReleaseCurrent() => context.Clear();

        public nint GetProcAddress(string name) => context.GetProcAddress(name);

        public void SwapBuffers() =>
            throw new NotSupportedException("Presentation is not part of this integration fixture.");

        public void Dispose()
        {
            // The window owns the native context.
        }
    }
}
