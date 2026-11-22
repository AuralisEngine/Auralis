using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Devices;

namespace Auralis.Graphics.OpenGL.TestSupport;

/// <summary>
/// Owns one hidden GLFW OpenGL ES 3.1+ context and the Auralis device created from it.
/// Device disposal releases pending work and native GLES resources before the provider destroys
/// the context and its GLFW window.
/// </summary>
public sealed class GlfwOpenGLESHardwareSession : IDisposable
{
    private int _disposed;

    private GlfwOpenGLESHardwareSession(
        GlfwOpenGLESContextProvider contextProvider,
        IGraphicsDevice device)
    {
        ContextProvider = contextProvider;
        Device = device;
    }

    public GlfwOpenGLESContextProvider ContextProvider { get; }

    public IGraphicsDevice Device { get; }

    public static async ValueTask<GlfwOpenGLESHardwareSession> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        GlfwOpenGLESContextProvider contextProvider = GlfwOpenGLESContextProvider.Create();

        try
        {
            GraphicsBackendProviderRegistry registry = new();
            registry.Register(new OpenGLESGraphicsBackendProvider(contextProvider));

            IGraphicsDevice device = await GraphicsDeviceFactory.CreateAsync(
                registry,
                new GraphicsDeviceOptions
                {
                    Backend = GraphicsBackend.OpenGLES,
                    RequiredQueues =
                        GraphicsQueueCapabilities.Graphics |
                        GraphicsQueueCapabilities.Compute |
                        GraphicsQueueCapabilities.Copy
                },
                cancellationToken).ConfigureAwait(false);

            // The provider declares owned context ownership. The device therefore disposes it
            // after draining its queue and releasing native resources.
            return new GlfwOpenGLESHardwareSession(contextProvider, device);
        }
        catch (Exception initializationError)
        {
            // The backend provider also guards failed device initialization. Provider disposal is
            // idempotent, so this is safe both when that guard already ran and when factory
            // selection failed before reaching it.
            try
            {
                contextProvider.Dispose();
            }
            catch (Exception disposalError)
            {
                throw new AggregateException(
                    "OpenGL ES hardware-session initialization failed and its GLFW context could not be disposed.",
                    initializationError,
                    disposalError);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // OpenGLESGraphicsDevice owns this provider. Its Dispose finishes GPU work, drains
        // submissions and releases native resources before disposing the context provider.
        Device.Dispose();
    }
}
