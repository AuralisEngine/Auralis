using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
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
            64, 64, 1, 65536, 1, 0, 0, 0, false, GraphicsTextureAspect.Depth);
        var stencilRequirements = depthRequirements with { Aspect = GraphicsTextureAspect.Stencil };

        depthRequirements.Validate(texture.Description);
        stencilRequirements.Validate(texture.Description);

        var region = new GraphicsSparseTextureRegion(
            0, 0, 0, 0, 0, 1, 1, 1, GraphicsTextureAspect.Depth);
        region.Validate(texture.Description, depthRequirements);

        Assert.Throws<ArgumentException>(() =>
            region.Validate(texture.Description, stencilRequirements));
        Assert.Throws<ArgumentException>(() =>
            (depthRequirements with { Aspect = GraphicsTextureAspect.Color }).Validate(texture.Description));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (depthRequirements with { Aspect = GraphicsTextureAspect.None }).Validate(texture.Description));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (depthRequirements with
            {
                Aspect = GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil
            }).Validate(texture.Description));
    }

    [Fact]
    public void SparseBindingValidationAcceptsValidTextureAndBufferUnmaps()
    {
        var device = CreateDevice(GraphicsFeature.SparseResources);
        var sparseDevice = new TestSparseDevice(device);
        var queue = new SparseQueue(device);
        var texture = CreateTexture(GraphicsFormat.RGBA8UNorm, device);
        var textureBinding = new GraphicsSparseTextureBinding(
            texture, new GraphicsSparseTextureRegion(0, 0, 0, 0, 0, 1, 1, 1), null, 0);
        GraphicsSparseResourceValidation.ValidateTextureBindings(sparseDevice, queue, [textureBinding]);

        var buffer = new TestBuffer(device, new GraphicsBufferDescription(
            131072, GraphicsBufferUsage.Storage,
            CreationOptions: GraphicsResourceCreationOptions.SparseResidency));
        var bufferBinding = new GraphicsSparseBufferBinding(
            buffer, new GraphicsSparseBufferRegion(0, 65536), null, 0);
        GraphicsSparseResourceValidation.ValidateBufferBindings(sparseDevice, queue, [bufferBinding]);
    }

    [Fact]
    public void SparseBindingValidationRejectsResourcesFromAnotherDevice()
    {
        var device = CreateDevice(GraphicsFeature.SparseResources);
        var otherDevice = CreateDevice(GraphicsFeature.SparseResources);
        var sparseDevice = new TestSparseDevice(device);
        var queue = new SparseQueue(device);
        var foreignTexture = CreateTexture(GraphicsFormat.RGBA8UNorm, otherDevice);
        var foreignTextureBinding = new GraphicsSparseTextureBinding(
            foreignTexture, new GraphicsSparseTextureRegion(0, 0, 0, 0, 0, 1, 1, 1), null, 0);
        Assert.Throws<ArgumentException>(() =>
            GraphicsSparseResourceValidation.ValidateTextureBindings(sparseDevice, queue, [foreignTextureBinding]));

        var foreignBuffer = new TestBuffer(otherDevice, new GraphicsBufferDescription(
            65536, GraphicsBufferUsage.Storage,
            CreationOptions: GraphicsResourceCreationOptions.SparseResidency));
        var foreignBufferBinding = new GraphicsSparseBufferBinding(
            foreignBuffer, new GraphicsSparseBufferRegion(0, 65536), null, 0);
        Assert.Throws<ArgumentException>(() =>
            GraphicsSparseResourceValidation.ValidateBufferBindings(sparseDevice, queue, [foreignBufferBinding]));
    }

    [Fact]
    public void SparseTextureRequirementsTranslateMipTailOverflowIntoRangeValidationError()
    {
        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            256, 256, 1, 9, 4, 1, GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.SparseResidency);
        var requirements = new GraphicsSparseTextureRequirements(
            64, 64, 1, 65536, 1, 16, ulong.MaxValue - 8, 16, false);

        Assert.Throws<ArgumentOutOfRangeException>(() => requirements.Validate(texture));
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
            IGraphicsTexture texture, GraphicsTextureAspect aspect) =>
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

    private sealed class TestBuffer(
        IGraphicsDevice device,
        GraphicsBufferDescription description) : GraphicsDeviceObject(device), IGraphicsBuffer
    {
        public string? DebugName => null;
        public GraphicsBufferDescription Description { get; } = description;
        public Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue) =>
            throw new NotSupportedException();
        public void Unmap() { }
        protected override void DisposeCore() { }
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
