using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Devices;

/// <summary>Observable lifecycle state of a graphics device.</summary>
public enum GraphicsDeviceStatus
{
    Operational,
    Lost,
    Disposed
}

/// <summary>
/// Optional device extension for querying device-loss state and its diagnostic reason.
/// State transitions are monotonic: Operational may become Lost, and either state may become Disposed.
/// A lost device is not expected to recover in place; applications create a replacement device.
/// </summary>
public interface IGraphicsDeviceHealth
{
    GraphicsDeviceStatus Status { get; }
    string? FailureReason { get; }
    /// <summary>Raised after a lifecycle state transition; handlers should not block the backend notification thread.</summary>
    event Action<GraphicsDeviceStatus>? StatusChanged;
}

/// <summary>Raised when an operation cannot proceed because the graphics device has been lost.</summary>
public sealed class GraphicsDeviceLostException : GraphicsException
{
    public GraphicsDeviceLostException(string? message = null)
        : base(message ?? "The graphics device has been lost.")
    {
    }

    public GraphicsDeviceLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
