using Auralis.Graphics.Abstractions;
using Auralis.Graphics.OpenGL.TestSupport;
using Xunit.Sdk;

namespace Auralis.Graphics.OpenGL.IntegrationTests;

/// <summary>Creates one shared desktop OpenGL hardware session for the integration-test class.</summary>
/// <remarks>
/// Native-library, platform, and context-availability failures are captured so each hardware test
/// can be reported as skipped. Exceptions raised after a usable context has been created are not
/// converted into skips.
/// </remarks>
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

    /// <summary>Returns the initialized hardware session or marks the requesting test as skipped.</summary>
    /// <returns>The shared GLFW context and Auralis device session.</returns>
    /// <exception cref="Xunit.Sdk.SkipException">A usable desktop OpenGL 4.5 Core context could not be created.</exception>
    public GlfwOpenGLHardwareSession GetSessionOrSkip()
    {
        if (_session is null)
            throw SkipException.ForSkip($"Desktop OpenGL integration tests require a usable OpenGL 4.5 Core context: {_unavailableReason}");
        return _session;
    }

    /// <summary>Disposes the shared session and its device-owned context, when initialization succeeded.</summary>
    public void Dispose() => _session?.Dispose();

    private static bool IsHardwareUnavailable(Exception exception) =>
        exception is HardwareContextUnavailableException
            or GraphicsBackendUnavailableException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or PlatformNotSupportedException;
}
