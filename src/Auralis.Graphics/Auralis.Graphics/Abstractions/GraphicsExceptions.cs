namespace Auralis.Graphics.Abstractions;

/// <summary>Base exception for errors reported by the common graphics layer.</summary>
public class GraphicsException : Exception
{
    public GraphicsException(string message) : base(message) { }

    public GraphicsException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Thrown when a requested backend cannot be used on the current system.</summary>
public sealed class GraphicsBackendUnavailableException : GraphicsException
{
    public GraphicsBackend Backend { get; }

    public GraphicsBackendUnavailableException(GraphicsBackend backend, string? reason = null)
        : base(reason is null
            ? $"The {backend} graphics backend is not available."
            : $"The {backend} graphics backend is not available: {reason}")
    {
        Backend = backend;
    }
}

/// <summary>Thrown when a device does not satisfy a requested optional feature set.</summary>
public sealed class GraphicsFeatureNotSupportedException : GraphicsException
{
    public GraphicsFeature RequestedFeatures { get; }
    public GraphicsFeature MissingFeatures { get; }

    public GraphicsFeatureNotSupportedException(GraphicsFeature requested, GraphicsFeature missing)
        : base($"The graphics device does not support the required feature(s): {missing}.")
    {
        RequestedFeatures = requested;
        MissingFeatures = missing;
    }
}

/// <summary>Thrown when a device lacks one or more requested descriptor-indexing capabilities.</summary>
public sealed class GraphicsDescriptorIndexingNotSupportedException : GraphicsException
{
    public GraphicsDescriptorIndexingCapabilities Requested { get; }
    public GraphicsDescriptorIndexingCapabilities Missing { get; }

    public GraphicsDescriptorIndexingNotSupportedException(
        GraphicsDescriptorIndexingCapabilities requested,
        GraphicsDescriptorIndexingCapabilities missing)
        : base("The graphics device does not enable every required descriptor-indexing capability.")
    {
        Requested = requested;
        Missing = missing;
    }
}

/// <summary>Thrown when a device lacks one or more requested logical queue capabilities.</summary>
public sealed class GraphicsQueueCapabilitiesNotSupportedException : GraphicsException
{
    public GraphicsQueueCapabilities Requested { get; }
    public GraphicsQueueCapabilities Missing { get; }

    public GraphicsQueueCapabilitiesNotSupportedException(
        GraphicsQueueCapabilities requested,
        GraphicsQueueCapabilities missing)
        : base($"The graphics device does not expose the required queue capability(s): {missing}.")
    {
        Requested = requested;
        Missing = missing;
    }
}

/// <summary>Thrown when a graphics descriptor or operation violates the common API contract.</summary>
public sealed class GraphicsValidationException : GraphicsException
{
    public GraphicsValidationException(string message) : base(message) { }
}
