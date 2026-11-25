namespace Auralis.Graphics.Devices;

/// <summary>Observable lifecycle state of a graphics device.</summary>
public enum GraphicsDeviceStatus
{
    Operational,
    Lost,
    Disposed
}

/// <summary>Optional device extension for querying device-loss state and its diagnostic reason.</summary>
public interface IGraphicsDeviceHealth
{
    GraphicsDeviceStatus Status { get; }
    string? FailureReason { get; }
    event Action<GraphicsDeviceStatus>? StatusChanged;
}

/// <summary>Raised when an operation cannot proceed because the graphics device has been lost.</summary>
public sealed class GraphicsDeviceLostException : GraphicsException
{
    public GraphicsDeviceLostException(string? message = null, Exception? innerException = null)
        : base(message ?? "The graphics device has been lost.", innerException)
    {
    }
}
