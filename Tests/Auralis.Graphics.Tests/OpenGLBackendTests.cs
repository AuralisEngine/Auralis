using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Presentation;
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
        Assert.Equal(OpenGLContextStateOwnership.Exclusive, borrowed.StateOwnership);
        Assert.Same(borrowed.ContextIdentity, borrowed.ContextIdentity);
    }





    [Fact]
    public void OpenGLDeviceOptionsRejectUnverifiableAdapterAndUnsupportedDebugLayers()
    {
        Assert.Throws<GraphicsBackendUnavailableException>(() =>
            OpenGLDeviceOptionsValidation.Validate(new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.OpenGL,
                Adapter = new GraphicsAdapterId(1, 2)
            }));

        Assert.Throws<GraphicsBackendUnavailableException>(() =>
            OpenGLDeviceOptionsValidation.Validate(new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.OpenGL,
                DebugOptions = GraphicsDebugOptions.EnableDebugLayer
            }));

        Assert.Throws<GraphicsBackendUnavailableException>(() =>
            OpenGLDeviceOptionsValidation.Validate(new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.OpenGL,
                DebugOptions = GraphicsDebugOptions.EnableGpuBasedValidation
            }));

        OpenGLDeviceOptionsValidation.Validate(new GraphicsDeviceOptions
        {
            Backend = GraphicsBackend.OpenGL,
            DebugOptions = GraphicsDebugOptions.EnableValidation
        });
    }

    [Fact]
    public void OpenGLPresentationRequiresExactDrawableCompatibilityAndSupportedPresentMode()
    {
        GraphicsNativeHandleKind windowKind = GraphicsNativeHandleKind.CreateCustom(0x8000);
        GraphicsNativeSurfaceDescription native = new(
            GraphicsNativePlatform.Custom,
            [new GraphicsNativeHandle(windowKind, (nint)123, GraphicsNativeHandleOwnership.Borrowed)]);
        var snapshot = new GraphicsPresentationTargetSnapshot(
            native, new GraphicsExtent2D(1280, 720), Revision: 1, IsSuspended: false);
        var compatibleProvider = new TestContextProvider(
            true,
            supportsPresentation: true,
            targetCompatible: true,
            supportedPresentMode: GraphicsPresentMode.Fifo);

        OpenGLPresentationTargetValidation.ValidateTarget(compatibleProvider, snapshot);
        OpenGLPresentationTargetValidation.ValidatePresentMode(compatibleProvider, GraphicsPresentMode.Fifo);

        var incompatibleProvider = new TestContextProvider(
            true,
            supportsPresentation: true,
            targetCompatible: false);
        Assert.Throws<GraphicsBackendUnavailableException>(() =>
            OpenGLPresentationTargetValidation.ValidateTarget(incompatibleProvider, snapshot));

        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLPresentationTargetValidation.ValidatePresentMode(
                compatibleProvider, GraphicsPresentMode.Immediate));

        var suspended = snapshot with
        {
            Extent = new GraphicsExtent2D(0, 0),
            Revision = 2,
            IsSuspended = true
        };
        OpenGLPresentationTargetValidation.ValidateTarget(compatibleProvider, suspended);
    }

    [Fact]
    public void DescriptorBindingSlotsAreStableAcrossSetsAndInputOrder()
    {
        var firstSet = new GraphicsBindingLayoutDescription(
        [
            new(7, GraphicsDescriptorType.SampledTexture, 2, GraphicsShaderStages.Fragment),
            new(3, GraphicsDescriptorType.UniformBuffer, 1, GraphicsShaderStages.Vertex),
            new(1, GraphicsDescriptorType.StorageBuffer, 2, GraphicsShaderStages.Compute),
            new(5, GraphicsDescriptorType.StorageTexture, 1, GraphicsShaderStages.Compute)
        ]);
        var secondSet = new GraphicsBindingLayoutDescription(
        [
            new(8, GraphicsDescriptorType.UniformBuffer, 2, GraphicsShaderStages.Vertex),
            new(1, GraphicsDescriptorType.CombinedTextureSampler, 1, GraphicsShaderStages.Fragment),
            new(0, GraphicsDescriptorType.StorageBuffer, 1, GraphicsShaderStages.Compute)
        ]);

        OpenGLDescriptorMapping[] mappings = OpenGLDescriptorBindingAllocator.Allocate(
            [firstSet, secondSet],
            maxUniformBufferBindings: 4,
            maxStorageBufferBindings: 4,
            maxTextureUnits: 4,
            maxImageUnits: 2);

        static uint Slot(
            ReadOnlySpan<OpenGLDescriptorMapping> values,
            uint setIndex,
            uint binding)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].SetIndex == setIndex && values[i].Description.Binding == binding)
                    return values[i].BaseSlot;
            }

            throw new Xunit.Sdk.XunitException($"Missing descriptor mapping for set {setIndex}, binding {binding}.");
        }

        Assert.Equal(0u, Slot(mappings, 0, 3)); // UBO namespace
        Assert.Equal(0u, Slot(mappings, 0, 1)); // SSBO namespace
        Assert.Equal(2u, Slot(mappings, 1, 0)); // SSBO namespace continues across sets
        Assert.Equal(0u, Slot(mappings, 0, 7)); // Texture-unit namespace
        Assert.Equal(2u, Slot(mappings, 1, 1)); // Combined texture/sampler follows sampled texture array
        Assert.Equal(0u, Slot(mappings, 0, 5)); // Image-unit namespace
        Assert.Equal(1u, Slot(mappings, 1, 8)); // UBO namespace continues across sets
    }

    [Fact]
    public void DescriptorBindingSlotAllocatorRejectsBindingPointOverflow()
    {
        var layout = new GraphicsBindingLayoutDescription(
        [
            new(0, GraphicsDescriptorType.UniformBuffer, 2, GraphicsShaderStages.Vertex)
        ]);

        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLDescriptorBindingAllocator.Allocate(
                [layout],
                maxUniformBufferBindings: 1,
                maxStorageBufferBindings: 1,
                maxTextureUnits: 1,
                maxImageUnits: 1));
    }

    [Fact]
    public void RenderPipelineEnumsMapToSilkOpenGLEnums()
    {
        Assert.Equal(PrimitiveType.Triangles,
            OpenGLPipelineMapping.ToPrimitiveType(GraphicsPrimitiveTopology.TriangleList));
        Assert.Equal(PrimitiveType.Patches,
            OpenGLPipelineMapping.ToPrimitiveType(GraphicsPrimitiveTopology.PatchList));
        Assert.Equal(DepthFunction.Lequal,
            OpenGLPipelineMapping.ToDepthFunction(GraphicsCompareOperation.LessEqual));
        Assert.Equal(StencilOp.IncrWrap,
            OpenGLPipelineMapping.ToStencilOperation(GraphicsStencilOperation.IncrementWrap));
        Assert.Equal(BlendingFactor.OneMinusSrcAlpha,
            OpenGLPipelineMapping.ToBlendFactor(GraphicsBlendFactor.OneMinusSourceAlpha));
        Assert.Equal(BlendEquationModeEXT.FuncReverseSubtract,
            OpenGLPipelineMapping.ToBlendEquation(GraphicsBlendOperation.ReverseSubtract));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OpenGLPipelineMapping.ToPrimitiveType((GraphicsPrimitiveTopology)int.MaxValue));
    }

    [Fact]
    public void VertexFormatMappingDistinguishesNormalizedAndIntegerInputs()
    {
        Assert.True(OpenGLPipelineMapping.TryGetVertexFormat(GraphicsFormat.RGBA8UNorm, out OpenGLVertexFormat normalized));
        Assert.Equal(4, normalized.ComponentCount);
        Assert.False(normalized.IsInteger);
        Assert.True(normalized.Normalized);

        Assert.True(OpenGLPipelineMapping.TryGetVertexFormat(GraphicsFormat.R32UInt, out OpenGLVertexFormat integer));
        Assert.Equal(1, integer.ComponentCount);
        Assert.True(integer.IsInteger);
        Assert.False(integer.Normalized);

        Assert.False(OpenGLPipelineMapping.TryGetVertexFormat(GraphicsFormat.BGRA8UNorm, out _));
        Assert.False(OpenGLPipelineMapping.TryGetVertexFormat(GraphicsFormat.D32Float, out _));
    }


    [Fact]
    public void ComputeDispatchValidationHonorsIndexedGroupCountLimits()
    {
        OpenGLComputeDispatchValidation.Validate(8, 4, 2, 65535, 65535, 65535);
        OpenGLComputeDispatchValidation.Validate(1, 1, 1, 0, 0, 0);

        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLComputeDispatchValidation.Validate(0, 1, 1, 65535, 65535, 65535));
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLComputeDispatchValidation.Validate(1, 65536, 1, 65535, 65535, 65535));
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLComputeDispatchValidation.Validate(1, 1, 8, 65535, 65535, 7));
    }


    [Fact]
    public void BufferStorageFlagsReflectCpuWriteAccessWithoutClaimingNativeHeapPlacement()
    {
        GraphicsBufferDescription gpuOnly = new(
            256, GraphicsBufferUsage.Storage,
            GraphicsMemoryAccess.GpuOnly,
            GraphicsMemoryPreference.DeviceLocal);
        GraphicsBufferDescription cpuToGpu = gpuOnly with
        {
            MemoryAccess = GraphicsMemoryAccess.CpuToGpu,
            MemoryPreference = GraphicsMemoryPreference.HostVisible
        };
        GraphicsBufferDescription gpuToCpu = gpuOnly with
        {
            MemoryAccess = GraphicsMemoryAccess.GpuToCpu,
            MemoryPreference = GraphicsMemoryPreference.HostCached
        };
        GraphicsBufferDescription cpuReadWrite = gpuOnly with
        {
            MemoryAccess = GraphicsMemoryAccess.CpuReadWrite,
            MemoryPreference = GraphicsMemoryPreference.HostVisible
        };

        Assert.Equal((BufferStorageMask)0, OpenGLBufferStorageMapping.GetStorageFlags(gpuOnly));
        Assert.Equal(BufferStorageMask.DynamicStorageBit, OpenGLBufferStorageMapping.GetStorageFlags(cpuToGpu));
        Assert.Equal((BufferStorageMask)0, OpenGLBufferStorageMapping.GetStorageFlags(gpuToCpu));
        Assert.Equal(BufferStorageMask.DynamicStorageBit, OpenGLBufferStorageMapping.GetStorageFlags(cpuReadWrite));
    }


    [Fact]
    public void VertexFormatByteSizesSupportOnlyMappedOpenGLAttributeFormats()
    {
        Assert.Equal(1u, OpenGLPipelineMapping.GetVertexFormatByteSize(GraphicsFormat.R8UNorm));
        Assert.Equal(2u, OpenGLPipelineMapping.GetVertexFormatByteSize(GraphicsFormat.RG8UNorm));
        Assert.Equal(4u, OpenGLPipelineMapping.GetVertexFormatByteSize(GraphicsFormat.RGBA8UNorm));
        Assert.Equal(8u, OpenGLPipelineMapping.GetVertexFormatByteSize(GraphicsFormat.RGBA16Float));
        Assert.Equal(12u, OpenGLPipelineMapping.GetVertexFormatByteSize(GraphicsFormat.RGB32Float));
        Assert.Equal(16u, OpenGLPipelineMapping.GetVertexFormatByteSize(GraphicsFormat.RGBA32Float));
        Assert.Equal(0u, OpenGLPipelineMapping.GetVertexFormatByteSize(GraphicsFormat.BGRA8UNorm));
        Assert.Equal(0u, OpenGLPipelineMapping.GetVertexFormatByteSize(GraphicsFormat.D32Float));
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
        OpenGLContextOwnership ownership = OpenGLContextOwnership.Borrowed,
        bool supportsPresentation = false,
        bool targetCompatible = false,
        GraphicsPresentMode supportedPresentMode = GraphicsPresentMode.Fifo) : IOpenGLContextProvider
    {
        public object ContextIdentity { get; } = new object();
        public OpenGLContextOwnership Ownership { get; } = ownership;
        public OpenGLContextStateOwnership StateOwnership { get; } = OpenGLContextStateOwnership.Exclusive;
        public bool IsAvailable { get; } = isAvailable;
        public string? UnavailableReason => IsAvailable ? null : "No current desktop GL context.";
        public bool SupportsPresentation { get; } = supportsPresentation;
        public bool IsPresentationTargetCompatible(in GraphicsPresentationTargetSnapshot target) =>
            SupportsPresentation && targetCompatible;
        public bool SupportsPresentMode(GraphicsPresentMode presentMode) =>
            SupportsPresentation && presentMode == supportedPresentMode;
        public void SetPresentMode(GraphicsPresentMode presentMode)
        {
            if (!SupportsPresentMode(presentMode))
                throw new NotSupportedException();
        }

        public void MakeCurrent() => throw new NotSupportedException();
        public void ReleaseCurrent() => throw new NotSupportedException();
        public nint GetProcAddress(string name) => 0;
        public void SwapBuffers()
        {
            if (!SupportsPresentation)
                throw new NotSupportedException();
        }
        public void Dispose() { }
    }
}
