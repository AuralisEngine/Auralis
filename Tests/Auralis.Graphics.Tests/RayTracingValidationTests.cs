using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class RayTracingValidationTests
{
    [Fact]
    public void TriangleHitGroupsAllowEveryClosestAnyHitCombination()
    {
        var device = CreateDevice();
        IGraphicsShader[] shaders =
        [
            new TestShader(device, GraphicsShaderStage.RayGeneration),
            new TestShader(device, GraphicsShaderStage.AnyHit),
            new TestShader(device, GraphicsShaderStage.ClosestHit)
        ];
        GraphicsRayTracingShaderEntry[] stages =
        [
            new(shaders[0], GraphicsRayTracingShaderStage.RayGeneration, "raygen"),
            new(shaders[1], GraphicsRayTracingShaderStage.AnyHit, "anyhit"),
            new(shaders[2], GraphicsRayTracingShaderStage.ClosestHit, "closest")
        ];
        GraphicsRayTracingShaderGroup[] groups =
        [
            GeneralRayGeneration(),
            new(GraphicsRayTracingShaderGroupType.TrianglesHitGroup, -1, -1, -1, -1),
            new(GraphicsRayTracingShaderGroupType.TrianglesHitGroup, -1, 2, -1, -1),
            new(GraphicsRayTracingShaderGroupType.TrianglesHitGroup, -1, -1, 1, -1),
            new(GraphicsRayTracingShaderGroupType.TrianglesHitGroup, -1, 2, 1, -1)
        ];

        CreatePipeline(device, stages, groups).Validate();
    }

    [Fact]
    public void ProceduralHitGroupsRequireIntersectionButAllowOptionalHitShaders()
    {
        var device = CreateDevice();
        IGraphicsShader[] shaders =
        [
            new TestShader(device, GraphicsShaderStage.RayGeneration),
            new TestShader(device, GraphicsShaderStage.Intersection),
            new TestShader(device, GraphicsShaderStage.AnyHit),
            new TestShader(device, GraphicsShaderStage.ClosestHit)
        ];
        GraphicsRayTracingShaderEntry[] stages =
        [
            new(shaders[0], GraphicsRayTracingShaderStage.RayGeneration, "raygen"),
            new(shaders[1], GraphicsRayTracingShaderStage.Intersection, "intersection"),
            new(shaders[2], GraphicsRayTracingShaderStage.AnyHit, "anyhit"),
            new(shaders[3], GraphicsRayTracingShaderStage.ClosestHit, "closest")
        ];
        GraphicsRayTracingShaderGroup[] groups =
        [
            GeneralRayGeneration(),
            new(GraphicsRayTracingShaderGroupType.ProceduralHitGroup, -1, -1, -1, 1),
            new(GraphicsRayTracingShaderGroupType.ProceduralHitGroup, -1, 3, -1, 1),
            new(GraphicsRayTracingShaderGroupType.ProceduralHitGroup, -1, -1, 2, 1),
            new(GraphicsRayTracingShaderGroupType.ProceduralHitGroup, -1, 3, 2, 1)
        ];

        CreatePipeline(device, stages, groups).Validate();
    }

    [Fact]
    public void RayTracingHitGroupsRejectInvalidIntersectionAndShaderReferences()
    {
        var device = CreateDevice();
        IGraphicsShader[] shaders =
        [
            new TestShader(device, GraphicsShaderStage.RayGeneration),
            new TestShader(device, GraphicsShaderStage.Intersection),
            new TestShader(device, GraphicsShaderStage.ClosestHit)
        ];
        GraphicsRayTracingShaderEntry[] stages =
        [
            new(shaders[0], GraphicsRayTracingShaderStage.RayGeneration, "raygen"),
            new(shaders[1], GraphicsRayTracingShaderStage.Intersection, "intersection"),
            new(shaders[2], GraphicsRayTracingShaderStage.ClosestHit, "closest")
        ];

        Assert.Throws<ArgumentException>(() => CreatePipeline(device, stages,
        [
            GeneralRayGeneration(),
            new(GraphicsRayTracingShaderGroupType.TrianglesHitGroup, -1, 2, -1, 1)
        ]).Validate());

        Assert.Throws<ArgumentException>(() => CreatePipeline(device, stages,
        [
            GeneralRayGeneration(),
            new(GraphicsRayTracingShaderGroupType.ProceduralHitGroup, -1, 2, -1, -1)
        ]).Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePipeline(device, stages,
        [
            GeneralRayGeneration(),
            new(GraphicsRayTracingShaderGroupType.TrianglesHitGroup, -1, 99, -1, -1)
        ]).Validate());

        Assert.Throws<ArgumentException>(() => CreatePipeline(device, stages,
        [
            GeneralRayGeneration(),
            new(GraphicsRayTracingShaderGroupType.TrianglesHitGroup, -1, 1, -1, -1)
        ]).Validate());
    }

    [Fact]
    public void RayTracingPipelineValidatesLayoutShaderLifetimeAndDeviceOwnership()
    {
        var device = CreateDevice();
        var layout = new TestPipelineLayout(device);
        var shader = new TestShader(device, GraphicsShaderStage.RayGeneration);
        var valid = CreatePipeline(device,
            [new(shader, GraphicsRayTracingShaderStage.RayGeneration, "raygen")],
            [GeneralRayGeneration()],
            layout);

        valid.Validate();

        layout.Dispose();
        Assert.Throws<ObjectDisposedException>(valid.Validate);

        var liveDevice = CreateDevice();
        var disposedDeviceLayout = new TestPipelineLayout(liveDevice);
        var disposedDeviceDescription = CreatePipeline(liveDevice,
            [new(new TestShader(liveDevice, GraphicsShaderStage.RayGeneration),
                GraphicsRayTracingShaderStage.RayGeneration, "raygen")],
            [GeneralRayGeneration()], disposedDeviceLayout);
        liveDevice.Dispose();
        Assert.Throws<ObjectDisposedException>(disposedDeviceDescription.Validate);

        var shaderDevice = CreateDevice();
        var liveLayout = new TestPipelineLayout(shaderDevice);
        var disposedShader = new TestShader(shaderDevice, GraphicsShaderStage.RayGeneration);
        var disposedShaderDescription = CreatePipeline(shaderDevice,
            [new(disposedShader, GraphicsRayTracingShaderStage.RayGeneration, "raygen")],
            [GeneralRayGeneration()], liveLayout);
        disposedShader.Dispose();
        Assert.Throws<ObjectDisposedException>(disposedShaderDescription.Validate);

        var otherDevice = CreateDevice();
        var crossDeviceDescription = CreatePipeline(shaderDevice,
            [new(new TestShader(otherDevice, GraphicsShaderStage.RayGeneration),
                GraphicsRayTracingShaderStage.RayGeneration, "raygen")],
            [GeneralRayGeneration()], liveLayout);
        Assert.Throws<ArgumentException>(crossDeviceDescription.Validate);
    }

    [Fact]
    public void RayTracingPipelineValidatesShaderStageEntryPointAndDeviceLimits()
    {
        var device = CreateDevice(new GraphicsDeviceLimits(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            MaxRayRecursionDepth: 2,
            MaxRayPayloadSizeInBytes: 32,
            MaxRayHitAttributeSizeInBytes: 8));
        var layout = new TestPipelineLayout(device);
        var wrongStage = CreatePipeline(device,
            [new(new TestShader(device, GraphicsShaderStage.Miss),
                GraphicsRayTracingShaderStage.RayGeneration, "raygen")],
            [GeneralRayGeneration()], layout);
        Assert.Throws<ArgumentException>(wrongStage.Validate);

        var emptyEntryPoint = CreatePipeline(device,
            [new(new TestShader(device, GraphicsShaderStage.RayGeneration),
                GraphicsRayTracingShaderStage.RayGeneration, " ")],
            [GeneralRayGeneration()], layout);
        Assert.Throws<ArgumentException>(emptyEntryPoint.Validate);

        var tooDeep = CreatePipeline(device,
            [new(new TestShader(device, GraphicsShaderStage.RayGeneration),
                GraphicsRayTracingShaderStage.RayGeneration, "raygen")],
            [GeneralRayGeneration()], layout) with { MaximumRecursionDepth = 3 };
        Assert.Throws<GraphicsException>(tooDeep.Validate);

        var tooLargePayload = CreatePipeline(device,
            [new(new TestShader(device, GraphicsShaderStage.RayGeneration),
                GraphicsRayTracingShaderStage.RayGeneration, "raygen")],
            [GeneralRayGeneration()], layout) with { MaximumPayloadSizeInBytes = 64 };
        Assert.Throws<GraphicsException>(tooLargePayload.Validate);
    }

    [Fact]
    public void RayTracingPipelineRequiresAtLeastOneRayGenerationGroup()
    {
        var device = CreateDevice();
        var miss = new TestShader(device, GraphicsShaderStage.Miss);
        var description = CreatePipeline(device,
            [new(miss, GraphicsRayTracingShaderStage.Miss, "miss")],
            [new(GraphicsRayTracingShaderGroupType.General, 0, -1, -1, -1)]);
        Assert.Throws<ArgumentException>(description.Validate);
    }

    [Fact]
    public void RayTracingVertexFormatsUseTheConsumedPositionBytesAndComponentAlignment()
    {
        var device = CreateDevice();
        var buffer = new TestBuffer(device,
            new GraphicsBufferDescription(256, GraphicsBufferUsage.AccelerationStructureBuildInput),
            0x10000);

        new GraphicsRayTracingTriangleGeometry(buffer, 0, 8, 3, GraphicsFormat.RG32Float).Validate();
        new GraphicsRayTracingTriangleGeometry(buffer, 0, 6, 3, GraphicsFormat.RGBA16Float).Validate();
        new GraphicsRayTracingTriangleGeometry(buffer, 0, 6, 3, GraphicsFormat.RGBA16UNorm).Validate();
        new GraphicsRayTracingTriangleGeometry(buffer, 0, 12, 3, GraphicsFormat.RGBA32Float).Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsRayTracingTriangleGeometry(buffer, 0, 5, 3, GraphicsFormat.RGBA16Float).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsRayTracingTriangleGeometry(buffer, 2, 8, 3, GraphicsFormat.RG32Float).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsRayTracingTriangleGeometry(buffer, 248, 8, 3, GraphicsFormat.RG32Float).Validate());
        var addressOverflowBuffer = new TestBuffer(device,
            new GraphicsBufferDescription(64, GraphicsBufferUsage.AccelerationStructureBuildInput),
            ulong.MaxValue - 4);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsRayTracingTriangleGeometry(addressOverflowBuffer, 0, 12, 3, GraphicsFormat.RGB32Float).Validate());

        var capabilities = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.RayTracing),
            GraphicsQueueCapabilities.Graphics,
            default,
            ReadOnlySpan<GraphicsFormatCapabilities>.Empty);
        Assert.Throws<GraphicsException>(() =>
            new GraphicsRayTracingTriangleGeometry(buffer, 0, 8, 3, GraphicsFormat.RG32Float).Validate(capabilities));
    }

    [Fact]
    public void PackedRayTracingInstanceValidatesLimitsFlagsTransformsAndBlasReferences()
    {
        var device = CreateDevice();
        var buffer = new TestBuffer(device,
            new GraphicsBufferDescription(36, GraphicsBufferUsage.AccelerationStructureBuildInput),
            0x1000);
        var blasDescription = new GraphicsAccelerationStructureDescription
        {
            IsTopLevel = false,
            Flags = GraphicsAccelerationStructureBuildFlags.AllowUpdate,
            Geometries = new GraphicsAccelerationStructureGeometry[]
            {
                new(
                    GraphicsAccelerationStructureGeometryType.Triangles,
                    new GraphicsRayTracingTriangleGeometry(buffer, 0, 12, 3, GraphicsFormat.RGB32Float),
                    default)
            }
        };
        var blas = new TestAccelerationStructure(device, blasDescription);
        var maxValues = new GraphicsRayTracingInstance(
            blas, GraphicsTransform3x4.Identity,
            InstanceCustomIndex: 0x00FF_FFFF,
            ShaderBindingTableRecordOffset: 0x00FF_FFFF);
        var packed = new GraphicsRayTracingInstanceData(maxValues, device);
        Assert.Equal(0x00FF_FFFFu | 0xFF00_0000u, packed.InstanceCustomIndexAndMask);
        Assert.Equal(0x00FF_FFFFu, packed.ShaderBindingTableRecordOffsetAndFlags);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var invalid = new GraphicsRayTracingInstance(blas, GraphicsTransform3x4.Identity,
                InstanceCustomIndex: 0x0100_0000);
            _ = new GraphicsRayTracingInstanceData(in invalid, device);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var invalid = new GraphicsRayTracingInstance(blas, GraphicsTransform3x4.Identity,
                ShaderBindingTableRecordOffset: 0x0100_0000);
            _ = new GraphicsRayTracingInstanceData(in invalid, device);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var invalid = new GraphicsRayTracingInstance(blas, GraphicsTransform3x4.Identity,
                Flags: (GraphicsRayTracingInstanceFlags)0x80);
            _ = new GraphicsRayTracingInstanceData(in invalid, device);
        });
        Assert.Throws<ArgumentException>(() =>
        {
            var invalid = new GraphicsRayTracingInstance(blas,
                new GraphicsTransform3x4(float.NaN, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0));
            _ = new GraphicsRayTracingInstanceData(in invalid, device);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            var invalid = new GraphicsRayTracingInstance(null!, GraphicsTransform3x4.Identity);
            _ = new GraphicsRayTracingInstanceData(in invalid);
        });
        Assert.Throws<ObjectDisposedException>(() =>
        {
            blas.Dispose();
            _ = new GraphicsRayTracingInstanceData(maxValues, device);
        });
    }

    [Fact]
    public void PackedRayTracingInstanceRejectsBottomLevelStructuresFromAnotherDevice()
    {
        var destinationDevice = CreateDevice();
        var otherDevice = CreateDevice();
        var buffer = new TestBuffer(otherDevice,
            new GraphicsBufferDescription(36, GraphicsBufferUsage.AccelerationStructureBuildInput),
            0x1000);
        var description = new GraphicsAccelerationStructureDescription
        {
            IsTopLevel = false,
            Flags = GraphicsAccelerationStructureBuildFlags.None,
            Geometries = new GraphicsAccelerationStructureGeometry[]
            {
                new(
                    GraphicsAccelerationStructureGeometryType.Triangles,
                    new GraphicsRayTracingTriangleGeometry(buffer, 0, 12, 3, GraphicsFormat.RGB32Float),
                    default)
            }
        };
        var blas = new TestAccelerationStructure(otherDevice, description);
        var instance = new GraphicsRayTracingInstance(blas, GraphicsTransform3x4.Identity);

        Assert.Throws<ArgumentException>(() => new GraphicsRayTracingInstanceData(instance, destinationDevice));
    }

    private static GraphicsRayTracingPipelineDescription CreatePipeline(
        IGraphicsDevice device,
        GraphicsRayTracingShaderEntry[] stages,
        GraphicsRayTracingShaderGroup[] groups,
        IGraphicsPipelineLayout? layout = null) =>
        CreatePipeline(
            device,
            new ReadOnlyMemory<GraphicsRayTracingShaderEntry>(stages),
            new ReadOnlyMemory<GraphicsRayTracingShaderGroup>(groups),
            layout);

    private static GraphicsRayTracingPipelineDescription CreatePipeline(
        IGraphicsDevice device,
        ReadOnlyMemory<GraphicsRayTracingShaderEntry> stages,
        ReadOnlyMemory<GraphicsRayTracingShaderGroup> groups,
        IGraphicsPipelineLayout? layout = null) =>
        new()
        {
            Layout = layout ?? new TestPipelineLayout(device),
            Stages = stages,
            Groups = groups,
            MaximumRecursionDepth = 1,
            MaximumPayloadSizeInBytes = 16,
            MaximumAttributeSizeInBytes = 8
        };

    private static GraphicsRayTracingShaderGroup GeneralRayGeneration() =>
        new(GraphicsRayTracingShaderGroupType.General, 0, -1, -1, -1);

    private static GraphicsDeviceFactoryTests.TestDevice CreateDevice(GraphicsDeviceLimits limits = default)
    {
        var capabilities = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.RayTracing),
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute,
            limits,
            ReadOnlySpan<GraphicsFormatCapabilities>.Empty);
        return new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.Vulkan, GraphicsFeature.RayTracing,
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute,
            capabilities);
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

    private sealed class TestBuffer(
        IGraphicsDevice device,
        GraphicsBufferDescription description,
        ulong deviceAddress) : GraphicsDeviceObject(device), IGraphicsBuffer, IGraphicsBufferDeviceAddress
    {
        public string? DebugName => null;
        public GraphicsBufferDescription Description { get; } = description;
        public ulong DeviceAddress { get; } = deviceAddress;
        public Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue) =>
            throw new NotSupportedException();
        public void Unmap() { }
        protected override void DisposeCore() { }
    }

    private sealed class TestAccelerationStructure(
        IGraphicsDevice device,
        GraphicsAccelerationStructureDescription description) : GraphicsDeviceObject(device), IGraphicsAccelerationStructure
    {
        public string? DebugName => null;
        public GraphicsAccelerationStructureDescription Description { get; } = description;
        public bool IsTopLevel => Description.IsTopLevel;
        public uint InstanceCount => 0;
        public ulong DeviceAddress => 0x8000;
        protected override void DisposeCore() { }
    }
}
