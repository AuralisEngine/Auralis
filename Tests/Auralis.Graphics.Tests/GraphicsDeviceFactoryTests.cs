using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class GraphicsDeviceFactoryTests
{
    [Fact]
    public async Task FactoryCreatesRequestedBackendWithoutPresentationTarget()
    {
        var device = new TestDevice(
            GraphicsBackend.Vulkan,
            GraphicsFeature.ComputeShaders,
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute);
        var registry = new GraphicsBackendProviderRegistry();
        registry.Register(new TestProvider(GraphicsBackend.Vulkan, device));

        using IGraphicsDevice actual = await GraphicsDeviceFactory.CreateAsync(
            registry,
            new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.Vulkan,
                RequiredFeatures = GraphicsFeature.ComputeShaders,
                RequiredQueues = GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute
            });

        Assert.Same(device, actual);
        Assert.Equal(GraphicsBackend.Vulkan, actual.Backend);
    }

    [Fact]
    public async Task AutomaticSelectionSkipsUnavailableProviderAndUsesNextAvailable()
    {
        var unavailable = new TestProvider(GraphicsBackend.Direct3D12, null, isAvailable: false);
        var device = new TestDevice(
            GraphicsBackend.Metal,
            GraphicsFeature.ComputeShaders,
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute);
        var available = new TestProvider(GraphicsBackend.Metal, device);
        var registry = new GraphicsBackendProviderRegistry();
        registry.Register(unavailable);
        registry.Register(available);

        using IGraphicsDevice actual = await GraphicsDeviceFactory.CreateAsync(
            registry,
            new GraphicsDeviceOptions { RequiredFeatures = GraphicsFeature.ComputeShaders });

        Assert.Same(device, actual);
        Assert.Equal(1, available.CreateCount);
        Assert.Equal(0, unavailable.CreateCount);
    }

    [Fact]
    public async Task FactoryDisposesDeviceThatDoesNotMeetRequiredCapabilities()
    {
        var device = new TestDevice(
            GraphicsBackend.Direct3D12,
            GraphicsFeature.None,
            GraphicsQueueCapabilities.Graphics);
        var registry = new GraphicsBackendProviderRegistry();
        registry.Register(new TestProvider(GraphicsBackend.Direct3D12, device));

        await Assert.ThrowsAsync<GraphicsFeatureNotSupportedException>(async () =>
            await GraphicsDeviceFactory.CreateAsync(
                registry,
                new GraphicsDeviceOptions
                {
                    Backend = GraphicsBackend.Direct3D12,
                    RequiredFeatures = GraphicsFeature.RayTracing
                }));

        Assert.True(device.IsDisposed);
    }

    [Fact]
    public void AdapterSelectionRequiresAnExplicitBackend()
    {
        var options = new GraphicsDeviceOptions { Adapter = new GraphicsAdapterId(1, 2) };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void DeviceObjectDisposalIsDeterministicAndIdempotent()
    {
        var device = new TestDevice(
            GraphicsBackend.Vulkan,
            GraphicsFeature.None,
            GraphicsQueueCapabilities.Graphics);
        var resource = new TestDeviceObject(device);

        resource.Dispose();
        resource.Dispose();

        Assert.True(resource.IsDisposed);
        Assert.Equal(1, resource.DisposeCount);
        Assert.False(device.IsDisposed);
    }

    internal sealed class TestProvider(
        GraphicsBackend backend,
        TestDevice? device,
        bool isAvailable = true) : IGraphicsBackendProvider
    {
        public GraphicsBackend Backend { get; } = backend;
        public bool IsAvailable { get; } = isAvailable;
        public string? UnavailableReason => IsAvailable ? null : "Disabled by test.";
        public int CreateCount { get; private set; }

        public ValueTask<ReadOnlyMemory<GraphicsAdapterInfo>> EnumerateAdaptersAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadOnlyMemory<GraphicsAdapterInfo>.Empty);

        public ValueTask<IGraphicsDevice> CreateDeviceAsync(
            GraphicsDeviceOptions options,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            if (device is null)
                throw new InvalidOperationException("No test device was supplied.");
            return ValueTask.FromResult<IGraphicsDevice>(device);
        }
    }

    internal sealed class TestDevice(
        GraphicsBackend backend,
        GraphicsFeature features,
        GraphicsQueueCapabilities queues,
        GraphicsDeviceCapabilities? capabilities = null) : IGraphicsDevice
    {
        public GraphicsBackend Backend { get; } = backend;
        public GraphicsAdapterInfo Adapter { get; } = new(
            new GraphicsAdapterId(1, 2), "Test adapter", GraphicsAdapterType.Discrete, 1, 2, 1024);
        public GraphicsDeviceInfo Info { get; } = new(
            "Test device", "Test driver", "1.0", "1.0", 1, 2, false);
        public GraphicsDeviceCapabilities Capabilities { get; } = capabilities ?? new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(features), queues, default, ReadOnlySpan<GraphicsFormatCapabilities>.Empty);
        public bool IsDisposed { get; private set; }

        public IGraphicsCommandQueue GetQueue(GraphicsQueueType queueType, uint index = 0) =>
            throw new NotSupportedException();
        public IGraphicsQueue GetPresentationQueue(uint index = 0) =>
            new TestPresentationQueue(this, index);
        public IGraphicsBuffer CreateBuffer(in GraphicsBufferDescription description) =>
            throw new NotSupportedException();
        public IGraphicsTexture CreateTexture(in GraphicsTextureDescription description) =>
            throw new NotSupportedException();
        public IGraphicsSampler CreateSampler(in GraphicsSamplerDescription description) =>
            throw new NotSupportedException();
        public IGraphicsShader CreateShader(in GraphicsShaderDescription description) =>
            throw new NotSupportedException();
        public IGraphicsBindingLayout CreateBindingLayout(GraphicsBindingLayoutDescription description) =>
            throw new NotSupportedException();
        public IGraphicsPipelineLayout CreatePipelineLayout(ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts) =>
            throw new NotSupportedException();
        public IGraphicsBindingSet CreateBindingSet(IGraphicsBindingLayout bindingLayout) =>
            throw new NotSupportedException();
        public IGraphicsRenderPipeline CreateRenderPipeline(GraphicsRenderPipelineDescription description) =>
            throw new NotSupportedException();
        public IGraphicsComputePipeline CreateComputePipeline(GraphicsComputePipelineDescription description) =>
            throw new NotSupportedException();
        public IGraphicsFence CreateFence(ulong initialValue = 0) =>
            throw new NotSupportedException();
        public IGraphicsPresentationSurface CreatePresentationSurface(IGraphicsPresentationTarget target) =>
            throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;
    }

    private sealed class TestPresentationQueue(IGraphicsDevice device, uint index) : IGraphicsQueue
    {
        public IGraphicsDevice Device { get; } = device;
        public uint Index { get; } = index;
        public uint QueueFamilyIndex => 0;
    }

    private sealed class TestDeviceObject(IGraphicsDevice device) : GraphicsDeviceObject(device)
    {
        public int DisposeCount { get; private set; }

        protected override void DisposeCore() => DisposeCount++;
    }
}
