using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class OpenGLESMemoryBarrierTranslatorTests
{
    [Fact]
    public void BufferDestinationAccessMapsToGlesCoreBits()
    {
        using var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.OpenGLES, GraphicsFeature.ComputeShaders,
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute);
        using var buffer = new TestBuffer(device, GraphicsBufferUsage.Storage |
            GraphicsBufferUsage.Vertex | GraphicsBufferUsage.Index |
            GraphicsBufferUsage.IndirectArguments | GraphicsBufferUsage.Constant |
            GraphicsBufferUsage.CopyDestination);

        Assert.Equal(0x40u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(buffer, GraphicsResourceAccess.IndirectRead)));
        Assert.Equal(0x2u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(buffer, GraphicsResourceAccess.IndexRead)));
        Assert.Equal(0x1u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(buffer, GraphicsResourceAccess.VertexRead)));
        Assert.Equal(0x4u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(buffer, GraphicsResourceAccess.UniformRead)));
        Assert.Equal(0x2000u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(buffer, GraphicsResourceAccess.ShaderRead)));
        Assert.Equal(0x2000u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(buffer, GraphicsResourceAccess.ShaderWrite)));
        Assert.Equal(0x200u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(
            buffer, GraphicsResourceAccess.TransferRead | GraphicsResourceAccess.TransferWrite)));
        Assert.Equal(0u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(
            buffer, GraphicsResourceAccess.ColorAttachmentWrite | GraphicsResourceAccess.DepthStencilWrite)));
    }

    [Fact]
    public void TextureDestinationAccessRespectsSampledStorageAndAttachmentUsage()
    {
        using var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.OpenGLES, GraphicsFeature.ComputeShaders,
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute);
        using var sampled = new TestTexture(device, GraphicsTextureUsage.Sampled);
        using var storage = new TestTexture(device, GraphicsTextureUsage.Storage);
        using var sampledStorage = new TestTexture(device, GraphicsTextureUsage.Sampled | GraphicsTextureUsage.Storage);
        using var color = new TestTexture(device, GraphicsTextureUsage.ColorAttachment);
        using var depth = new TestTexture(device, GraphicsTextureUsage.DepthStencilAttachment);

        Assert.Equal(0x8u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(sampled, GraphicsResourceAccess.ShaderRead)));
        Assert.Equal(0x20u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(storage, GraphicsResourceAccess.ShaderRead)));
        Assert.Equal(0x20u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(storage, GraphicsResourceAccess.ShaderWrite)));
        Assert.Equal(0x28u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(sampledStorage, GraphicsResourceAccess.ShaderRead)));
        Assert.Equal(0x400u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(
            color, GraphicsResourceAccess.ColorAttachmentRead | GraphicsResourceAccess.ColorAttachmentWrite)));
        Assert.Equal(0x400u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(
            depth, GraphicsResourceAccess.DepthStencilRead | GraphicsResourceAccess.DepthStencilWrite)));
        Assert.Equal(0x100u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(sampled, GraphicsResourceAccess.TransferRead)));
        Assert.Equal(0u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(
            sampled, GraphicsResourceAccess.IndirectRead | GraphicsResourceAccess.IndexRead |
                GraphicsResourceAccess.VertexRead | GraphicsResourceAccess.UniformRead)));
    }

    [Fact]
    public void MultipleBarriersCombineAndGenericMemoryAccessRemainsConservative()
    {
        using var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.OpenGLES, GraphicsFeature.ComputeShaders,
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute);
        using var buffer = new TestBuffer(device, GraphicsBufferUsage.Storage | GraphicsBufferUsage.IndirectArguments);
        GraphicsResourceBarrier[] barriers =
        [
            Barrier(buffer, GraphicsResourceAccess.IndirectRead),
            Barrier(buffer, GraphicsResourceAccess.ShaderRead)
        ];

        Assert.Equal(0x2040u, OpenGLESMemoryBarrierTranslator.ToMask(barriers));
        Assert.Equal(uint.MaxValue, OpenGLESMemoryBarrierTranslator.ToMask(
            Barrier(buffer, GraphicsResourceAccess.MemoryRead)));
        Assert.Equal(uint.MaxValue, OpenGLESMemoryBarrierTranslator.ToMask(
            Barrier(buffer, GraphicsResourceAccess.MemoryWrite | GraphicsResourceAccess.IndirectRead)));
    }

    [Fact]
    public void EmptySpanAndDestinationAccessNoneProduceNoMask()
    {
        Assert.Equal(0u, OpenGLESMemoryBarrierTranslator.ToMask(ReadOnlySpan<GraphicsResourceBarrier>.Empty));

        using var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.OpenGLES, GraphicsFeature.None, GraphicsQueueCapabilities.Graphics);
        using var buffer = new TestBuffer(device, GraphicsBufferUsage.Storage);
        Assert.Equal(0u, OpenGLESMemoryBarrierTranslator.ToMask(Barrier(buffer, GraphicsResourceAccess.None)));
    }

    private static GraphicsResourceBarrier Barrier(IGraphicsResource resource, GraphicsResourceAccess access) =>
        new(resource, GraphicsPipelineStage.ComputeShader, GraphicsPipelineStage.FragmentShader,
            GraphicsResourceAccess.ShaderWrite, access);

    private sealed class TestBuffer(IGraphicsDevice device, GraphicsBufferUsage usage)
        : GraphicsDeviceObject(device), IGraphicsBuffer
    {
        public string? DebugName => null;
        public GraphicsBufferDescription Description { get; } =
            new(256, usage, GraphicsMemoryAccess.GpuOnly);
        public Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue) =>
            throw new NotSupportedException();
        public void Unmap() => throw new NotSupportedException();
        protected override void DisposeCore() { }
    }

    private sealed class TestTexture(IGraphicsDevice device, GraphicsTextureUsage usage)
        : GraphicsDeviceObject(device), IGraphicsTexture
    {
        public string? DebugName => null;
        public GraphicsTextureDescription Description { get; } =
            new(GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
                64, 64, 1, 1, 1, 1, usage);
        public IGraphicsTextureView CreateView(in GraphicsTextureViewDescription description) =>
            throw new NotSupportedException();
        protected override void DisposeCore() { }
    }
}
