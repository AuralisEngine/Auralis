using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class GraphicsReviewRegressionTests
{

    [Fact]
    public void ContextActivationOnlyMakesCurrentAtOutermostDepth()
    {
        int activations = 0;
        int releases = 0;
        bool current = false;
        var activation = new GraphicsContextActivation(
            () => { Assert.False(current); current = true; activations++; },
            () => { Assert.True(current); current = false; releases++; });

        activation.Enter();
        Assert.True(current);
        activation.Enter();
        Assert.Equal(1, activations);
        activation.Exit();
        Assert.True(current);
        Assert.Equal(0, releases);
        activation.Exit();
        Assert.False(current);
        Assert.Equal(1, activations);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void ContextGateAcquisitionTimesOutWhenAnotherThreadHoldsTheGate()
    {
        object gate = new();
        AssertContextGateTimesOutWhileHeld(
            action => { lock (gate) action(); },
            () => GraphicsContextGate.TryEnter(
                gate, TimeSpan.FromMilliseconds(30), CancellationToken.None),
            () => Monitor.Exit(gate));

        Assert.True(GraphicsContextGate.TryEnter(gate, TimeSpan.Zero, CancellationToken.None));
        Monitor.Exit(gate);
    }

    [Fact]
    public void ContextGateLockAcquisitionTimesOutWhenAnotherThreadHoldsTheGate()
    {
        Lock gate = new();
        AssertContextGateTimesOutWhileHeld(
            action => { lock (gate) action(); },
            () => GraphicsContextGate.TryEnter(
                gate, TimeSpan.FromMilliseconds(30), CancellationToken.None),
            gate.Exit);

        Assert.True(GraphicsContextGate.TryEnter(gate, TimeSpan.Zero, CancellationToken.None));
        gate.Exit();
    }

    [Fact]
    public void ContextGateAcquisitionSucceedsWhenTheGateIsAvailableIncludingZeroTimeout()
    {
        object monitorGate = new();
        Assert.True(GraphicsContextGate.TryEnter(monitorGate, TimeSpan.Zero, CancellationToken.None));
        Monitor.Exit(monitorGate);
        Assert.True(GraphicsContextGate.TryEnter(
            monitorGate, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Monitor.Exit(monitorGate);

        Lock lockGate = new();
        Assert.True(GraphicsContextGate.TryEnter(lockGate, TimeSpan.Zero, CancellationToken.None));
        lockGate.Exit();
        Assert.True(GraphicsContextGate.TryEnter(
            lockGate, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        lockGate.Exit();
        Assert.True(GraphicsContextGate.TryEnter(lockGate, TimeSpan.MaxValue, CancellationToken.None));
        lockGate.Exit();

        Assert.True(GraphicsContextGate.TryEnter(monitorGate, TimeSpan.MaxValue, CancellationToken.None));
        Monitor.Exit(monitorGate);
    }

    [Fact]
    public void ContextGateInfiniteAcquisitionHonorsCancellationForBothGateTypes()
    {
        object monitorGate = new();
        AssertInfiniteContextGateAcquisitionCanBeCancelled(
            action => { lock (monitorGate) action(); },
            (timeout, token) => GraphicsContextGate.TryEnter(monitorGate, timeout, token),
            () => Monitor.Exit(monitorGate));

        Lock lockGate = new();
        AssertInfiniteContextGateAcquisitionCanBeCancelled(
            action => { lock (lockGate) action(); },
            (timeout, token) => GraphicsContextGate.TryEnter(lockGate, timeout, token),
            lockGate.Exit);
    }

    [Fact]
    public void ContextGateAcquisitionSurvivesRepeatedContentionWithoutLeakingOwnership()
    {
        object monitorGate = new();
        AssertRepeatedContextGateContention(
            action => { lock (monitorGate) action(); },
            timeout => GraphicsContextGate.TryEnter(monitorGate, timeout, CancellationToken.None),
            () => Monitor.Exit(monitorGate));

        Lock lockGate = new();
        AssertRepeatedContextGateContention(
            action => { lock (lockGate) action(); },
            timeout => GraphicsContextGate.TryEnter(lockGate, timeout, CancellationToken.None),
            lockGate.Exit);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(12)]
    public void IndirectZeroCountIsAValidatedNoOpForAllDrawCommandLayouts(uint commandSize)
    {
        GraphicsBufferDescription description = new(
            128,
            GraphicsBufferUsage.IndirectArguments,
            GraphicsMemoryAccess.CpuToGpu);

        Assert.False(GraphicsCommandValidation.TryValidateIndirectCommandSequence(
            description, offset: 0, drawCount: 0, commandSize, stride: 0, out uint effectiveStride));
        Assert.Equal(commandSize, effectiveStride);

        Assert.True(GraphicsCommandValidation.TryValidateIndirectCommandSequence(
            description, offset: 0, drawCount: 1, commandSize, stride: 0, out _));
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsCommandValidation.TryValidateIndirectCommandSequence(
                description, offset: 2, drawCount: 0, commandSize, stride: 0, out _));
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsCommandValidation.TryValidateIndirectCommandSequence(
                description, offset: 0, drawCount: 0, commandSize, stride: commandSize - 4, out _));
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsCommandValidation.TryValidateIndirectCommandSequence(
                description, offset: 128, drawCount: 1, commandSize, stride: 0, out _));
    }

    [Fact]
    public void GlesRetainedResourceCleanupAggregatesEveryReleaseFailure()
    {
        var first = new ThrowingGlesRetainedResource("first");
        var second = new ThrowingGlesRetainedResource("second");
        var third = new ThrowingGlesRetainedResource("third");

        AggregateException exception = Assert.Throws<AggregateException>(() =>
            OpenGLESGraphicsCommandList.ReleaseRetainedResources([first, second, third]));

        Assert.Equal(3, exception.InnerExceptions.Count);
        Assert.Equal(1, first.ReleaseAttempts);
        Assert.Equal(1, second.ReleaseAttempts);
        Assert.Equal(1, third.ReleaseAttempts);
    }

    [Fact]
    public void RenderTargetValidationChecksOwnershipExtentSamplesUsageAndFormats()
    {
        var colorCapabilities = new GraphicsFormatCapabilities(
            GraphicsFormat.RGBA8UNorm,
            GraphicsFormatSupport.ColorAttachment,
            GraphicsSampleCountFlags.One | GraphicsSampleCountFlags.Four);
        var depthCapabilities = new GraphicsFormatCapabilities(
            GraphicsFormat.D32Float,
            GraphicsFormatSupport.DepthStencilAttachment,
            GraphicsSampleCountFlags.One | GraphicsSampleCountFlags.Four);
        var device = CreateDevice(GraphicsFeature.None, colorCapabilities, depthCapabilities);

        var colorTexture = new TestTexture(device, new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            128, 64, 1, 1, 1, 1,
            GraphicsTextureUsage.ColorAttachment));
        var depthTexture = new TestTexture(device, new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.D32Float,
            128, 64, 1, 1, 1, 1,
            GraphicsTextureUsage.DepthStencilAttachment));

        var colorView = new TestTextureView(colorTexture, new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));
        var depthView = new TestTextureView(depthTexture, new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));

        Assert.Equal(
            new GraphicsRenderTargetExtent(128, 64, 1),
            GraphicsRenderTargetValidation.Validate(device, [colorView], depthView));

        var mismatchedExtent = new TestTexture(device, colorTexture.Description with { Width = 64 });
        var mismatchedView = new TestTextureView(mismatchedExtent, colorView.Description);
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsRenderTargetValidation.Validate(device, [colorView, mismatchedView]));

        var foreignDevice = CreateDevice(GraphicsFeature.None, colorCapabilities, depthCapabilities);
        var foreignTexture = new TestTexture(foreignDevice, colorTexture.Description);
        var foreignView = new TestTextureView(foreignTexture, colorView.Description);
        Assert.Throws<ArgumentException>(() =>
            GraphicsRenderTargetValidation.Validate(device, [foreignView]));

        var sampledOnly = new TestTexture(device,
            colorTexture.Description with { Usage = GraphicsTextureUsage.Sampled });
        var sampledOnlyView = new TestTextureView(sampledOnly, colorView.Description);
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsRenderTargetValidation.Validate(device, [sampledOnlyView]));
    }


    [Fact]
    public void RenderTargetValidationAcceptsDisposedParentOwnerWhileLiveViewRetainsBackingStorage()
    {
        var colorCapabilities = new GraphicsFormatCapabilities(
            GraphicsFormat.RGBA8UNorm,
            GraphicsFormatSupport.ColorAttachment,
            GraphicsSampleCountFlags.One);
        var device = CreateDevice(GraphicsFeature.None, colorCapabilities);
        var texture = new RetainedTestTexture(device, new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            128, 64, 1, 1, 1, 1,
            GraphicsTextureUsage.ColorAttachment));
        var view = new RetainedTestTextureView(texture, new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));

        Assert.Equal(
            new GraphicsRenderTargetExtent(128, 64, 1),
            GraphicsRenderTargetValidation.Validate(device, [view]));

        texture.Dispose();

        Assert.True(texture.IsDisposed);
        Assert.False(view.IsDisposed);
        Assert.Equal(0, texture.NativeReleaseCount);
        Assert.Equal(
            new GraphicsRenderTargetExtent(128, 64, 1),
            GraphicsRenderTargetValidation.Validate(device, [view]));

        view.Dispose();
        Assert.Equal(1, texture.NativeReleaseCount);
        view.Dispose();
        Assert.Equal(1, texture.NativeReleaseCount);
    }

    [Fact]
    public void RenderTargetValidationRejectsDisposedViewsDevicesAndForeignBackingTextures()
    {
        var colorCapabilities = new GraphicsFormatCapabilities(
            GraphicsFormat.RGBA8UNorm,
            GraphicsFormatSupport.ColorAttachment,
            GraphicsSampleCountFlags.One);
        var device = CreateDevice(GraphicsFeature.None, colorCapabilities);
        GraphicsTextureDescription textureDescription = CreateTextureDescription(
            GraphicsTextureUsage.ColorAttachment);
        GraphicsTextureViewDescription viewDescription = new(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1);

        var disposedViewTexture = new TestTexture(device, textureDescription);
        var disposedView = new TestTextureView(disposedViewTexture, viewDescription);
        disposedView.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            GraphicsRenderTargetValidation.Validate(device, [disposedView]));

        var foreignDevice = CreateDevice(GraphicsFeature.None, colorCapabilities);
        var foreignTexture = new TestTexture(foreignDevice, textureDescription);
        var foreignView = new TestTextureView(foreignTexture, viewDescription);
        Assert.Throws<ArgumentException>(() =>
            GraphicsRenderTargetValidation.Validate(device, [foreignView]));

        // The view itself belongs to the command-list device, but its backing texture does not.
        var foreignBackingView = new TestTextureView(device, foreignTexture, viewDescription);
        Assert.Throws<ArgumentException>(() =>
            GraphicsRenderTargetValidation.Validate(device, [foreignBackingView]));

        var disposedDevice = CreateDevice(GraphicsFeature.None, colorCapabilities);
        var deviceTexture = new TestTexture(disposedDevice, textureDescription);
        var deviceView = new TestTextureView(deviceTexture, viewDescription);
        disposedDevice.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            GraphicsRenderTargetValidation.Validate(disposedDevice, [deviceView]));
    }

    [Fact]
    public void RenderTargetValidationComparesEffectiveMipExtentsRatherThanParentExtents()
    {
        var colorCapabilities = new GraphicsFormatCapabilities(
            GraphicsFormat.RGBA8UNorm,
            GraphicsFormatSupport.ColorAttachment,
            GraphicsSampleCountFlags.One);
        var device = CreateDevice(GraphicsFeature.None, colorCapabilities);

        var mipmappedTexture = new TestTexture(device, new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            256, 128, 1, 2, 1, 1,
            GraphicsTextureUsage.ColorAttachment));
        var matchingBaseTexture = new TestTexture(device, new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            128, 64, 1, 1, 1, 1,
            GraphicsTextureUsage.ColorAttachment));

        var mipView = new TestTextureView(mipmappedTexture, new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 1, 1, 0, 1));
        var baseView = new TestTextureView(matchingBaseTexture, new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1));

        Assert.Equal(
            new GraphicsRenderTargetExtent(128, 64, 1),
            GraphicsRenderTargetValidation.Validate(device, [mipView, baseView]));

        var mismatchedBaseTexture = new TestTexture(device, matchingBaseTexture.Description with { Width = 64 });
        var mismatchedBaseView = new TestTextureView(mismatchedBaseTexture, baseView.Description);
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsRenderTargetValidation.Validate(device, [mipView, mismatchedBaseView]));
    }

    [Fact]
    public void RenderTargetAndDynamicStateDescriptionsRejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsViewport(0, 0, -1, 100).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsViewport(0, 0, 100, 100, 0.8f, 0.2f).Validate());
        Assert.Throws<ArgumentException>(() =>
            new GraphicsViewport(float.NaN, 0, 100, 100).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsScissorRect(0, 0, uint.MaxValue, 1).Validate());
        Assert.Throws<ArgumentException>(() =>
            new GraphicsClearColor(0, 0, float.PositiveInfinity, 1).Validate());

        new GraphicsViewport(0, 0, 1280, 720).Validate();
        new GraphicsScissorRect(-2, 0, 0, 32).Validate();
        new GraphicsClearColor(0, 0, 0, 1).Validate();
    }

    [Fact]
    public void VariableCountBindingMustHaveHighestBindingNumberRegardlessOfInputOrder()
    {
        GraphicsBindingLayoutEntry[] variableIsNotHighest =
        [
            new(10, GraphicsDescriptorType.UniformBuffer, 1, GraphicsShaderStages.Vertex),
            new(2, GraphicsDescriptorType.SampledTexture, 100, GraphicsShaderStages.Fragment,
                GraphicsBindingFlags.VariableCount)
        ];

        GraphicsBindingLayoutEntry[] variableIsHighestButNotLast =
        [
            new(10, GraphicsDescriptorType.SampledTexture, 100, GraphicsShaderStages.Fragment,
                GraphicsBindingFlags.VariableCount),
            new(2, GraphicsDescriptorType.UniformBuffer, 1, GraphicsShaderStages.Vertex)
        ];

        Assert.Throws<ArgumentException>(() => new GraphicsBindingLayoutDescription(variableIsNotHighest));
        var valid = new GraphicsBindingLayoutDescription(variableIsHighestButNotLast);
        Assert.Equal(10u, valid.Entries[0].Binding);
    }

    [Fact]
    public void BarrierAccessCompatibilityValidatesResourceKindUsageAndStage()
    {
        var device = CreateDevice();
        var buffer = new TestBuffer(device,
            new GraphicsBufferDescription(
                1024,
                GraphicsBufferUsage.Storage | GraphicsBufferUsage.Index |
                GraphicsBufferUsage.Vertex | GraphicsBufferUsage.CopyDestination));
        var sampledTexture = new TestTexture(device, CreateTextureDescription(
            GraphicsTextureUsage.Sampled));

        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            buffer,
            GraphicsPipelineStage.ColorAttachmentOutput,
            GraphicsPipelineStage.ColorAttachmentOutput,
            GraphicsResourceAccess.ColorAttachmentWrite,
            GraphicsResourceAccess.ColorAttachmentWrite).Validate());

        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            sampledTexture,
            GraphicsPipelineStage.FragmentShader,
            GraphicsPipelineStage.FragmentShader,
            GraphicsResourceAccess.IndexRead,
            GraphicsResourceAccess.IndexRead,
            GraphicsTextureLayout.ShaderReadOnly,
            GraphicsTextureLayout.ShaderReadOnly).Validate());

        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            buffer,
            GraphicsPipelineStage.ComputeShader,
            GraphicsPipelineStage.ComputeShader,
            GraphicsResourceAccess.TransferWrite,
            GraphicsResourceAccess.TransferWrite).Validate());

        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            sampledTexture,
            GraphicsPipelineStage.Transfer,
            GraphicsPipelineStage.Transfer,
            GraphicsResourceAccess.ShaderRead,
            GraphicsResourceAccess.ShaderRead,
            GraphicsTextureLayout.ShaderReadOnly,
            GraphicsTextureLayout.ShaderReadOnly).Validate());

        new GraphicsResourceBarrier(
            buffer,
            GraphicsPipelineStage.ComputeShader,
            GraphicsPipelineStage.VertexShader,
            GraphicsResourceAccess.ShaderWrite,
            GraphicsResourceAccess.ShaderRead).Validate();

        new GraphicsResourceBarrier(
            sampledTexture,
            GraphicsPipelineStage.FragmentShader,
            GraphicsPipelineStage.FragmentShader,
            GraphicsResourceAccess.ShaderRead,
            GraphicsResourceAccess.ShaderRead,
            GraphicsTextureLayout.ShaderReadOnly,
            GraphicsTextureLayout.ShaderReadOnly).Validate();
    }

    [Fact]
    public void BarrierValidatesTextureLayoutsAgainstDeclaredUsage()
    {
        var device = CreateDevice();
        var sampledTexture = new TestTexture(device, CreateTextureDescription(GraphicsTextureUsage.Sampled));

        var invalidColorAttachmentTransition = new GraphicsResourceBarrier(
            sampledTexture,
            GraphicsPipelineStage.FragmentShader,
            GraphicsPipelineStage.ColorAttachmentOutput,
            GraphicsResourceAccess.None,
            GraphicsResourceAccess.None,
            GraphicsTextureLayout.ShaderReadOnly,
            GraphicsTextureLayout.ColorAttachment);

        Assert.Throws<ArgumentException>(invalidColorAttachmentTransition.Validate);

        var invalidTransferTransition = new GraphicsResourceBarrier(
            sampledTexture,
            GraphicsPipelineStage.TopOfPipe,
            GraphicsPipelineStage.Transfer,
            GraphicsResourceAccess.None,
            GraphicsResourceAccess.None,
            GraphicsTextureLayout.Undefined,
            GraphicsTextureLayout.TransferDestination);

        Assert.Throws<ArgumentException>(invalidTransferTransition.Validate);

        var presentTexture = new TestTexture(device,
            CreateTextureDescription(GraphicsTextureUsage.Present));
        new GraphicsResourceBarrier(
            presentTexture,
            GraphicsPipelineStage.ColorAttachmentOutput,
            GraphicsPipelineStage.BottomOfPipe,
            GraphicsResourceAccess.None,
            GraphicsResourceAccess.None,
            GraphicsTextureLayout.Undefined,
            GraphicsTextureLayout.Present).Validate();
    }

    [Fact]
    public void HostBarrierAccessRequiresMatchingCpuMemoryAccess()
    {
        var device = CreateDevice();
        var gpuOnly = new TestBuffer(device,
            new GraphicsBufferDescription(256, GraphicsBufferUsage.Storage));
        var cpuToGpu = new TestBuffer(device,
            new GraphicsBufferDescription(
                256, GraphicsBufferUsage.Storage, GraphicsMemoryAccess.CpuToGpu));

        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            gpuOnly,
            GraphicsPipelineStage.Host,
            GraphicsPipelineStage.ComputeShader,
            GraphicsResourceAccess.HostWrite,
            GraphicsResourceAccess.ShaderRead).Validate());

        new GraphicsResourceBarrier(
            cpuToGpu,
            GraphicsPipelineStage.Host,
            GraphicsPipelineStage.ComputeShader,
            GraphicsResourceAccess.HostWrite,
            GraphicsResourceAccess.ShaderRead).Validate();
    }

    [Fact]
    public void DeviceCapabilitiesEnforceBufferAndTextureLimits()
    {
        GraphicsDeviceLimits limits = default(GraphicsDeviceLimits) with
        {
            MaxBufferSizeInBytes = 1024,
            MaxTextureDimension1D = 64,
            MaxTextureDimension2D = 128,
            MaxTextureDimension3D = 32,
            MaxTextureArrayLayers = 4
        };
        var capabilities = CreateCapabilities(
            GraphicsFeature.None,
            limits,
            new GraphicsFormatCapabilities(
                GraphicsFormat.RGBA8UNorm,
                GraphicsFormatSupport.Sampled,
                GraphicsSampleCountFlags.One));

        capabilities.ValidateBufferDescription(
            new GraphicsBufferDescription(1024, GraphicsBufferUsage.Storage));
        Assert.Throws<GraphicsException>(() => capabilities.ValidateBufferDescription(
            new GraphicsBufferDescription(1025, GraphicsBufferUsage.Storage)));

        capabilities.ValidateTextureDescription(CreateTextureDescription(
            GraphicsTextureUsage.Sampled, width: 128, height: 64));

        // Presentation compatibility is surface-specific and is not represented by a per-format feature bit.
        var presentationOnlyCapabilities = CreateCapabilities(
            GraphicsFeature.None,
            default,
            new GraphicsFormatCapabilities(
                GraphicsFormat.RGBA8UNorm,
                GraphicsFormatSupport.None,
                GraphicsSampleCountFlags.One));
        presentationOnlyCapabilities.ValidateTextureDescription(
            CreateTextureDescription(GraphicsTextureUsage.Present));
        Assert.Throws<GraphicsException>(() => capabilities.ValidateTextureDescription(
            CreateTextureDescription(GraphicsTextureUsage.Sampled, width: 129, height: 64)));

        var arrayTexture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            32, 32, 1, 1, 5, 1,
            GraphicsTextureUsage.Sampled);
        Assert.Throws<GraphicsException>(() => capabilities.ValidateTextureDescription(arrayTexture));

        var oneDimensional = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture1D,
            GraphicsFormat.RGBA8UNorm,
            65, 1, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled);
        Assert.Throws<GraphicsException>(() => capabilities.ValidateTextureDescription(oneDimensional));

        var threeDimensional = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture3D,
            GraphicsFormat.RGBA8UNorm,
            16, 16, 33, 1, 1, 1,
            GraphicsTextureUsage.Sampled);
        Assert.Throws<GraphicsException>(() => capabilities.ValidateTextureDescription(threeDimensional));
    }

    [Fact]
    public void DeviceCapabilitiesValidateTextureViewFormatSupportAndVertexInputLimits()
    {
        GraphicsDeviceLimits limits = default(GraphicsDeviceLimits) with
        {
            MaxVertexInputAttributes = 1,
            MaxVertexInputBindings = 1
        };
        var capabilities = CreateCapabilities(
            GraphicsFeature.None,
            limits,
            new GraphicsFormatCapabilities(
                GraphicsFormat.RGBA8UNorm,
                GraphicsFormatSupport.Sampled,
                GraphicsSampleCountFlags.One),
            new GraphicsFormatCapabilities(
                GraphicsFormat.RGBA8UNormSrgb,
                GraphicsFormatSupport.Sampled,
                GraphicsSampleCountFlags.One),
            new GraphicsFormatCapabilities(
                GraphicsFormat.R32Float,
                GraphicsFormatSupport.VertexBuffer,
                GraphicsSampleCountFlags.One));

        var mutableTexture = CreateTextureDescription(
            GraphicsTextureUsage.Sampled,
            creationOptions: GraphicsResourceCreationOptions.MutableFormat);
        var srgbView = new GraphicsTextureViewDescription(
            GraphicsFormat.RGBA8UNormSrgb,
            GraphicsTextureViewType.Texture2D,
            0, 1, 0, 1);
        capabilities.ValidateTextureViewDescription(mutableTexture, srgbView);

        var layout = new GraphicsVertexInputLayout(
            [new GraphicsVertexBufferLayout(0, 16)],
            [new GraphicsVertexAttribute(0, 0, GraphicsFormat.R32Float, 0)]);
        capabilities.ValidateVertexInputLayout(layout);

        var tooManyAttributes = new GraphicsVertexInputLayout(
            [new GraphicsVertexBufferLayout(0, 16)],
            [
                new GraphicsVertexAttribute(0, 0, GraphicsFormat.R32Float, 0),
                new GraphicsVertexAttribute(1, 0, GraphicsFormat.R32Float, 4)
            ]);
        Assert.Throws<GraphicsException>(() => capabilities.ValidateVertexInputLayout(tooManyAttributes));

        var unsupportedVertexFormat = new GraphicsVertexInputLayout(
            [new GraphicsVertexBufferLayout(0, 16)],
            [new GraphicsVertexAttribute(0, 0, GraphicsFormat.RGBA8UNorm, 0)]);
        Assert.Throws<GraphicsException>(() => capabilities.ValidateVertexInputLayout(unsupportedVertexFormat));

        var missingViewFormat = CreateCapabilities(
            GraphicsFeature.None,
            default,
            new GraphicsFormatCapabilities(
                GraphicsFormat.RGBA8UNorm,
                GraphicsFormatSupport.Sampled,
                GraphicsSampleCountFlags.One));
        Assert.Throws<GraphicsException>(() =>
            missingViewFormat.ValidateTextureViewDescription(mutableTexture, srgbView));
    }

    [Fact]
    public void RenderPipelineSupportsMeshTaskGeometryAndTessellationStageConfigurations()
    {
        var renderFormats = new GraphicsFormatCapabilities(
            GraphicsFormat.RGBA8UNorm,
            GraphicsFormatSupport.ColorAttachment | GraphicsFormatSupport.Blend,
            GraphicsSampleCountFlags.One);

        var meshOnlyDevice = CreateDevice(GraphicsFeature.MeshShaders, renderFormats);
        var meshOnlyPipeline = new GraphicsRenderPipelineDescription(
            new TestPipelineLayout(meshOnlyDevice),
            [new TestShader(meshOnlyDevice, GraphicsShaderStage.Mesh)],
            GraphicsPrimitiveTopology.TriangleList,
            [GraphicsFormat.RGBA8UNorm]);
        Assert.Null(meshOnlyPipeline.VertexShader);

        var meshDevice = CreateDevice(
            GraphicsFeature.MeshShaders | GraphicsFeature.TaskShaders,
            renderFormats);
        var meshLayout = new TestPipelineLayout(meshDevice);
        IGraphicsShader[] meshShaders =
        [
            new TestShader(meshDevice, GraphicsShaderStage.Mesh),
            new TestShader(meshDevice, GraphicsShaderStage.Task)
        ];
        var meshPipeline = new GraphicsRenderPipelineDescription(
            meshLayout,
            meshShaders,
            GraphicsPrimitiveTopology.TriangleList,
            [GraphicsFormat.RGBA8UNorm]);
        Assert.Null(meshPipeline.VertexShader);
        Assert.Equal(2, meshPipeline.Shaders.Length);

        var geometryDevice = CreateDevice(GraphicsFeature.GeometryShaders, renderFormats);
        var geometryPipeline = new GraphicsRenderPipelineDescription(
            new TestPipelineLayout(geometryDevice),
            [new TestShader(geometryDevice, GraphicsShaderStage.Vertex),
             new TestShader(geometryDevice, GraphicsShaderStage.Geometry)],
            GraphicsPrimitiveTopology.TriangleList,
            [GraphicsFormat.RGBA8UNorm]);
        Assert.NotNull(geometryPipeline.VertexShader);

        var tessellationDevice = CreateDevice(
            GraphicsFeature.TessellationShaders,
            [renderFormats],
            limits: default(GraphicsDeviceLimits) with { MaxTessellationPatchSize = 8 });
        var tessellationPipeline = new GraphicsRenderPipelineDescription(
            new TestPipelineLayout(tessellationDevice),
            [new TestShader(tessellationDevice, GraphicsShaderStage.Vertex),
             new TestShader(tessellationDevice, GraphicsShaderStage.TessellationControl),
             new TestShader(tessellationDevice, GraphicsShaderStage.TessellationEvaluation)],
            GraphicsPrimitiveTopology.PatchList,
            [GraphicsFormat.RGBA8UNorm],
            tessellation: new GraphicsTessellationState(4));
        Assert.Equal(4u, tessellationPipeline.Tessellation!.Value.PatchControlPoints);
    }

    [Fact]
    public void PipelineValidationRequiresShaderFeaturesAndLegalStageCombinations()
    {
        var renderFormats = new GraphicsFormatCapabilities(
            GraphicsFormat.RGBA8UNorm,
            GraphicsFormatSupport.ColorAttachment,
            GraphicsSampleCountFlags.One);

        var noOptionalFeatures = CreateDevice(GraphicsFeature.None, renderFormats);
        var layout = new TestPipelineLayout(noOptionalFeatures);

        Assert.Throws<ArgumentException>(() => new GraphicsRenderPipelineDescription(
            layout,
            [new TestShader(noOptionalFeatures, GraphicsShaderStage.Task)],
            GraphicsPrimitiveTopology.TriangleList,
            [GraphicsFormat.RGBA8UNorm]));

        Assert.Throws<ArgumentException>(() => new GraphicsRenderPipelineDescription(
            layout,
            [new TestShader(noOptionalFeatures, GraphicsShaderStage.Mesh),
             new TestShader(noOptionalFeatures, GraphicsShaderStage.Vertex)],
            GraphicsPrimitiveTopology.TriangleList,
            [GraphicsFormat.RGBA8UNorm]));

        Assert.Throws<ArgumentException>(() => new GraphicsRenderPipelineDescription(
            layout,
            [new TestShader(noOptionalFeatures, GraphicsShaderStage.Geometry)],
            GraphicsPrimitiveTopology.TriangleList,
            [GraphicsFormat.RGBA8UNorm]));

        Assert.Throws<GraphicsFeatureNotSupportedException>(() =>
            new GraphicsRenderPipelineDescription(
                layout,
                [new TestShader(noOptionalFeatures, GraphicsShaderStage.Vertex),
                 new TestShader(noOptionalFeatures, GraphicsShaderStage.Geometry)],
                GraphicsPrimitiveTopology.TriangleList,
                [GraphicsFormat.RGBA8UNorm]));

        Assert.Throws<GraphicsFeatureNotSupportedException>(() =>
            new GraphicsComputePipelineDescription(
                layout,
                new TestShader(noOptionalFeatures, GraphicsShaderStage.Compute)));

        // Mesh and task shaders are separate device features, even though task stages require a mesh stage.
        var meshOnlyDevice = CreateDevice(GraphicsFeature.MeshShaders, renderFormats);
        var meshOnlyLayout = new TestPipelineLayout(meshOnlyDevice);
        var missingTaskFeature = Assert.Throws<GraphicsFeatureNotSupportedException>(() =>
            new GraphicsRenderPipelineDescription(
                meshOnlyLayout,
                [new TestShader(meshOnlyDevice, GraphicsShaderStage.Mesh),
                 new TestShader(meshOnlyDevice, GraphicsShaderStage.Task)],
                GraphicsPrimitiveTopology.TriangleList,
                [GraphicsFormat.RGBA8UNorm]));
        Assert.Equal(GraphicsFeature.TaskShaders, missingTaskFeature.MissingFeatures);

        var taskOnlyFeatureDevice = CreateDevice(GraphicsFeature.TaskShaders, renderFormats);
        Assert.Throws<GraphicsFeatureNotSupportedException>(() =>
            new GraphicsRenderPipelineDescription(
                new TestPipelineLayout(taskOnlyFeatureDevice),
                [new TestShader(taskOnlyFeatureDevice, GraphicsShaderStage.Mesh),
                 new TestShader(taskOnlyFeatureDevice, GraphicsShaderStage.Task)],
                GraphicsPrimitiveTopology.TriangleList,
                [GraphicsFormat.RGBA8UNorm]));

        Assert.Throws<ArgumentException>(() => new GraphicsRenderPipelineDescription(
            layout,
            [new TestShader(noOptionalFeatures, GraphicsShaderStage.Vertex),
             new TestShader(noOptionalFeatures, GraphicsShaderStage.TessellationControl)],
            GraphicsPrimitiveTopology.PatchList,
            [GraphicsFormat.RGBA8UNorm],
            tessellation: new GraphicsTessellationState(3)));
    }

    [Fact]
    public void BlendingRequiresFormatBlendSupport()
    {
        var device = CreateDevice(
            GraphicsFeature.None,
            new GraphicsFormatCapabilities(
                GraphicsFormat.RGBA8UNorm,
                GraphicsFormatSupport.ColorAttachment,
                GraphicsSampleCountFlags.One));
        var blend = new GraphicsBlendState(
        [
            new GraphicsBlendAttachmentState(
                true,
                GraphicsBlendFactor.One,
                GraphicsBlendFactor.Zero,
                GraphicsBlendOperation.Add,
                GraphicsBlendFactor.One,
                GraphicsBlendFactor.Zero,
                GraphicsBlendOperation.Add)
        ]);

        Assert.Throws<GraphicsException>(() => new GraphicsRenderPipelineDescription(
            new TestPipelineLayout(device),
            [new TestShader(device, GraphicsShaderStage.Vertex)],
            GraphicsPrimitiveTopology.TriangleList,
            [GraphicsFormat.RGBA8UNorm],
            blend: blend));
    }

    [Fact]
    public void PresentationSurfaceSelectsQueueBySurfaceCompatibility()
    {
        var device = CreateDevice();
        var foreignDevice = CreateDevice();
        var target = new TestTarget();
        var surface = new TestPresentationSurface(device, target);

        var graphicsQueue = new TestQueue(device, GraphicsQueueType.Graphics);
        IGraphicsQueue presentQueue = device.GetPresentationQueue();
        var computeQueue = new TestQueue(device, GraphicsQueueType.Compute);
        IGraphicsQueue foreignQueue = foreignDevice.GetPresentationQueue();

        Assert.IsNotAssignableFrom<IGraphicsCommandQueue>(presentQueue);
        Assert.True(surface.SupportsPresentation(graphicsQueue));
        Assert.True(surface.SupportsPresentation(presentQueue));
        Assert.False(surface.SupportsPresentation(computeQueue));
        Assert.False(surface.SupportsPresentation(foreignQueue));

        var swapChain = surface.CreateSwapChain(
            presentQueue,
            new GraphicsSwapChainDescription(1280, 720, GraphicsFormat.BGRA8UNorm, 3, GraphicsPresentMode.Fifo));
        Assert.Same(presentQueue, swapChain.PresentationQueue);
        Assert.Throws<GraphicsException>(() => surface.CreateSwapChain(
            computeQueue,
            new GraphicsSwapChainDescription(1280, 720, GraphicsFormat.BGRA8UNorm, 3, GraphicsPresentMode.Fifo)));
    }

    [Fact]
    public void TextureViewsCanSelectSingleLayersAndCompatibleMutableFormats()
    {
        var arrayTexture = CreateTextureDescription(
            GraphicsTextureUsage.Sampled,
            arrayLayers: 8);
        var arrayLayerView = new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown,
            GraphicsTextureViewType.Texture2D,
            0, 1, 5, 1);
        arrayLayerView.Validate(arrayTexture);

        var cubeTexture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Cube,
            GraphicsFormat.RGBA8UNorm,
            128, 128, 1, 1, 6, 1,
            GraphicsTextureUsage.Sampled);
        var cubeFaceView = new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown,
            GraphicsTextureViewType.Texture2D,
            0, 1, 3, 1);
        cubeFaceView.Validate(cubeTexture);

        var immutable = CreateTextureDescription(GraphicsTextureUsage.Sampled);
        var srgbView = new GraphicsTextureViewDescription(
            GraphicsFormat.RGBA8UNormSrgb,
            GraphicsTextureViewType.Texture2D,
            0, 1, 0, 1);
        Assert.Throws<ArgumentException>(() => srgbView.Validate(immutable));

        var mutable = immutable with
        {
            CreationOptions = GraphicsResourceCreationOptions.MutableFormat
        };
        srgbView.Validate(mutable);

        var incompatibleView = srgbView with { Format = GraphicsFormat.BGRA8UNormSrgb };
        Assert.Throws<ArgumentException>(() => incompatibleView.Validate(mutable));
    }

    [Fact]
    public void CpuFenceSignalsCannotOvertakePendingGpuSignals()
    {
        GraphicsCommandValidation.ValidateCpuFenceSignalDoesNotOvertakeGpu(4, 5);
        GraphicsCommandValidation.ValidateCpuFenceSignalDoesNotOvertakeGpu(5, 5);
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsCommandValidation.ValidateCpuFenceSignalDoesNotOvertakeGpu(10, 5));
        GraphicsCommandValidation.ValidateCpuFenceSignalDoesNotOvertakeGpu(10, null);
    }

    [Fact]
    public void IndexedIndirectRequiresZeroIndexBufferOffset()
    {
        GraphicsCommandValidation.ValidateIndexedIndirectIndexBufferOffset(0);
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsCommandValidation.ValidateIndexedIndirectIndexBufferOffset(2));
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsCommandValidation.ValidateIndexedIndirectIndexBufferOffset(4));
    }

    [Fact]
    public void TextureCopyRejectsAliasedSourceAndDestination()
    {
        var device = CreateDevice();
        var texture = new TestTexture(device, CreateTextureDescription(
            GraphicsTextureUsage.CopySource | GraphicsTextureUsage.CopyDestination));
        var other = new TestTexture(device, texture.Description);

        GraphicsCommandValidation.ValidateTextureCopyNotAliased(texture, other);
        Assert.Throws<GraphicsValidationException>(() =>
            GraphicsCommandValidation.ValidateTextureCopyNotAliased(texture, texture));
    }

    [Fact]
    public void GlesFailedSubmissionRecoveryCompletesListsBeforeContextGateIsReleased()
    {
        var lifecycle = new GraphicsCommandListLifecycle(GraphicsQueueType.Graphics);
        lifecycle.Begin();
        lifecycle.End();
        lifecycle.MarkSubmitted();

        object contextGate = new();
        GraphicsCommandListState stateObservedByDisposer = GraphicsCommandListState.Pending;
        var disposer = new Thread(() =>
        {
            lock (contextGate)
                stateObservedByDisposer = lifecycle.State;
        });

        lock (contextGate)
        {
            GraphicsCommandListSubmissionRecovery.RecoverFailedSubmission(
                new[] { lifecycle },
                submittedCount: 1,
                commandsStarted: true,
                finishGpuWork: static () => { },
                markCompleted: static list => list.MarkCompleted());
            disposer.Start();
        }

        disposer.Join();
        Assert.Equal(GraphicsCommandListState.Executable, stateObservedByDisposer);
    }

    [Fact]
    public void GlesFailedSubmissionRecoveryLeavesListsPendingWhenFinishFails()
    {
        var lifecycle = new GraphicsCommandListLifecycle(GraphicsQueueType.Graphics);
        lifecycle.Begin();
        lifecycle.End();
        lifecycle.MarkSubmitted();

        Assert.Throws<InvalidOperationException>(() =>
            GraphicsCommandListSubmissionRecovery.RecoverFailedSubmission(
                new[] { lifecycle },
                submittedCount: 1,
                commandsStarted: true,
                finishGpuWork: static () => throw new InvalidOperationException("Injected finish failure."),
                markCompleted: static list => list.MarkCompleted()));

        Assert.Equal(GraphicsCommandListState.Pending, lifecycle.State);
    }


    [Fact]
    public void ConcurrentCompletionRecoveryIsIdempotent()
    {
        var lifecycle = new GraphicsCommandListLifecycle(GraphicsQueueType.Graphics);
        lifecycle.Begin();
        lifecycle.End();
        lifecycle.MarkSubmitted();

        Assert.True(lifecycle.TryMarkCompleted());
        Assert.False(lifecycle.TryMarkCompleted());
        Assert.Equal(GraphicsCommandListState.Executable, lifecycle.State);
    }

    private static void AssertContextGateTimesOutWhileHeld(
        Action<Action> holdGate,
        Func<bool> tryEnter,
        Action release)
    {
        TimeSpan watchdog = TimeSpan.FromSeconds(5);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim completed = new();

        bool acquired = false;
        TimeSpan acquisitionElapsed = default;
        Exception? workerFailure = null;
        Thread waiter = new(() =>
        {
            started.Set();
            long operationStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                acquired = tryEnter();
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
            finally
            {
                // Time only the operation on the worker, not Thread.Start, event waits, or Join.
                acquisitionElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(operationStart);
                if (acquired)
                {
                    try { release(); }
                    catch (Exception exception) { workerFailure ??= exception; }
                }

                completed.Set();
            }
        });

        bool waiterStarted = false;
        bool completedWhileGateWasHeld = false;
        try
        {
            holdGate(() =>
            {
                waiter.Start();
                waiterStarted = true;
                Assert.True(started.Wait(watchdog), "The lock-waiting thread did not start.");
                completedWhileGateWasHeld = completed.Wait(watchdog);
            });
        }
        finally
        {
            if (waiterStarted)
                Assert.True(waiter.Join(watchdog), "The lock-waiting thread did not terminate.");
        }

        Assert.True(
            completedWhileGateWasHeld,
            "TryEnter must finish before the owning thread releases the gate.");
        Assert.Null(workerFailure);
        Assert.False(acquired, "TryEnter unexpectedly acquired a gate still owned by another thread.");
        Assert.InRange(acquisitionElapsed.TotalMilliseconds, 20, 500);
    }

    private static void AssertInfiniteContextGateAcquisitionCanBeCancelled(
        Action<Action> holdGate,
        Func<TimeSpan, CancellationToken, bool> tryEnter,
        Action release)
    {
        TimeSpan watchdog = TimeSpan.FromSeconds(5);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim completed = new();
        using CancellationTokenSource cancellation = new();

        bool acquired = false;
        bool cancellationObserved = false;
        Exception? workerFailure = null;
        Thread waiter = new(() =>
        {
            started.Set();
            try
            {
                acquired = tryEnter(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
            finally
            {
                if (acquired)
                {
                    try { release(); }
                    catch (Exception exception) { workerFailure ??= exception; }
                }

                completed.Set();
            }
        });

        bool waiterStarted = false;
        bool completedWhileGateWasHeld = false;
        try
        {
            holdGate(() =>
            {
                waiter.Start();
                waiterStarted = true;
                Assert.True(started.Wait(watchdog), "The cancellable lock-waiting thread did not start.");
                // Let the worker enter its 1 ms polling loop before requesting cancellation.
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));
                completedWhileGateWasHeld = completed.Wait(watchdog);
            });
        }
        finally
        {
            if (waiterStarted)
                Assert.True(waiter.Join(watchdog), "The cancelled lock-waiting thread did not terminate.");
        }

        Assert.True(
            completedWhileGateWasHeld,
            "Cancellation must interrupt an infinite wait while the gate remains owned.");
        Assert.True(cancellationObserved);
        Assert.False(acquired);
        Assert.Null(workerFailure);

        // Prove cancellation did not leave the gate owned by the worker.
        Assert.True(tryEnter(TimeSpan.Zero, CancellationToken.None));
        release();
    }

    private static void AssertRepeatedContextGateContention(
        Action<Action> holdGate,
        Func<TimeSpan, bool> tryEnter,
        Action release)
    {
        const int attemptCount = 3;
        TimeSpan watchdog = TimeSpan.FromSeconds(5);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim completed = new();

        bool[] acquired = new bool[attemptCount];
        Exception? workerFailure = null;
        Thread waiter = new(() =>
        {
            started.Set();
            try
            {
                for (int i = 0; i < attemptCount; i++)
                {
                    acquired[i] = tryEnter(TimeSpan.FromMilliseconds(20));
                    if (acquired[i])
                    {
                        release();
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
            finally
            {
                completed.Set();
            }
        });

        bool waiterStarted = false;
        bool completedWhileGateWasHeld = false;
        try
        {
            holdGate(() =>
            {
                waiter.Start();
                waiterStarted = true;
                Assert.True(started.Wait(watchdog), "The contended lock-waiting thread did not start.");
                completedWhileGateWasHeld = completed.Wait(watchdog);
            });
        }
        finally
        {
            if (waiterStarted)
                Assert.True(waiter.Join(watchdog), "The repeatedly contended thread did not terminate.");
        }

        Assert.True(completedWhileGateWasHeld);
        Assert.Null(workerFailure);
        Assert.All(acquired, wasAcquired => Assert.False(wasAcquired));

        // The original owner has now released the gate; it must be immediately reusable.
        Assert.True(tryEnter(TimeSpan.Zero));
        release();
    }

    private static GraphicsDeviceFactoryTests.TestDevice CreateDevice(
        GraphicsFeature features = GraphicsFeature.None,
        params GraphicsFormatCapabilities[] formats) =>
        CreateDevice(features, formats, default);

    private static GraphicsDeviceFactoryTests.TestDevice CreateDevice(
        GraphicsFeature features,
        GraphicsFormatCapabilities[] formats,
        GraphicsDeviceLimits limits) =>
        new(
            GraphicsBackend.Vulkan,
            features,
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute,
            CreateCapabilities(features, limits, formats));

    private static GraphicsDeviceCapabilities CreateCapabilities(
        GraphicsFeature features,
        GraphicsDeviceLimits limits,
        params GraphicsFormatCapabilities[] formats) =>
        new(
            new GraphicsFeatureSet(features),
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute,
            limits,
            formats);

    private static GraphicsTextureDescription CreateTextureDescription(
        GraphicsTextureUsage usage,
        uint width = 64,
        uint height = 64,
        uint arrayLayers = 1,
        GraphicsResourceCreationOptions creationOptions = GraphicsResourceCreationOptions.None) =>
        new(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            width, height, 1, 1, arrayLayers, 1,
            usage,
            creationOptions);

    private sealed class ThrowingGlesRetainedResource(string label) : IOpenGLESNativeResource
    {
        public int ReleaseAttempts { get; private set; }
        public bool TryRetain() => true;
        public bool TryRetainDependency() => true;
        public void ReleaseReference()
        {
            ReleaseAttempts++;
            throw new InvalidOperationException($"Synthetic GLES release failure: {label}.");
        }
        public void ReleaseNative(Silk.NET.OpenGLES.GL gl) { }
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

    private sealed class TestTexture(
        IGraphicsDevice device,
        GraphicsTextureDescription description) : GraphicsDeviceObject(device), IGraphicsTexture
    {
        public string? DebugName => null;
        public GraphicsTextureDescription Description { get; } = description;
        public IGraphicsTextureView CreateView(in GraphicsTextureViewDescription viewDescription) =>
            new TestTextureView(this, viewDescription);
        protected override void DisposeCore() { }
    }

    private sealed class TestTextureView : GraphicsDeviceObject, IGraphicsTextureView
    {
        public TestTextureView(IGraphicsTexture texture, GraphicsTextureViewDescription description)
            : this(texture.Device, texture, description)
        {
        }

        public TestTextureView(
            IGraphicsDevice device,
            IGraphicsTexture texture,
            GraphicsTextureViewDescription description) : base(device)
        {
            Texture = texture;
            Description = description;
        }

        public string? DebugName => null;
        public IGraphicsTexture Texture { get; }
        public GraphicsTextureViewDescription Description { get; }
        protected override void DisposeCore() { }
    }

    /// <summary>A hardware-independent texture whose backing storage is retained by each live view.</summary>
    private sealed class RetainedTestTexture(
        IGraphicsDevice device,
        GraphicsTextureDescription description) : GraphicsDeviceObject(device), IGraphicsTexture
    {
        private readonly OpenGLESResourceReferenceState _references = new();
        private int _nativeReleaseCount;

        public string? DebugName => null;
        public GraphicsTextureDescription Description { get; } = description;
        internal int NativeReleaseCount => Volatile.Read(ref _nativeReleaseCount);

        public IGraphicsTextureView CreateView(in GraphicsTextureViewDescription viewDescription)
        {
            ThrowIfDisposed();
            GraphicsTextureViewDescription copy = viewDescription;
            copy.Validate(Description);
            return new RetainedTestTextureView(this, copy);
        }

        internal bool TryRetainView() => _references.TryRetain();

        internal void ReleaseViewReference()
        {
            if (_references.ReleaseReference())
                Interlocked.Increment(ref _nativeReleaseCount);
        }

        protected override void DisposeCore()
        {
            if (_references.CloseOwner())
                Interlocked.Increment(ref _nativeReleaseCount);
        }
    }

    private sealed class RetainedTestTextureView : GraphicsDeviceObject, IGraphicsTextureView
    {
        private readonly RetainedTestTexture _texture;

        internal RetainedTestTextureView(
            RetainedTestTexture texture,
            GraphicsTextureViewDescription description) : base(texture.Device)
        {
            if (!texture.TryRetainView())
                throw new ObjectDisposedException(nameof(RetainedTestTexture));

            _texture = texture;
            Description = description;
        }

        public string? DebugName => null;
        public IGraphicsTexture Texture => _texture;
        public GraphicsTextureViewDescription Description { get; }

        protected override void DisposeCore() => _texture.ReleaseViewReference();
    }

    private sealed class TestPipelineLayout(IGraphicsDevice device)
        : GraphicsDeviceObject(device), IGraphicsPipelineLayout
    {
        public ReadOnlyMemory<IGraphicsBindingLayout> BindingLayouts => ReadOnlyMemory<IGraphicsBindingLayout>.Empty;
        protected override void DisposeCore() { }
    }

    private sealed class TestShader(
        IGraphicsDevice device,
        GraphicsShaderStage stage) : GraphicsDeviceObject(device), IGraphicsShader
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

    private sealed class TestQueue(
        IGraphicsDevice device,
        GraphicsQueueType queueType) : IGraphicsCommandQueue
    {
        public IGraphicsDevice Device { get; } = device;
        public GraphicsQueueType QueueType { get; } = queueType;
        public uint Index => 0;
        public IGraphicsCommandList CreateCommandList(GraphicsCommandListUsage usage = GraphicsCommandListUsage.Reusable) =>
            throw new NotSupportedException();
        public void Submit(ReadOnlySpan<IGraphicsCommandList> commandLists, IGraphicsFence? signalFence = null, ulong signalValue = 0) =>
            throw new NotSupportedException();
        public void Wait(IGraphicsFence fence, ulong value) => throw new NotSupportedException();
    }

    private sealed class TestTarget : IGraphicsPresentationTarget
    {
        private readonly GraphicsPresentationTargetSnapshot _snapshot = new(
            new GraphicsNativeSurfaceDescription(
                GraphicsNativePlatform.Custom,
                [new GraphicsNativeHandle(
                    GraphicsNativeHandleKind.CreateCustom(0x8000),
                    (nint)42,
                    GraphicsNativeHandleOwnership.Borrowed)]),
            new GraphicsExtent2D(1280, 720),
            Revision: 1,
            IsSuspended: false);

        public GraphicsPresentationTargetSnapshot GetSnapshot() => _snapshot;
    }

    private sealed class TestPresentationSurface(
        IGraphicsDevice device,
        IGraphicsPresentationTarget target) : GraphicsDeviceObject(device), IGraphicsPresentationSurface
    {
        public IGraphicsPresentationTarget Target { get; } = target;
        public GraphicsPresentationTargetSnapshot Snapshot => Target.GetSnapshot();

        public bool SupportsPresentation(IGraphicsQueue queue) =>
            !IsDisposed && ReferenceEquals(queue.Device, Device) &&
            (queue is not IGraphicsCommandQueue commandQueue ||
             commandQueue.QueueType == GraphicsQueueType.Graphics);

        public IGraphicsSwapChain CreateSwapChain(
            IGraphicsQueue presentationQueue,
            in GraphicsSwapChainDescription description)
        {
            if (!SupportsPresentation(presentationQueue))
                throw new GraphicsException("The selected command queue cannot present to this surface.");
            description.Validate();
            return new TestSwapChain(Device, presentationQueue, description);
        }

        protected override void DisposeCore() { }
    }

    private sealed class TestSwapChain(
        IGraphicsDevice device,
        IGraphicsQueue presentationQueue,
        GraphicsSwapChainDescription description) : GraphicsDeviceObject(device), IGraphicsSwapChain
    {
        public GraphicsSwapChainDescription Description { get; private set; } = description;
        public IGraphicsQueue PresentationQueue { get; } = presentationQueue;
        public ulong Generation { get; private set; } = 1;
        public GraphicsAcquireResult AcquireNextImage(TimeSpan timeout) => default;
        public GraphicsPresentStatus Present(
            in GraphicsAcquiredImage image,
            IGraphicsFence? waitFence = null,
            ulong waitValue = 0) => GraphicsPresentStatus.Presented;
        public void Resize(in GraphicsSwapChainDescription description)
        {
            description.Validate();
            Description = description;
            Generation++;
        }
        protected override void DisposeCore() { }
    }
}
