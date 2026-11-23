using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Xunit.Sdk;

namespace Auralis.Graphics.OpenGLES.IntegrationTests;

/// <summary>Creates and shares one OpenGL ES device/context across the integration-test class.</summary>
/// <remarks>Unavailable native GLFW or GLES environments are converted into per-test skips; unexpected initialization failures still fail fixture construction.</remarks>
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

    /// <summary>Returns the initialized GLES device or skips the requesting test when no usable context exists.</summary>
    /// <returns>The shared graphics device.</returns>
    /// <exception cref="Xunit.Sdk.SkipException">A usable OpenGL ES 3.1-or-newer context could not be created.</exception>
    public IGraphicsDevice GetDeviceOrSkip()
    {
        if (_device is null)
            throw SkipException.ForSkip($"OpenGL ES integration tests require a usable OpenGL ES 3.1+ context: {_unavailableReason}");
        return _device;
    }

    /// <summary>Returns the GLFW context provider or skips the requesting test when context creation was unavailable.</summary>
    /// <returns>The provider for the fixture's GLES context.</returns>
    /// <exception cref="Xunit.Sdk.SkipException">A usable OpenGL ES 3.1-or-newer context could not be created.</exception>
    public GlfwOpenGLESContextProvider GetContextOrSkip()
    {
        if (_context is null)
            throw SkipException.ForSkip($"OpenGL ES integration tests require a usable OpenGL ES 3.1+ context: {_unavailableReason}");
        return _context;
    }

    /// <summary>Disposes the device first, allowing it to drain GPU work and release resources before context teardown.</summary>
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
