using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class GraphicsAspectAndMultiviewTests
{
    [Theory]
    [InlineData(GraphicsFormat.D16UNorm, GraphicsTextureAspect.Depth)]
    [InlineData(GraphicsFormat.D32Float, GraphicsTextureAspect.Depth)]
    [InlineData(GraphicsFormat.S8UInt, GraphicsTextureAspect.Stencil)]
    [InlineData(GraphicsFormat.D24UNormS8UInt, GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil)]
    [InlineData(GraphicsFormat.D32FloatS8UInt, GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil)]
    [InlineData(GraphicsFormat.RGBA8UNorm, GraphicsTextureAspect.Color)]
    public void FormatAspectsAreCentralized(GraphicsFormat format, GraphicsTextureAspect expected) =>
        Assert.Equal(expected, GraphicsFormatInfo.GetAspects(format));

    [Theory]
    [InlineData(GraphicsFormat.S8UInt, true, false)]
    [InlineData(GraphicsFormat.D16UNorm, false, true)]
    [InlineData(GraphicsFormat.D32Float, false, true)]
    [InlineData(GraphicsFormat.D24UNormS8UInt, true, true)]
    [InlineData(GraphicsFormat.D32FloatS8UInt, true, true)]
    public void PipelineDepthAndStencilStatesRequireTheirActualFormatAspects(
        GraphicsFormat format, bool supportsStencil, bool supportsDepth)
    {
        GraphicsDeviceFactoryTests.TestDevice device = CreateDevice();
        GraphicsFormat[] noColorTargets = [];
        if (supportsDepth)
        {
            _ = CreatePipeline(device, noColorTargets, format,
                new GraphicsDepthStencilState { DepthTestEnabled = true });
            _ = CreatePipeline(device, noColorTargets, format,
                new GraphicsDepthStencilState { DepthWriteEnabled = true });
            _ = CreatePipeline(device, noColorTargets, format,
                new GraphicsDepthStencilState { DepthBoundsTestEnabled = true });
        }
        else
        {
            Assert.Throws<ArgumentException>(() => CreatePipeline(device, noColorTargets, format,
                new GraphicsDepthStencilState { DepthTestEnabled = true }));
            Assert.Throws<ArgumentException>(() => CreatePipeline(device, noColorTargets, format,
                new GraphicsDepthStencilState { DepthWriteEnabled = true }));
            Assert.Throws<ArgumentException>(() => CreatePipeline(device, noColorTargets, format,
                new GraphicsDepthStencilState { DepthBoundsTestEnabled = true }));
        }

        if (supportsStencil)
            _ = CreatePipeline(device, noColorTargets, format,
                new GraphicsDepthStencilState { StencilTestEnabled = true });
        else
            Assert.Throws<ArgumentException>(() => CreatePipeline(device, noColorTargets, format,
                new GraphicsDepthStencilState { StencilTestEnabled = true }));
    }

    [Fact]
    public void PipelineRejectsColorAndDepthStencilFormatAspectConfusion()
    {
        GraphicsDeviceFactoryTests.TestDevice device = CreateDevice();
        Assert.Throws<ArgumentException>(() => CreatePipeline(
            device, [GraphicsFormat.D24UNormS8UInt], GraphicsFormat.Unknown, new GraphicsDepthStencilState()));
        Assert.Throws<ArgumentException>(() => CreatePipeline(
            device, [GraphicsFormat.RGBA8UNorm], GraphicsFormat.RGBA8UNorm, new GraphicsDepthStencilState()));
    }

    [Fact]
    public void DepthStencilViewsCanSelectIndividualAspectsOfCombinedFormats()
    {
        GraphicsDeviceFactoryTests.TestDevice device = CreateDevice();
        GraphicsTextureDescription textureDescription = CreateTexture(
            GraphicsFormat.D24UNormS8UInt, GraphicsTextureUsage.DepthStencilAttachment);
        var depthView = new TestTextureView(device, new TestTexture(device, textureDescription),
            CreateView(GraphicsFormat.D24UNormS8UInt, GraphicsTextureAspect.Depth));
        var stencilView = new TestTextureView(device, new TestTexture(device, textureDescription),
            CreateView(GraphicsFormat.D24UNormS8UInt, GraphicsTextureAspect.Stencil));

        new GraphicsRenderingDescription(64, 64, [],
            new GraphicsDepthStencilAttachment(depthView,
                GraphicsAttachmentLoadOperation.Clear, GraphicsAttachmentStoreOperation.Store,
                GraphicsAttachmentLoadOperation.DontCare, GraphicsAttachmentStoreOperation.DontCare,
                new GraphicsClearDepthStencil(0.5f, 0)));

        new GraphicsRenderingDescription(64, 64, [],
            new GraphicsDepthStencilAttachment(stencilView,
                GraphicsAttachmentLoadOperation.DontCare, GraphicsAttachmentStoreOperation.DontCare,
                GraphicsAttachmentLoadOperation.Clear, GraphicsAttachmentStoreOperation.Store,
                new GraphicsClearDepthStencil(1, 0x7f)));

        Assert.Throws<ArgumentException>(() => new GraphicsTextureViewDescription(
            GraphicsFormat.D24UNormS8UInt, GraphicsTextureViewType.Texture2D,
            0, 1, 0, 1, GraphicsTextureAspect.Color).Validate(textureDescription));
    }

    [Fact]
    public void DepthStencilAttachmentOperationsMustMatchSelectedViewAspects()
    {
        GraphicsDeviceFactoryTests.TestDevice device = CreateDevice();
        GraphicsTextureDescription textureDescription = CreateTexture(
            GraphicsFormat.D16UNorm, GraphicsTextureUsage.DepthStencilAttachment);
        var view = new TestTextureView(device, new TestTexture(device, textureDescription),
            CreateView(GraphicsFormat.D16UNorm, GraphicsTextureAspect.Depth));

        Assert.Throws<ArgumentException>(() => new GraphicsRenderingDescription(64, 64, [],
            new GraphicsDepthStencilAttachment(view,
                GraphicsAttachmentLoadOperation.DontCare, GraphicsAttachmentStoreOperation.DontCare,
                GraphicsAttachmentLoadOperation.Clear, GraphicsAttachmentStoreOperation.Store,
                new GraphicsClearDepthStencil(1, 0))));
    }

    [Fact]
    public void MultiviewIsIndependentFromMultiViewportAndHonorsViewMaskLimits()
    {
        GraphicsDeviceFactoryTests.TestDevice multiViewportOnly = CreateDevice(
            GraphicsFeature.MultiViewport, maxMultiviewViews: 4);
        TestTextureView arrayView = CreateColorArrayView(multiViewportOnly, 4);

        Assert.Throws<GraphicsFeatureNotSupportedException>(() =>
            CreateRendering(arrayView, viewMask: 0b0011));
        _ = CreateRendering(arrayView, viewMask: 0);

        GraphicsDeviceFactoryTests.TestDevice multiview = CreateDevice(
            GraphicsFeature.Multiview, maxMultiviewViews: 4);
        TestTextureView multiviewArray = CreateColorArrayView(multiview, 5);
        _ = CreateRendering(multiviewArray, viewMask: 0b0101);
        Assert.Throws<GraphicsException>(() => CreateRendering(multiviewArray, viewMask: 0b1_0000));
        Assert.Throws<ArgumentException>(() => new GraphicsRenderingDescription(
            64, 64, [new GraphicsColorAttachment(multiviewArray,
                GraphicsAttachmentLoadOperation.DontCare, GraphicsAttachmentStoreOperation.Store, default)],
            layerCount: 2, viewMask: 0b0011));
    }

    [Fact]
    public void EnablingMultiviewRequiresAReportedMeaningfulViewLimit()
    {
        var formats = new[] { new GraphicsFormatCapabilities(
            GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.ColorAttachment,
            GraphicsSampleCountFlags.One) };
        Assert.Throws<ArgumentException>(() => new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.Multiview),
            GraphicsQueueCapabilities.Graphics,
            default,
            formats));
        _ = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.Multiview),
            GraphicsQueueCapabilities.Graphics,
            default(GraphicsDeviceLimits) with { MaxMultiviewViewCount = 2 },
            formats);
    }

    private static GraphicsRenderPipelineDescription CreatePipeline(
        IGraphicsDevice device, GraphicsFormat[] colors, GraphicsFormat depth,
        GraphicsDepthStencilState depthStencil) =>
        new(new TestPipelineLayout(device),
            [new TestShader(device, GraphicsShaderStage.Vertex)],
            GraphicsPrimitiveTopology.TriangleList, colors, depth, depthStencil: depthStencil);

    private static GraphicsRenderingDescription CreateRendering(TestTextureView view, uint viewMask) =>
        new(64, 64,
            [new GraphicsColorAttachment(view,
                GraphicsAttachmentLoadOperation.DontCare, GraphicsAttachmentStoreOperation.Store, default)],
            layerCount: 1, viewMask: viewMask);

    private static TestTextureView CreateColorArrayView(IGraphicsDevice device, uint layers)
    {
        GraphicsTextureDescription texture = CreateTexture(
            GraphicsFormat.RGBA8UNorm, GraphicsTextureUsage.ColorAttachment, layers);
        return new TestTextureView(device, new TestTexture(device, texture),
            new GraphicsTextureViewDescription(GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2DArray,
                0, 1, 0, layers));
    }

    private static GraphicsTextureDescription CreateTexture(
        GraphicsFormat format, GraphicsTextureUsage usage, uint layers = 1) =>
        new(GraphicsTextureDimension.Texture2D, format, 64, 64, 1, 1, layers, 1, usage);

    private static GraphicsTextureViewDescription CreateView(
        GraphicsFormat format, GraphicsTextureAspect aspects) =>
        new(format, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1, aspects);

    private static GraphicsDeviceFactoryTests.TestDevice CreateDevice(
        GraphicsFeature features = GraphicsFeature.None, uint maxMultiviewViews = 0)
    {
        GraphicsFormatCapabilities[] formats =
        [
            new(GraphicsFormat.RGBA8UNorm,
                GraphicsFormatSupport.ColorAttachment | GraphicsFormatSupport.Blend,
                GraphicsSampleCountFlags.One),
            new(GraphicsFormat.D16UNorm, GraphicsFormatSupport.DepthStencilAttachment, GraphicsSampleCountFlags.One),
            new(GraphicsFormat.D32Float, GraphicsFormatSupport.DepthStencilAttachment, GraphicsSampleCountFlags.One),
            new(GraphicsFormat.S8UInt, GraphicsFormatSupport.DepthStencilAttachment, GraphicsSampleCountFlags.One),
            new(GraphicsFormat.D24UNormS8UInt, GraphicsFormatSupport.DepthStencilAttachment, GraphicsSampleCountFlags.One),
            new(GraphicsFormat.D32FloatS8UInt, GraphicsFormatSupport.DepthStencilAttachment, GraphicsSampleCountFlags.One)
        ];
        GraphicsDeviceLimits limits = default(GraphicsDeviceLimits) with
        {
            MaxMultiviewViewCount = maxMultiviewViews
        };
        GraphicsFeature effectiveFeatures = features | GraphicsFeature.DepthBoundsTest;
        var capabilities = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(effectiveFeatures), GraphicsQueueCapabilities.Graphics,
            limits, formats);
        return new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.Vulkan, effectiveFeatures, GraphicsQueueCapabilities.Graphics, capabilities);
    }

    private sealed class TestPipelineLayout(IGraphicsDevice device)
        : GraphicsDeviceObject(device), IGraphicsPipelineLayout
    {
        public ReadOnlyMemory<IGraphicsBindingLayout> BindingLayouts => ReadOnlyMemory<IGraphicsBindingLayout>.Empty;
        protected override void DisposeCore() { }
    }

    private sealed class TestShader(IGraphicsDevice device, GraphicsShaderStage stage)
        : GraphicsDeviceObject(device), IGraphicsShader
    {
        public string? DebugName => null;
        public GraphicsShaderDescription Description { get; } = new()
        {
            Stage = stage,
            Format = GraphicsShaderFormat.SpirV,
            Code = new byte[] { 1, 2, 3 }
        };
        protected override void DisposeCore() { }
    }

    private sealed class TestTexture(IGraphicsDevice device, GraphicsTextureDescription description)
        : GraphicsDeviceObject(device), IGraphicsTexture
    {
        public string? DebugName => null;
        public GraphicsTextureDescription Description { get; } = description;
        public IGraphicsTextureView CreateView(in GraphicsTextureViewDescription description) =>
            throw new NotSupportedException();
        protected override void DisposeCore() { }
    }

    private sealed class TestTextureView(
        IGraphicsDevice device, IGraphicsTexture texture, GraphicsTextureViewDescription description)
        : GraphicsDeviceObject(device), IGraphicsTextureView
    {
        public string? DebugName => null;
        public IGraphicsTexture Texture { get; } = texture;
        public GraphicsTextureViewDescription Description { get; } = description;
        protected override void DisposeCore() { }
    }
}
