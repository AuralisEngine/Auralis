using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.Devices;

namespace Auralis.Graphics.OpenGL.TestSupport;

/// <summary>Owns one real GLFW OpenGL context and one Auralis device for a test/benchmark process.</summary>
public sealed class GlfwOpenGLHardwareSession : IDisposable
{
    private GlfwOpenGLHardwareSession(
        GlfwOpenGLContextProvider contextProvider,
        IGraphicsDevice device)
    {
        ContextProvider = contextProvider;
        Device = device;
    }

    /// <summary>Gets the GLFW provider for the native desktop OpenGL context owned by the device.</summary>
    public GlfwOpenGLContextProvider ContextProvider { get; }
    /// <summary>Gets the Auralis graphics device created from <see cref="ContextProvider" />.</summary>
    public IGraphicsDevice Device { get; }

    /// <summary>Creates a hidden GLFW OpenGL context and initializes an Auralis device over it.</summary>
    /// <param name="cancellationToken">Token passed to graphics-device creation.</param>
    /// <returns>A session that owns the device and its transferred context-provider lifetime.</returns>
    /// <exception cref="HardwareContextUnavailableException">GLFW or a suitable OpenGL 4.5 Core context is unavailable.</exception>
    /// <exception cref="GraphicsBackendUnavailableException">The injected context cannot satisfy the backend requirements.</exception>
    /// <exception cref="OperationCanceledException">Device creation was canceled.</exception>
    public static async ValueTask<GlfwOpenGLHardwareSession> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        GlfwOpenGLContextProvider contextProvider = GlfwOpenGLContextProvider.Create();
        try
        {
            GraphicsBackendProviderRegistry registry = new();
            registry.Register(new OpenGLGraphicsBackendProvider(contextProvider));

            IGraphicsDevice device = await GraphicsDeviceFactory.CreateAsync(
                registry,
                new GraphicsDeviceOptions
                {
                    Backend = GraphicsBackend.OpenGL,
                    RequiredQueues =
                        GraphicsQueueCapabilities.Graphics |
                        GraphicsQueueCapabilities.Compute |
                        GraphicsQueueCapabilities.Copy
                },
                cancellationToken).ConfigureAwait(false);

            return new GlfwOpenGLHardwareSession(contextProvider, device);
        }
        catch
        {
            contextProvider.Dispose();
            throw;
        }
    }

    /// <summary>Disposes the graphics device, which releases Auralis resources before destroying the owned GLFW context.</summary>
    public void Dispose()
    {
        // The provider transfers context ownership to the device. Device disposal releases all
        // Auralis GL resources before destroying the GLFW context and native library.
        Device.Dispose();
    }
}
