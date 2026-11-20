using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Devices;

/// <summary>
/// Implemented by a backend assembly to advertise availability, enumerate adapters, and create devices.
/// Implementations must not create a window or presentation surface as part of device creation.
/// </summary>
public interface IGraphicsBackendProvider
{
    GraphicsBackend Backend { get; }

    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    ValueTask<ReadOnlyMemory<GraphicsAdapterInfo>> EnumerateAdaptersAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IGraphicsDevice> CreateDeviceAsync(
        GraphicsDeviceOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit registry for backend providers. Register providers during application startup, before
/// creating devices. Registration and lookup are intentionally free of reflection and assembly scanning.
/// </summary>
public sealed class GraphicsBackendProviderRegistry
{
    private IGraphicsBackendProvider[] _providers = [];

    public int Count => _providers.Length;

    public void Register(IGraphicsBackendProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (!Enum.IsDefined(provider.Backend))
            throw new ArgumentOutOfRangeException(nameof(provider), "The provider reports an unknown backend.");

        for (int i = 0; i < _providers.Length; i++)
        {
            if (_providers[i].Backend == provider.Backend)
                throw new InvalidOperationException($"A provider for {provider.Backend} is already registered.");
        }

        IGraphicsBackendProvider[] next = new IGraphicsBackendProvider[_providers.Length + 1];
        Array.Copy(_providers, next, _providers.Length);
        next[^1] = provider;
        _providers = next;
    }

    public bool TryGetProvider(GraphicsBackend backend, out IGraphicsBackendProvider? provider)
    {
        for (int i = 0; i < _providers.Length; i++)
        {
            if (_providers[i].Backend == backend)
            {
                provider = _providers[i];
                return true;
            }
        }

        provider = null;
        return false;
    }

    public IGraphicsBackendProvider GetProvider(GraphicsBackend backend)
    {
        if (TryGetProvider(backend, out IGraphicsBackendProvider? provider))
            return provider!;

        throw new GraphicsBackendUnavailableException(backend, "No provider has been registered.");
    }

    internal IGraphicsBackendProvider[] GetProvidersSnapshot() => _providers;
}

/// <summary>Common device creation, adapter enumeration, and backend-selection entry point.</summary>
public static class GraphicsDeviceFactory
{
    public static ValueTask<ReadOnlyMemory<GraphicsAdapterInfo>> EnumerateAdaptersAsync(
        GraphicsBackendProviderRegistry registry,
        GraphicsBackend backend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        IGraphicsBackendProvider provider = registry.GetProvider(backend);

        if (!provider.IsAvailable)
            throw new GraphicsBackendUnavailableException(backend, provider.UnavailableReason);

        return provider.EnumerateAdaptersAsync(cancellationToken);
    }

    public static async ValueTask<IGraphicsDevice> CreateAsync(
        GraphicsBackendProviderRegistry registry,
        GraphicsDeviceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.Backend is { } requestedBackend)
        {
            IGraphicsBackendProvider provider = registry.GetProvider(requestedBackend);
            return await CreateFromProviderAsync(provider, options, cancellationToken).ConfigureAwait(false);
        }

        GraphicsBackendUnavailableException? lastUnavailable = null;
        GraphicsFeatureNotSupportedException? lastFeatureMismatch = null;
        GraphicsQueueCapabilitiesNotSupportedException? lastQueueMismatch = null;

        IGraphicsBackendProvider[] providers = registry.GetProvidersSnapshot();
        for (int i = 0; i < providers.Length; i++)
        {
            IGraphicsBackendProvider provider = providers[i];
            if (!provider.IsAvailable)
            {
                lastUnavailable = new GraphicsBackendUnavailableException(
                    provider.Backend, provider.UnavailableReason);
                continue;
            }

            try
            {
                return await CreateFromProviderAsync(provider, options, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphicsBackendUnavailableException exception)
            {
                lastUnavailable = exception;
            }
            catch (GraphicsFeatureNotSupportedException exception)
            {
                lastFeatureMismatch = exception;
            }
            catch (GraphicsQueueCapabilitiesNotSupportedException exception)
            {
                lastQueueMismatch = exception;
            }
        }

        if (lastFeatureMismatch is not null)
            throw lastFeatureMismatch;
        if (lastQueueMismatch is not null)
            throw lastQueueMismatch;
        if (lastUnavailable is not null)
            throw lastUnavailable;

        throw new GraphicsException("No graphics backend providers have been registered.");
    }

    private static async ValueTask<IGraphicsDevice> CreateFromProviderAsync(
        IGraphicsBackendProvider provider,
        GraphicsDeviceOptions options,
        CancellationToken cancellationToken)
    {
        if (!provider.IsAvailable)
            throw new GraphicsBackendUnavailableException(provider.Backend, provider.UnavailableReason);

        IGraphicsDevice device = await provider.CreateDeviceAsync(options, cancellationToken).ConfigureAwait(false);

        if (device.Backend != provider.Backend)
        {
            GraphicsBackend actualBackend = device.Backend;
            device.Dispose();
            throw new GraphicsException(
                $"The {provider.Backend} provider returned a device identified as {actualBackend}.");
        }

        if (options.Adapter is { } requestedAdapter && device.Adapter.Id != requestedAdapter)
        {
            device.Dispose();
            throw new GraphicsException("The backend returned a different adapter than the one requested.");
        }

        GraphicsFeature missingFeatures = device.Capabilities.Features.MissingFrom(options.RequiredFeatures);
        GraphicsQueueCapabilities missingQueues =
            options.RequiredQueues & ~device.Capabilities.QueueCapabilities;

        if (missingFeatures != GraphicsFeature.None)
        {
            device.Dispose();
            throw new GraphicsFeatureNotSupportedException(options.RequiredFeatures, missingFeatures);
        }

        if (missingQueues != GraphicsQueueCapabilities.None)
        {
            device.Dispose();
            throw new GraphicsQueueCapabilitiesNotSupportedException(options.RequiredQueues, missingQueues);
        }

        return device;
    }
}
