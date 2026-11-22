namespace Auralis.Graphics.OpenGL.TestSupport;

/// <summary>
/// Indicates that the host cannot provide the native window/context required by a hardware test.
/// This condition can be reported as a skipped test; failures after context creation are not
/// represented by this exception.
/// </summary>
public sealed class HardwareContextUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
