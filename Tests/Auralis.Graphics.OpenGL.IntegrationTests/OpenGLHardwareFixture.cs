using Auralis.Graphics.Abstractions;
using Auralis.Graphics.OpenGL.TestSupport;
using Xunit.Sdk;

namespace Auralis.Graphics.OpenGL.IntegrationTests;

public sealed class OpenGLHardwareFixture : IDisposable
{
    private readonly GlfwOpenGLHardwareSession? _session;
    private readonly string? _unavailableReason;

    public OpenGLHardwareFixture()
    {
        try
        {
            _session = GlfwOpenGLHardwareSession.CreateAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (IsHardwareUnavailable(exception))
        {
            _unavailableReason = exception.Message;
        }
    }

    public GlfwOpenGLHardwareSession GetSessionOrSkip()
    {
        if (_session is null)
            throw SkipException.ForSkip($"Desktop OpenGL integration tests require a usable OpenGL 4.5 Core context: {_unavailableReason}");
        return _session;
    }

    public void Dispose() => _session?.Dispose();

    private static bool IsHardwareUnavailable(Exception exception) =>
        exception is HardwareContextUnavailableException
            or GraphicsBackendUnavailableException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or PlatformNotSupportedException;
}
