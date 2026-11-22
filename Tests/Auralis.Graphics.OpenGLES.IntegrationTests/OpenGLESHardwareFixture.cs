using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Xunit.Sdk;

namespace Auralis.Graphics.OpenGLES.IntegrationTests;

public sealed class OpenGLESHardwareFixture : IDisposable
{
    private readonly GlfwOpenGLESContextProvider? _context;
    private readonly IGraphicsDevice? _device;
    private readonly string? _unavailableReason;

    public OpenGLESHardwareFixture()
    {
        GlfwOpenGLESContextProvider? context = null;
        try
        {
            context = GlfwOpenGLESContextProvider.Create();
            GraphicsBackendProviderRegistry registry = new();
            registry.Register(new OpenGLESGraphicsBackendProvider(context));
            _device = GraphicsDeviceFactory.CreateAsync(
                registry,
                new GraphicsDeviceOptions
                {
                    Backend = GraphicsBackend.OpenGLES,
                    RequiredQueues = GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute
                }).AsTask().GetAwaiter().GetResult();
            _context = context;
        }
        catch (Exception exception) when (IsHardwareUnavailable(exception))
        {
            context?.Dispose();
            _unavailableReason = exception.Message;
        }
        catch
        {
            context?.Dispose();
            throw;
        }
    }

    public IGraphicsDevice GetDeviceOrSkip()
    {
        if (_device is null)
            throw SkipException.ForSkip($"OpenGL ES integration tests require a usable OpenGL ES 3.1+ context: {_unavailableReason}");
        return _device;
    }

    public GlfwOpenGLESContextProvider GetContextOrSkip()
    {
        if (_context is null)
            throw SkipException.ForSkip($"OpenGL ES integration tests require a usable OpenGL ES 3.1+ context: {_unavailableReason}");
        return _context;
    }

    public void Dispose()
    {
        if (_device is not null) _device.Dispose();
        else _context?.Dispose();
    }

    private static bool IsHardwareUnavailable(Exception exception) =>
        exception is HardwareContextUnavailableException
            or GraphicsBackendUnavailableException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or PlatformNotSupportedException;
}
