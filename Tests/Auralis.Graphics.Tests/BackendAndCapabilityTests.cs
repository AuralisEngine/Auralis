using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class BackendAndCapabilityTests
{
    [Fact]
    public void RegistryRegistersProvidersExplicitlyAndRejectsDuplicates()
    {
        var registry = new GraphicsBackendProviderRegistry();
        var provider = new TestProvider(GraphicsBackend.Vulkan);

        registry.Register(provider);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetProvider(GraphicsBackend.Vulkan, out IGraphicsBackendProvider? found));
        Assert.Same(provider, found);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new TestProvider(GraphicsBackend.Vulkan)));
    }

    [Fact]
    public void FormatCapabilitiesAreCopiedAndQueriedByFormat()
    {
        GraphicsFormatCapabilities[] formats =
        [
            new(GraphicsFormat.RGBA8UNorm,
                GraphicsFormatSupport.Sampled | GraphicsFormatSupport.ColorAttachment,
                GraphicsSampleCountFlags.One | GraphicsSampleCountFlags.Four)
        ];

        var capabilities = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.ComputeShaders),
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute,
            default,
            formats);

        formats[0] = default;

        Assert.True(capabilities.SupportsFormat(GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.Sampled));
        Assert.False(capabilities.SupportsFormat(GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.Storage));
        Assert.True(capabilities.TryGetFormatCapabilities(GraphicsFormat.RGBA8UNorm, out var format));
        Assert.True(format.SupportsSampleCount(4));
        Assert.False(format.SupportsSampleCount(2));
        Assert.True(capabilities.Features.Supports(GraphicsFeature.ComputeShaders));
    }

    [Fact]
    public void CapabilitySnapshotRejectsDuplicateFormatEntries()
    {
        GraphicsFormatCapabilities[] formats =
        [
            new(GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.Sampled, GraphicsSampleCountFlags.One),
            new(GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.Storage, GraphicsSampleCountFlags.One)
        ];

        Assert.Throws<ArgumentException>(() => new GraphicsDeviceCapabilities(
            default, GraphicsQueueCapabilities.None, default, formats));
    }

    [Fact]
    public void DeviceCapabilitiesValidateFormatUsageAndSampleCount()
    {
        var capabilities = new GraphicsDeviceCapabilities(
            default,
            GraphicsQueueCapabilities.Graphics,
            default,
            [new GraphicsFormatCapabilities(
                GraphicsFormat.RGBA8UNorm,
                GraphicsFormatSupport.Sampled | GraphicsFormatSupport.ColorAttachment,
                GraphicsSampleCountFlags.One | GraphicsSampleCountFlags.Four)]);

        var sampledTexture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            64, 64, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled);
        capabilities.ValidateTextureDescription(sampledTexture);

        var storageTexture = sampledTexture with { Usage = GraphicsTextureUsage.Storage };
        var unsupportedSamples = sampledTexture with { SampleCount = 2 };

        Assert.Throws<GraphicsException>(() => capabilities.ValidateTextureDescription(storageTexture));
        Assert.Throws<GraphicsException>(() => capabilities.ValidateTextureDescription(unsupportedSamples));
    }

    [Fact]
    public void DeviceCapabilitiesValidateAnisotropyAgainstReportedLimits()
    {
        var withoutAnisotropy = new GraphicsDeviceCapabilities(
            default, GraphicsQueueCapabilities.Graphics, default, []);

        var sampler = new GraphicsSamplerDescription(
            GraphicsFilter.Linear, GraphicsFilter.Linear, GraphicsFilter.Linear,
            GraphicsAddressMode.Repeat, GraphicsAddressMode.Repeat, GraphicsAddressMode.Repeat,
            0, 10, MaxAnisotropy: 4);

        Assert.Throws<GraphicsException>(() => withoutAnisotropy.ValidateSamplerDescription(sampler));

        var withAnisotropy = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.SamplerAnisotropy),
            GraphicsQueueCapabilities.Graphics,
            new GraphicsDeviceLimits(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8),
            []);
        withAnisotropy.ValidateSamplerDescription(sampler);
        Assert.Throws<GraphicsException>(() =>
            withAnisotropy.ValidateSamplerDescription(sampler with { MaxAnisotropy = 16 }));
    }

    [Fact]
    public void BindingLayoutRejectsDuplicateBindingsAndZeroCount()
    {
        GraphicsBindingLayoutEntry[] duplicate =
        [
            new(0, GraphicsDescriptorType.UniformBuffer, 1, GraphicsShaderStages.Vertex),
            new(0, GraphicsDescriptorType.Sampler, 1, GraphicsShaderStages.Fragment)
        ];

        GraphicsBindingLayoutEntry[] zeroCount =
        [
            new(1, GraphicsDescriptorType.StorageBuffer, 0, GraphicsShaderStages.Compute)
        ];

        Assert.Throws<ArgumentException>(() => new GraphicsBindingLayoutDescription(duplicate));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphicsBindingLayoutDescription(zeroCount));
    }

    [Fact]
    public void VertexLayoutRejectsAttributesWithMissingBindings()
    {
        GraphicsVertexBufferLayout[] buffers = [new(0, 32)];
        GraphicsVertexAttribute[] attributes = [new(0, 1, GraphicsFormat.RGBA32Float, 0)];

        Assert.Throws<ArgumentException>(() => new GraphicsVertexInputLayout(buffers, attributes));
    }

    private sealed class TestProvider(GraphicsBackend backend) : IGraphicsBackendProvider
    {
        public GraphicsBackend Backend { get; } = backend;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public ValueTask<ReadOnlyMemory<GraphicsAdapterInfo>> EnumerateAdaptersAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadOnlyMemory<GraphicsAdapterInfo>.Empty);

        public ValueTask<IGraphicsDevice> CreateDeviceAsync(
            GraphicsDeviceOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
