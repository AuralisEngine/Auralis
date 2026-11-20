using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGL;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class OpenGLBackendTests
{
    [Fact]
    public void OpenGLBackendIsAppendedWithoutChangingExistingBackendValues()
    {
        Assert.Equal(0, (int)GraphicsBackend.Direct3D12);
        Assert.Equal(1, (int)GraphicsBackend.Vulkan);
        Assert.Equal(2, (int)GraphicsBackend.Metal);
        Assert.Equal(3, (int)GraphicsBackend.OpenGL);
    }

    [Fact]
    public void ProviderRequiresAnAvailableInjectedContext()
    {
        var context = new TestContextProvider(isAvailable: false);
        var provider = new OpenGLGraphicsBackendProvider(context);

        Assert.Equal(GraphicsBackend.OpenGL, provider.Backend);
        Assert.False(provider.IsAvailable);
        Assert.Equal("No current desktop GL context.", provider.UnavailableReason);
        Assert.Throws<GraphicsBackendUnavailableException>(
            () => provider.EnumerateAdaptersAsync());
    }

    [Fact]
    public async Task ProviderDoesNotInventSystemWideAdapterInformation()
    {
        var provider = new OpenGLGraphicsBackendProvider(new TestContextProvider(true));

        ReadOnlyMemory<GraphicsAdapterInfo> adapters = await provider.EnumerateAdaptersAsync();

        Assert.Empty(adapters.ToArray());
    }

    [Fact]
    public void ProviderCanBeRegisteredExplicitlyWithoutCreatingAContext()
    {
        var context = new TestContextProvider(isAvailable: false);
        var provider = new OpenGLGraphicsBackendProvider(context);
        var registry = new GraphicsBackendProviderRegistry();

        registry.Register(provider);

        Assert.True(registry.TryGetProvider(GraphicsBackend.OpenGL, out var registered));
        Assert.Same(provider, registered);
        Assert.Throws<InvalidOperationException>(
            () => registry.Register(new OpenGLGraphicsBackendProvider(new TestContextProvider(false))));
    }

    [Fact]
    public void ContextProviderOwnershipIsExplicit()
    {
        var borrowed = new TestContextProvider(true, OpenGLContextOwnership.Borrowed);
        var owned = new TestContextProvider(true, OpenGLContextOwnership.Owned);

        Assert.Equal(OpenGLContextOwnership.Borrowed, borrowed.Ownership);
        Assert.Equal(OpenGLContextOwnership.Owned, owned.Ownership);
        Assert.Same(borrowed.ContextIdentity, borrowed.ContextIdentity);
    }



    [Fact]
    public void TextureDescriptionsMapToTheExpectedDesktopOpenGLTargets()
    {
        static GraphicsTextureDescription Description(
            GraphicsTextureDimension dimension,
            uint width = 64,
            uint height = 64,
            uint depth = 1,
            uint layers = 1,
            uint samples = 1,
            uint mipLevels = 1) =>
            new(dimension, GraphicsFormat.RGBA8UNorm, width, height, depth,
                mipLevels, layers, samples, GraphicsTextureUsage.Sampled);

        Assert.Equal(TextureTarget.Texture1D,
            OpenGLTextureTargetMapping.ForTexture(Description(GraphicsTextureDimension.Texture1D, height: 1)));
        Assert.Equal(TextureTarget.Texture1DArray,
            OpenGLTextureTargetMapping.ForTexture(Description(GraphicsTextureDimension.Texture1D, height: 1, layers: 4)));
        Assert.Equal(TextureTarget.Texture2D,
            OpenGLTextureTargetMapping.ForTexture(Description(GraphicsTextureDimension.Texture2D)));
        Assert.Equal(TextureTarget.Texture2DArray,
            OpenGLTextureTargetMapping.ForTexture(Description(GraphicsTextureDimension.Texture2D, layers: 4)));
        Assert.Equal(TextureTarget.Texture3D,
            OpenGLTextureTargetMapping.ForTexture(Description(GraphicsTextureDimension.Texture3D, depth: 16)));
        Assert.Equal(TextureTarget.TextureCubeMap,
            OpenGLTextureTargetMapping.ForTexture(Description(GraphicsTextureDimension.Cube, layers: 6)));
        Assert.Equal(TextureTarget.TextureCubeMapArray,
            OpenGLTextureTargetMapping.ForTexture(Description(GraphicsTextureDimension.Cube, layers: 12)));
        Assert.Equal(TextureTarget.Texture2DMultisample,
            OpenGLTextureTargetMapping.ForTexture(Description(GraphicsTextureDimension.Texture2D, samples: 4)));
        Assert.Equal(TextureTarget.Texture2DMultisampleArray,
            OpenGLTextureTargetMapping.ForTexture(Description(GraphicsTextureDimension.Texture2D, layers: 4, samples: 4)));
    }

    [Fact]
    public void SamplerDescriptionsMapAllCommonStatesWithoutFallbacks()
    {
        Assert.Equal(TextureMinFilter.NearestMipmapNearest,
            OpenGLSamplerMapping.ToMinFilter(GraphicsFilter.Nearest, GraphicsFilter.Nearest));
        Assert.Equal(TextureMinFilter.LinearMipmapNearest,
            OpenGLSamplerMapping.ToMinFilter(GraphicsFilter.Linear, GraphicsFilter.Nearest));
        Assert.Equal(TextureMinFilter.NearestMipmapLinear,
            OpenGLSamplerMapping.ToMinFilter(GraphicsFilter.Nearest, GraphicsFilter.Linear));
        Assert.Equal(TextureMinFilter.LinearMipmapLinear,
            OpenGLSamplerMapping.ToMinFilter(GraphicsFilter.Linear, GraphicsFilter.Linear));
        Assert.Equal(TextureMagFilter.Linear,
            OpenGLSamplerMapping.ToMagFilter(GraphicsFilter.Linear));
        Assert.Equal(TextureWrapMode.Repeat,
            OpenGLSamplerMapping.ToAddressMode(GraphicsAddressMode.Repeat));
        Assert.Equal(TextureWrapMode.MirroredRepeat,
            OpenGLSamplerMapping.ToAddressMode(GraphicsAddressMode.MirroredRepeat));
        Assert.Equal(TextureWrapMode.ClampToEdge,
            OpenGLSamplerMapping.ToAddressMode(GraphicsAddressMode.ClampToEdge));
        Assert.Equal(TextureWrapMode.ClampToBorder,
            OpenGLSamplerMapping.ToAddressMode(GraphicsAddressMode.ClampToBorder));
        Assert.Equal((TextureWrapMode)GLEnum.MirrorClampToEdge,
            OpenGLSamplerMapping.ToAddressMode(GraphicsAddressMode.MirrorClampToEdge));
        Assert.Equal(GLEnum.Lequal,
            OpenGLSamplerMapping.ToCompareFunction(GraphicsCompareOperation.LessEqual));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OpenGLSamplerMapping.ToAddressMode((GraphicsAddressMode)int.MaxValue));
    }


    [Fact]
    public void MemoryBarrierTranslationUsesTheDestinationConsumerAndResourceType()
    {
        var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.OpenGL, GraphicsFeature.None, GraphicsQueueCapabilities.Graphics);
        var buffer = new TranslationTestBuffer(device, new GraphicsBufferDescription(
            256,
            GraphicsBufferUsage.Storage | GraphicsBufferUsage.Vertex |
            GraphicsBufferUsage.IndirectArguments | GraphicsBufferUsage.CopyDestination));

        var vertexConsumer = new GraphicsResourceBarrier(
            buffer,
            GraphicsPipelineStage.ComputeShader,
            GraphicsPipelineStage.VertexInput,
            GraphicsResourceAccess.ShaderWrite,
            GraphicsResourceAccess.VertexRead);
        var indirectConsumer = new GraphicsResourceBarrier(
            buffer,
            GraphicsPipelineStage.Transfer,
            GraphicsPipelineStage.DrawIndirect,
            GraphicsResourceAccess.TransferWrite,
            GraphicsResourceAccess.IndirectRead);
        var storageConsumer = new GraphicsResourceBarrier(
            buffer,
            GraphicsPipelineStage.ComputeShader,
            GraphicsPipelineStage.ComputeShader,
            GraphicsResourceAccess.ShaderWrite,
            GraphicsResourceAccess.ShaderRead);

        Assert.Equal(
            MemoryBarrierMask.VertexAttribArrayBarrierBit,
            OpenGLMemoryBarrierTranslator.ToMask(vertexConsumer));
        Assert.Equal(
            MemoryBarrierMask.CommandBarrierBit,
            OpenGLMemoryBarrierTranslator.ToMask(indirectConsumer));
        Assert.Equal(
            MemoryBarrierMask.ShaderStorageBarrierBit,
            OpenGLMemoryBarrierTranslator.ToMask(storageConsumer));
        Assert.Equal(
            MemoryBarrierMask.AllBarrierBits,
            OpenGLMemoryBarrierTranslator.ToMask(vertexConsumer with
            {
                DestinationAccess = GraphicsResourceAccess.MemoryRead
            }));

        buffer.Dispose();
        device.Dispose();
    }

    private sealed class TranslationTestBuffer(
        IGraphicsDevice device,
        GraphicsBufferDescription description) : GraphicsDeviceObject(device), IGraphicsBuffer
    {
        public string? DebugName => null;
        public GraphicsBufferDescription Description { get; } = description;
        public Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue) =>
            throw new NotSupportedException();
        public void Unmap() => throw new NotSupportedException();
        protected override void DisposeCore() { }
    }

    [Fact]
    public void MappedMemoryBecomesInaccessibleAfterInvalidation()
    {
        var manager = new OpenGLMappedMemoryManager([1, 2, 3, 4]);
        Memory<byte> memory = manager.Memory;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, memory.ToArray());

        manager.Invalidate();

        Assert.Throws<ObjectDisposedException>(() => _ = memory.Span[0]);
        Assert.Throws<ObjectDisposedException>(() => manager.GetBuffer());
    }

    private sealed class TestContextProvider(
        bool isAvailable,
        OpenGLContextOwnership ownership = OpenGLContextOwnership.Borrowed) : IOpenGLContextProvider
    {
        public object ContextIdentity { get; } = new object();
        public OpenGLContextOwnership Ownership { get; } = ownership;
        public bool IsAvailable { get; } = isAvailable;
        public string? UnavailableReason => IsAvailable ? null : "No current desktop GL context.";
        public bool SupportsPresentation => false;

        public void MakeCurrent() => throw new NotSupportedException();
        public void ReleaseCurrent() => throw new NotSupportedException();
        public nint GetProcAddress(string name) => 0;
        public void SwapBuffers() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
