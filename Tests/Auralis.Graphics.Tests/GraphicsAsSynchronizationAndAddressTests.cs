using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class GraphicsAsSynchronizationAndAddressTests
{
    [Fact]
    public void AsBuildInputBuffersUseDedicatedBuildInputReadAccess()
    {
        var device = CreateDevice();
        var input = new TestBuffer(device,
            new GraphicsBufferDescription(512, GraphicsBufferUsage.AccelerationStructureBuildInput));

        new GraphicsResourceBarrier(
            input,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsResourceAccess.AccelerationStructureBuildInputRead,
            GraphicsResourceAccess.AccelerationStructureBuildInputRead).Validate();

        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            input,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsResourceAccess.ShaderRead,
            GraphicsResourceAccess.AccelerationStructureBuildInputRead).Validate());

        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            input,
            GraphicsPipelineStage.ComputeShader,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsResourceAccess.AccelerationStructureBuildInputRead,
            GraphicsResourceAccess.AccelerationStructureBuildInputRead).Validate());

        var ordinaryStorage = new TestBuffer(device,
            new GraphicsBufferDescription(512, GraphicsBufferUsage.Storage));
        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            ordinaryStorage,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsResourceAccess.AccelerationStructureBuildInputRead,
            GraphicsResourceAccess.AccelerationStructureBuildInputRead).Validate());
    }

    [Fact]
    public void AsScratchBuffersUseDedicatedAsReadWriteAccessAndUsage()
    {
        var device = CreateDevice();
        var scratch = new TestBuffer(device,
            new GraphicsBufferDescription(512, GraphicsBufferUsage.AccelerationStructureScratch));

        new GraphicsResourceBarrier(
            scratch,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsResourceAccess.AccelerationStructureRead,
            GraphicsResourceAccess.AccelerationStructureWrite).Validate();

        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            scratch,
            GraphicsPipelineStage.RayTracingShader,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsResourceAccess.AccelerationStructureRead,
            GraphicsResourceAccess.AccelerationStructureWrite).Validate());

        var storageOnly = new TestBuffer(device,
            new GraphicsBufferDescription(512, GraphicsBufferUsage.Storage));
        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            storageOnly,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsResourceAccess.AccelerationStructureRead,
            GraphicsResourceAccess.AccelerationStructureWrite).Validate());
    }

    [Fact]
    public void AccelerationStructureStorageUsesAsAccessIntentsInBuildAndTraceStages()
    {
        var device = CreateDevice();
        var vertex = new TestBuffer(device,
            new GraphicsBufferDescription(256, GraphicsBufferUsage.AccelerationStructureBuildInput));
        var structure = CreateBlas(device, vertex);

        new GraphicsResourceBarrier(
            structure,
            GraphicsPipelineStage.AccelerationStructureBuild,
            GraphicsPipelineStage.RayTracingShader,
            GraphicsResourceAccess.AccelerationStructureWrite,
            GraphicsResourceAccess.AccelerationStructureRead).Validate();

        Assert.Throws<ArgumentException>(() => new GraphicsResourceBarrier(
            structure,
            GraphicsPipelineStage.RayTracingShader,
            GraphicsPipelineStage.RayTracingShader,
            GraphicsResourceAccess.AccelerationStructureRead,
            GraphicsResourceAccess.AccelerationStructureWrite).Validate());
    }

    [Fact]
    public void TriangleGeometryRejectsFullGpuAddressRangeOverflow()
    {
        var device = CreateDevice();
        var vertex = new TestBuffer(device,
            new GraphicsBufferDescription(256, GraphicsBufferUsage.AccelerationStructureBuildInput),
            ulong.MaxValue - 63);

        var geometry = new GraphicsRayTracingTriangleGeometry(
            vertex, 0, 12, 8, GraphicsFormat.RGB32Float);
        Assert.Throws<ArgumentOutOfRangeException>(geometry.Validate);
    }

    [Fact]
    public void ShaderBindingTableRejectsFullGpuAddressRangeOverflow()
    {
        var device = CreateDevice();
        var buffer = new TestBuffer(device,
            new GraphicsBufferDescription(256, GraphicsBufferUsage.ShaderBindingTable),
            ulong.MaxValue - 63);
        var properties = new GraphicsShaderBindingTableProperties(32, 8, 64, 4096);
        var region = new GraphicsShaderBindingTableRegion(buffer, 0, 128, 32);

        Assert.Throws<ArgumentOutOfRangeException>(() => region.Validate(properties));
    }

    [Fact]
    public void TlasInstanceBufferRejectsFullGpuAddressRangeOverflow()
    {
        var device = CreateDevice();
        var buffer = new TestBuffer(device,
            new GraphicsBufferDescription(256, GraphicsBufferUsage.AccelerationStructureBuildInput),
            ulong.MaxValue - 15);
        var description = new GraphicsAccelerationStructureDescription
        {
            IsTopLevel = true,
            Flags = GraphicsAccelerationStructureBuildFlags.None,
            MaximumInstanceCount = 2,
            Geometries = ReadOnlyMemory<GraphicsAccelerationStructureGeometry>.Empty
        };
        var instances = new GraphicsRayTracingInstanceBuffer(buffer, 0, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => instances.Validate(device, description));
    }

    [Fact]
    public void ScratchBufferRejectsFullGpuAddressRangeOverflow()
    {
        var device = CreateDevice();
        var vertex = new TestBuffer(device,
            new GraphicsBufferDescription(256, GraphicsBufferUsage.AccelerationStructureBuildInput));
        var destination = CreateBlas(device, vertex);
        var scratch = new TestBuffer(device,
            new GraphicsBufferDescription(512, GraphicsBufferUsage.AccelerationStructureScratch),
            ulong.MaxValue - 255);
        var sizes = new GraphicsAccelerationStructureSizes(1024, 512, 256, 256, 256);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GraphicsRayTracingCommandValidation.ValidateBuild(
                destination, null, scratch, 0, sizes,
                GraphicsAccelerationStructureBuildFlags.None,
                ReadOnlySpan<GraphicsRayTracingInstance>.Empty));
    }

    [Fact]
    public void SbtAlignmentUsesEffectiveGpuAddressRatherThanBufferOffsetAlone()
    {
        var device = CreateDevice();
        var buffer = new TestBuffer(device,
            new GraphicsBufferDescription(256, GraphicsBufferUsage.ShaderBindingTable),
            0x10001);
        var properties = new GraphicsShaderBindingTableProperties(32, 8, 64, 4096);
        var region = new GraphicsShaderBindingTableRegion(buffer, 63, 32, 32);

        region.Validate(properties);
        Assert.Equal(0x10040UL, region.DeviceAddress);
    }

    private static TestDevice CreateDevice()
    {
        var capabilities = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.RayTracing),
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute,
            default,
            [
                new GraphicsFormatCapabilities(
                    GraphicsFormat.RGB32Float,
                    GraphicsFormatSupport.AccelerationStructureVertex,
                    GraphicsSampleCountFlags.One)
            ]);
        return new TestDevice(
            GraphicsBackend.Vulkan,
            GraphicsFeature.RayTracing,
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute,
            capabilities);
    }

    private static TestAccelerationStructure CreateBlas(TestDevice device, TestBuffer vertexBuffer)
    {
        var geometry = new GraphicsAccelerationStructureGeometry(
            GraphicsAccelerationStructureGeometryType.Triangles,
            new GraphicsRayTracingTriangleGeometry(vertexBuffer, 0, 12, 3, GraphicsFormat.RGB32Float),
            default);
        var description = new GraphicsAccelerationStructureDescription
        {
            IsTopLevel = false,
            Flags = GraphicsAccelerationStructureBuildFlags.None,
            Geometries = new[] { geometry }
        };
        return new TestAccelerationStructure(device, description);
    }

    private sealed class TestBuffer(
        IGraphicsDevice device,
        GraphicsBufferDescription description,
        ulong deviceAddress = 0x10000)
        : GraphicsDeviceObject(device), IGraphicsBuffer, IGraphicsBufferDeviceAddress
    {
        public string? DebugName => null;
        public GraphicsBufferDescription Description { get; } = description;
        public ulong DeviceAddress { get; } = deviceAddress;
        public Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue) =>
            throw new NotSupportedException();
        public void Unmap() => throw new NotSupportedException();
        protected override void DisposeCore() { }
    }

    private sealed class TestAccelerationStructure(
        IGraphicsDevice device,
        GraphicsAccelerationStructureDescription description)
        : GraphicsDeviceObject(device), IGraphicsAccelerationStructure
    {
        public string? DebugName => null;
        public GraphicsAccelerationStructureDescription Description { get; } = description;
        public bool IsTopLevel => Description.IsTopLevel;
        public uint InstanceCount => 0;
        public ulong DeviceAddress => 0x20000;
        protected override void DisposeCore() { }
    }
}
