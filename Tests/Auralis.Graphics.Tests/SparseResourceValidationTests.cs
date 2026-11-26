using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class SparseResourceValidationTests
{
    [Fact]
    public void SparseBindingRequiresQueueSpecificCapabilityAndMatchingDevice()
    {
        var device = CreateDevice(GraphicsFeature.SparseResources);
        var otherDevice = CreateDevice(GraphicsFeature.SparseResources);
        var sparseDevice = new TestSparseDevice(device);
        var sparseQueue = new SparseQueue(device);
        var ordinaryQueue = new OrdinaryQueue(device);

        GraphicsSparseResourceValidation.ValidateBindingQueue(sparseDevice, sparseQueue);
        Assert.Throws<ArgumentException>(() =>
            GraphicsSparseResourceValidation.ValidateBindingQueue(sparseDevice, ordinaryQueue));
        Assert.Throws<ArgumentException>(() =>
            GraphicsSparseResourceValidation.ValidateBindingQueue(sparseDevice, new SparseQueue(otherDevice)));
    }

    [Fact]
    public void SparseBindingRejectsDeviceWithoutSparseResourcesFeature()
    {
        var device = CreateDevice(GraphicsFeature.None);
        var sparseDevice = new TestSparseDevice(device);

        Assert.Throws<GraphicsFeatureNotSupportedException>(() =>
            GraphicsSparseResourceValidation.ValidateBindingQueue(sparseDevice, new SparseQueue(device)));
    }

    [Fact]
    public void SparseTextureRequirementsAreAspectSpecific()
    {
        var texture = CreateTexture(GraphicsFormat.D24UNormS8UInt);
        var depthRequirements = new GraphicsSparseTextureRequirements(
            64, 64, 1, 65536, 1, 0, 0, 0, false, GraphicsSparseTextureAspect.Depth);
        var stencilRequirements = depthRequirements with { Aspect = GraphicsSparseTextureAspect.Stencil };

        depthRequirements.Validate(texture.Description);
        stencilRequirements.Validate(texture.Description);

        var region = new GraphicsSparseTextureRegion(
            0, 0, 0, 0, 0, 1, 1, 1, GraphicsSparseTextureAspect.Depth);
        region.Validate(texture.Description, depthRequirements);

        Assert.Throws<ArgumentException>(() =>
            region.Validate(texture.Description, stencilRequirements));
        Assert.Throws<ArgumentException>(() =>
            (depthRequirements with { Aspect = GraphicsSparseTextureAspect.Color }).Validate(texture.Description));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (depthRequirements with { Aspect = GraphicsSparseTextureAspect.None }).Validate(texture.Description));
    }

    [Fact]
    public void SparseMetadataRequirementsRepresentSingleAndPerLayerOpaqueTails()
    {
        var texture = CreateTexture(GraphicsFormat.RGBA8UNorm, layers: 4);
        var singleTail = new GraphicsSparseTextureMetadataRequirements(
            SizeInBytes: 4096,
            OffsetInBytes: 8192,
            StrideInBytes: 0,
            Alignment: 256,
            SingleMipTail: true);
        singleTail.Validate(texture.Description);

        new GraphicsSparseTextureMetadataBinding(texture, 0, null, 0).Validate(singleTail);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsSparseTextureMetadataBinding(texture, 1, null, 0).Validate(singleTail));

        var perLayer = new GraphicsSparseTextureMetadataRequirements(
            SizeInBytes: 1024,
            OffsetInBytes: 4096,
            StrideInBytes: 2048,
            Alignment: 256,
            SingleMipTail: false);
        perLayer.Validate(texture.Description);
        new GraphicsSparseTextureMetadataBinding(texture, 3, null, 0).Validate(perLayer);

        Assert.Throws<ArgumentException>(() =>
            (perLayer with { StrideInBytes = 512 }).Validate(texture.Description));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (perLayer with { OffsetInBytes = ulong.MaxValue }).Validate(texture.Description));
    }

    [Fact]
    public void SparseMetadataBindingRejectsBadUnmapAndAllocationOffsets()
    {
        var device = CreateDevice(GraphicsFeature.SparseResources);
        var texture = CreateTexture(GraphicsFormat.RGBA8UNorm, device);
        var requirements = new GraphicsSparseTextureMetadataRequirements(
            512, 0, 0, 256, true);

        Assert.Throws<ArgumentException>(() =>
            new GraphicsSparseTextureMetadataBinding(texture, 0, null, 1).Validate(requirements));
    }

    [Fact]
    public void SparseQueueValidationRejectsMetadataTexturesFromAnotherDevice()
    {
        var device = CreateDevice(GraphicsFeature.SparseResources);
        var otherDevice = CreateDevice(GraphicsFeature.SparseResources);
        var sparseDevice = new TestSparseDevice(device);
        var texture = CreateTexture(GraphicsFormat.RGBA8UNorm, otherDevice);
        var binding = new GraphicsSparseTextureMetadataBinding(texture, 0, null, 0);
        var requirements = new GraphicsSparseTextureMetadataRequirements(256, 0, 0, 256, true);

        Assert.Throws<ArgumentException>(() =>
            GraphicsSparseResourceValidation.ValidateTextureMetadataBindings(
                sparseDevice, new SparseQueue(device), [binding], requirements));
    }

    private static GraphicsDeviceFactoryTests.TestDevice CreateDevice(GraphicsFeature features) =>
        new(GraphicsBackend.Vulkan, features, GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute);

    private static TestTexture CreateTexture(
        GraphicsFormat format,
        IGraphicsDevice? device = null,
        uint layers = 1)
    {
        device ??= CreateDevice(GraphicsFeature.SparseResources);
        return new TestTexture(device, new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            format,
            256, 256, 1, 1, layers, 1,
            GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.SparseResidency));
    }

    private sealed class TestSparseDevice(IGraphicsDevice device) : IGraphicsSparseTextureMetadataDevice
    {
        public IGraphicsDevice Device { get; } = device;

        public GraphicsSparseTextureRequirements GetSparseTextureRequirements(
            IGraphicsTexture texture, GraphicsSparseTextureAspect aspect) =>
            new(64, 64, 1, 65536, texture.Description.MipLevels, 0, 0, 0, false, aspect);

        public GraphicsSparseBufferRequirements GetSparseBufferRequirements(IGraphicsBuffer buffer) =>
            new(65536, 65536);

        public GraphicsSparseTextureMetadataRequirements GetSparseTextureMetadataRequirements(IGraphicsTexture texture) =>
            new(4096, 0, 0, 256, true);

        public void BindSparseTextures(
            IGraphicsSparseBindingQueue queue,
            ReadOnlySpan<GraphicsSparseTextureBinding> bindings,
            IGraphicsFence? waitFence = null,
            ulong waitValue = 0,
            IGraphicsFence? signalFence = null,
            ulong signalValue = 0) { }

        public void BindSparseBuffers(
            IGraphicsSparseBindingQueue queue,
            ReadOnlySpan<GraphicsSparseBufferBinding> bindings,
            IGraphicsFence? waitFence = null,
            ulong waitValue = 0,
            IGraphicsFence? signalFence = null,
            ulong signalValue = 0) { }

        public void BindSparseTextureMetadata(
            IGraphicsSparseBindingQueue queue,
            ReadOnlySpan<GraphicsSparseTextureMetadataBinding> bindings,
            IGraphicsFence? waitFence = null,
            ulong waitValue = 0,
            IGraphicsFence? signalFence = null,
            ulong signalValue = 0) { }
    }

    private sealed class SparseQueue(IGraphicsDevice device) : IGraphicsSparseBindingQueue
    {
        public IGraphicsDevice Device { get; } = device;
        public uint Index => 0;
        public uint QueueFamilyIndex => 3;
    }

    private sealed class OrdinaryQueue(IGraphicsDevice device) : IGraphicsQueue
    {
        public IGraphicsDevice Device { get; } = device;
        public uint Index => 0;
        public uint QueueFamilyIndex => 3;
    }

    private sealed class TestTexture(
        IGraphicsDevice device,
        GraphicsTextureDescription description) : GraphicsDeviceObject(device), IGraphicsTexture
    {
        public string? DebugName => null;
        public GraphicsTextureDescription Description { get; } = description;
        public IGraphicsTextureView CreateView(in GraphicsTextureViewDescription viewDescription) =>
            throw new NotSupportedException();
        protected override void DisposeCore() { }
    }
}
