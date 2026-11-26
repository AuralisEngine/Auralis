using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class GraphicsDescriptorIndexingTests
{
    [Fact]
    public void StructuralValidationRejectsVariableCountOnDynamicBufferDescriptors()
    {
        Assert.Throws<ArgumentException>(() => new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                0, GraphicsDescriptorType.DynamicUniformBuffer, 8,
                GraphicsShaderStages.Vertex, GraphicsBindingFlags.VariableCount)
        ]));

        Assert.Throws<ArgumentException>(() => new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                0, GraphicsDescriptorType.DynamicStorageBuffer, 8,
                GraphicsShaderStages.Compute, GraphicsBindingFlags.UpdateAfterBind)
        ]));
    }

    [Fact]
    public void UpdateAfterBindCannotShareLayoutWithDynamicBufferDescriptors()
    {
        Assert.Throws<ArgumentException>(() => new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                0, GraphicsDescriptorType.SampledTexture, 16,
                GraphicsShaderStages.Fragment, GraphicsBindingFlags.UpdateAfterBind),
            new GraphicsBindingLayoutEntry(
                1, GraphicsDescriptorType.DynamicUniformBuffer, 1,
                GraphicsShaderStages.Vertex)
        ]));
    }

    [Fact]
    public void DeviceValidationUsesPerDescriptorTypeUpdateAfterBindCapabilities()
    {
        var capabilities = CreateCapabilities(
            new GraphicsDescriptorIndexingCapabilities(
                PartiallyBound: false,
                UpdateUnusedWhilePending: false,
                VariableDescriptorCount: false,
                UniformBufferUpdateAfterBind: false,
                SampledImageUpdateAfterBind: false,
                StorageImageUpdateAfterBind: false,
                StorageBufferUpdateAfterBind: true));

        var storageLayout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                0, GraphicsDescriptorType.StorageBuffer, 8,
                GraphicsShaderStages.Compute, GraphicsBindingFlags.UpdateAfterBind)
        ]);
        storageLayout.Validate(capabilities);

        var uniformLayout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                0, GraphicsDescriptorType.UniformBuffer, 8,
                GraphicsShaderStages.Vertex, GraphicsBindingFlags.UpdateAfterBind)
        ]);
        Assert.Throws<GraphicsException>(() => uniformLayout.Validate(capabilities));
    }

    [Fact]
    public void DescriptorIndexingFeaturesAreIndependentlyValidated()
    {
        var partiallyBoundOnly = CreateCapabilities(new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: true,
            UpdateUnusedWhilePending: false,
            VariableDescriptorCount: false,
            UniformBufferUpdateAfterBind: false,
            SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false,
            StorageBufferUpdateAfterBind: false));

        var validPartial = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                0, GraphicsDescriptorType.SampledTexture, 32,
                GraphicsShaderStages.Fragment, GraphicsBindingFlags.PartiallyBound)
        ]);
        validPartial.Validate(partiallyBoundOnly);

        var unsupportedVariable = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                0, GraphicsDescriptorType.StorageBuffer, 32,
                GraphicsShaderStages.Compute, GraphicsBindingFlags.VariableCount)
        ]);
        Assert.Throws<GraphicsException>(() => unsupportedVariable.Validate(partiallyBoundOnly));

        var updateUnusedOnly = CreateCapabilities(new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: false,
            UpdateUnusedWhilePending: true,
            VariableDescriptorCount: false,
            UniformBufferUpdateAfterBind: false,
            SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false,
            StorageBufferUpdateAfterBind: false));
        var unusedWhilePending = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                1, GraphicsDescriptorType.SampledTexture, 4,
                GraphicsShaderStages.Fragment, GraphicsBindingFlags.UpdateUnusedWhilePending)
        ]);
        // Vulkan's UPDATE_UNUSED_WHILE_PENDING flag is not required to also imply UPDATE_AFTER_BIND.
        unusedWhilePending.Validate(updateUnusedOnly);
    }

    [Fact]
    public void DescriptorIndexingCapabilitiesRequireTheUmbrellaFeatureToBeEnabled()
    {
        var descriptorCapabilities = new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: true,
            UpdateUnusedWhilePending: false,
            VariableDescriptorCount: false,
            UniformBufferUpdateAfterBind: false,
            SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false,
            StorageBufferUpdateAfterBind: false);

        Assert.Throws<ArgumentException>(() => new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.None),
            GraphicsQueueCapabilities.Graphics,
            default,
            ReadOnlySpan<GraphicsFormatCapabilities>.Empty,
            descriptorCapabilities));
    }

    [Fact]
    public void DynamicUniformAndStorageOffsetsUseTheirOwnDeviceAlignments()
    {
        var layout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                2, GraphicsDescriptorType.DynamicUniformBuffer, 2, GraphicsShaderStages.Vertex),
            new GraphicsBindingLayoutEntry(
                7, GraphicsDescriptorType.DynamicStorageBuffer, 1, GraphicsShaderStages.Compute)
        ]);
        GraphicsDynamicBufferOffset[] validOffsets =
        [
            new(2, 0, 256),
            new(2, 1, 512),
            new(7, 0, 48)
        ];
        var limits = new GraphicsDeviceLimits(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            MinUniformBufferOffsetAlignment: 256,
            MinStorageBufferOffsetAlignment: 16);

        layout.ValidateDynamicOffsets(validOffsets, limits);
        Assert.Throws<ArgumentException>(() =>
            layout.ValidateDynamicOffsets(
            [
                new(2, 0, 256),
                new(2, 1, 512),
                new(7, 0, 49)
            ], limits));
        Assert.Throws<ArgumentException>(() =>
            layout.ValidateDynamicOffsets(
            [
                new(2, 0, 128),
                new(2, 1, 512),
                new(7, 0, 48)
            ], limits));
    }

    [Fact]
    public void DynamicOffsetAlignmentIsSkippedOnlyWhenBackendDoesNotReportIt()
    {
        var layout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                0, GraphicsDescriptorType.DynamicUniformBuffer, 1, GraphicsShaderStages.Vertex)
        ]);

        layout.ValidateDynamicOffsets(
            [new GraphicsDynamicBufferOffset(0, 0, 3)],
            default);
    }

    [Fact]
    public void DynamicOffsetValidationRejectsCountsTooLargeForManagedSpans()
    {
        var layout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                0, GraphicsDescriptorType.DynamicStorageBuffer, uint.MaxValue, GraphicsShaderStages.Compute)
        ]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            layout.ValidateDynamicOffsets(ReadOnlySpan<GraphicsDynamicBufferOffset>.Empty));
    }

    [Fact]
    public void DeviceLimitsRequirePowerOfTwoDynamicOffsetAlignments()
    {
        var invalidUniform = new GraphicsDeviceLimits(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            MinUniformBufferOffsetAlignment: 3);
        var invalidStorage = new GraphicsDeviceLimits(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            MinStorageBufferOffsetAlignment: 24);

        Assert.Throws<ArgumentOutOfRangeException>(invalidUniform.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(invalidStorage.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.None),
            GraphicsQueueCapabilities.Graphics,
            invalidUniform,
            ReadOnlySpan<GraphicsFormatCapabilities>.Empty));
    }

    private static GraphicsDeviceCapabilities CreateCapabilities(
        GraphicsDescriptorIndexingCapabilities descriptorIndexing) =>
        new(
            new GraphicsFeatureSet(GraphicsFeature.DescriptorIndexing),
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute,
            default,
            ReadOnlySpan<GraphicsFormatCapabilities>.Empty,
            descriptorIndexing);
}


public sealed class GraphicsDescriptorIndexingRequestTests
{
    [Fact]
    public void DeviceOptionsRequireUmbrellaFeatureForFineGrainedDescriptorRequirements()
    {
        var options = new GraphicsDeviceOptions
        {
            RequiredDescriptorIndexing = new GraphicsDescriptorIndexingCapabilities(
                PartiallyBound: true,
                UpdateUnusedWhilePending: false,
                VariableDescriptorCount: false,
                UniformBufferUpdateAfterBind: false,
                SampledImageUpdateAfterBind: false,
                StorageImageUpdateAfterBind: false,
                StorageBufferUpdateAfterBind: false)
        };

        Assert.Throws<ArgumentException>(options.Validate);
        (options with { RequiredFeatures = GraphicsFeature.DescriptorIndexing }).Validate();
    }

    [Fact]
    public void AdapterReportsPhysicalDescriptorIndexingSeparatelyFromEnabledDeviceCapabilities()
    {
        var physical = new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: true,
            UpdateUnusedWhilePending: true,
            VariableDescriptorCount: true,
            UniformBufferUpdateAfterBind: false,
            SampledImageUpdateAfterBind: true,
            StorageImageUpdateAfterBind: false,
            StorageBufferUpdateAfterBind: true);
        var adapter = new GraphicsAdapterInfo(
            new GraphicsAdapterId(1, 2), "Adapter", GraphicsAdapterType.Discrete, 1, 2, 4096)
        {
            SupportedDescriptorIndexing = physical
        };
        var enabled = new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: true,
            UpdateUnusedWhilePending: false,
            VariableDescriptorCount: false,
            UniformBufferUpdateAfterBind: false,
            SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false,
            StorageBufferUpdateAfterBind: false);

        Assert.True(adapter.SupportedDescriptorIndexing.Includes(enabled));
        Assert.False(enabled.Includes(adapter.SupportedDescriptorIndexing));
    }

    [Fact]
    public async Task FactoryRejectsAndDisposesDeviceMissingRequestedDescriptorCapabilities()
    {
        var enabled = new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: true,
            UpdateUnusedWhilePending: false,
            VariableDescriptorCount: false,
            UniformBufferUpdateAfterBind: false,
            SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false,
            StorageBufferUpdateAfterBind: false);
        var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.Vulkan,
            GraphicsFeature.DescriptorIndexing,
            GraphicsQueueCapabilities.Graphics,
            new GraphicsDeviceCapabilities(
                new GraphicsFeatureSet(GraphicsFeature.DescriptorIndexing),
                GraphicsQueueCapabilities.Graphics,
                default,
                ReadOnlySpan<GraphicsFormatCapabilities>.Empty,
                enabled));
        var registry = new GraphicsBackendProviderRegistry();
        registry.Register(new GraphicsDeviceFactoryTests.TestProvider(GraphicsBackend.Vulkan, device));

        var required = new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: true,
            UpdateUnusedWhilePending: false,
            VariableDescriptorCount: true,
            UniformBufferUpdateAfterBind: false,
            SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false,
            StorageBufferUpdateAfterBind: false);

        GraphicsDescriptorIndexingNotSupportedException exception =
            await Assert.ThrowsAsync<GraphicsDescriptorIndexingNotSupportedException>(async () =>
                await GraphicsDeviceFactory.CreateAsync(
                    registry,
                    new GraphicsDeviceOptions
                    {
                        Backend = GraphicsBackend.Vulkan,
                        RequiredFeatures = GraphicsFeature.DescriptorIndexing,
                        RequiredDescriptorIndexing = required
                    }));

        Assert.True(exception.Missing.VariableDescriptorCount);
        Assert.True(device.IsDisposed);
    }

    [Fact]
    public async Task AutomaticProviderSelectionFallsBackWhenDescriptorCapabilitiesCannotBeEnabled()
    {
        var required = new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: true, UpdateUnusedWhilePending: true, VariableDescriptorCount: true,
            UniformBufferUpdateAfterBind: false, SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false, StorageBufferUpdateAfterBind: false);
        var insufficient = new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: true, UpdateUnusedWhilePending: false, VariableDescriptorCount: false,
            UniformBufferUpdateAfterBind: false, SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false, StorageBufferUpdateAfterBind: false);

        GraphicsDeviceFactoryTests.TestDevice CreateDevice(
            GraphicsBackend backend, GraphicsDescriptorIndexingCapabilities enabled) =>
            new(backend, GraphicsFeature.DescriptorIndexing, GraphicsQueueCapabilities.Graphics,
                new GraphicsDeviceCapabilities(
                    new GraphicsFeatureSet(GraphicsFeature.DescriptorIndexing),
                    GraphicsQueueCapabilities.Graphics, default,
                    ReadOnlySpan<GraphicsFormatCapabilities>.Empty, enabled));

        var firstDevice = CreateDevice(GraphicsBackend.Vulkan, insufficient);
        var secondDevice = CreateDevice(GraphicsBackend.Direct3D12, required);
        var firstProvider = new GraphicsDeviceFactoryTests.TestProvider(GraphicsBackend.Vulkan, firstDevice);
        var secondProvider = new GraphicsDeviceFactoryTests.TestProvider(GraphicsBackend.Direct3D12, secondDevice);
        var registry = new GraphicsBackendProviderRegistry();
        registry.Register(firstProvider);
        registry.Register(secondProvider);

        using IGraphicsDevice selected = await GraphicsDeviceFactory.CreateAsync(
            registry,
            new GraphicsDeviceOptions
            {
                RequiredFeatures = GraphicsFeature.DescriptorIndexing,
                RequiredDescriptorIndexing = required
            });

        Assert.Same(secondDevice, selected);
        Assert.True(firstDevice.IsDisposed);
        Assert.Equal(1, firstProvider.CreateCount);
        Assert.Equal(1, secondProvider.CreateCount);
    }

    [Fact]
    public async Task FactoryAcceptsDeviceThatEnablesAllRequestedDescriptorCapabilities()
    {
        var enabled = new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: true,
            UpdateUnusedWhilePending: true,
            VariableDescriptorCount: true,
            UniformBufferUpdateAfterBind: false,
            SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false,
            StorageBufferUpdateAfterBind: true);
        var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.Vulkan,
            GraphicsFeature.DescriptorIndexing,
            GraphicsQueueCapabilities.Graphics,
            new GraphicsDeviceCapabilities(
                new GraphicsFeatureSet(GraphicsFeature.DescriptorIndexing),
                GraphicsQueueCapabilities.Graphics,
                default,
                ReadOnlySpan<GraphicsFormatCapabilities>.Empty,
                enabled));
        var registry = new GraphicsBackendProviderRegistry();
        registry.Register(new GraphicsDeviceFactoryTests.TestProvider(GraphicsBackend.Vulkan, device));

        using IGraphicsDevice actual = await GraphicsDeviceFactory.CreateAsync(
            registry,
            new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.Vulkan,
                RequiredFeatures = GraphicsFeature.DescriptorIndexing,
                RequiredDescriptorIndexing = new GraphicsDescriptorIndexingCapabilities(
                    PartiallyBound: true,
                    UpdateUnusedWhilePending: true,
                    VariableDescriptorCount: true,
                    UniformBufferUpdateAfterBind: false,
                    SampledImageUpdateAfterBind: false,
                    StorageImageUpdateAfterBind: false,
                    StorageBufferUpdateAfterBind: true)
            });

        Assert.Same(device, actual);
    }
}
