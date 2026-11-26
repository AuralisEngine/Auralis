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

    private static GraphicsDeviceCapabilities CreateCapabilities(
        GraphicsDescriptorIndexingCapabilities descriptorIndexing) =>
        new(
            new GraphicsFeatureSet(GraphicsFeature.DescriptorIndexing),
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute,
            default,
            ReadOnlySpan<GraphicsFormatCapabilities>.Empty,
            descriptorIndexing);
}
