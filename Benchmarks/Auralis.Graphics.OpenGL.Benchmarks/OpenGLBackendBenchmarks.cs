using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Resources;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Auralis.Graphics.OpenGL.Benchmarks;

/// <summary>
/// Hardware-dependent OpenGL measurements. Results are specific to the driver, GPU, CPU,
/// context creation stack, and power/clock state. MemoryDiagnoser reports managed allocations.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class OpenGLBackendBenchmarks
{
    private GlfwOpenGLHardwareSession _session = null!;
    private IGraphicsDevice _device = null!;
    private IGraphicsCommandQueue _copyQueue = null!;
    private IGraphicsBuffer _source = null!;
    private IGraphicsBuffer _destination = null!;
    private IGraphicsCommandList _commandList = null!;
    private IGraphicsFence _fence = null!;
    private ulong _fenceValue;

    [GlobalSetup]
    public void Setup()
    {
        _session = GlfwOpenGLHardwareSession.CreateAsync().AsTask().GetAwaiter().GetResult();
        _device = _session.Device;
        _copyQueue = _device.GetQueue(GraphicsQueueType.Copy);

        _source = _device.CreateBuffer(new GraphicsBufferDescription(
            4096,
            GraphicsBufferUsage.CopySource,
            GraphicsMemoryAccess.CpuToGpu));
        _destination = _device.CreateBuffer(new GraphicsBufferDescription(
            4096,
            GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.GpuToCpu));
        Memory<byte> initial = _source.Map(GraphicsMapMode.Write, 0, 4096);
        initial.Span.Fill(0x5A);
        _source.Unmap();

        _commandList = _copyQueue.CreateCommandList();
        _fence = _device.CreateFence();
        Console.WriteLine($"Benchmark driver: {_device.Info}");
    }

    [Benchmark(Description = "Command recording: one buffer copy")]
    public int RecordCopyCommand()
    {
        RecordCopy();
        return _commandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    [Benchmark(Description = "Command submission plus GPU fence wait")]
    public int SubmitAndWait()
    {
        RecordCopy();
        ulong value = ++_fenceValue;
        _copyQueue.Submit([_commandList], _fence, value);
        if (!_fence.Wait(value, TimeSpan.FromSeconds(15)))
            throw new TimeoutException($"GPU fence {value} did not complete.");

        return _commandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    [Benchmark(Description = "Completed fence value polling")]
    public ulong PollCompletedFenceValue() => _fence.CompletedValue;

    [Benchmark(Description = "Create and dispose a 4 KiB buffer")]
    public void CreateAndDisposeBuffer()
    {
        using IGraphicsBuffer buffer = _device.CreateBuffer(new GraphicsBufferDescription(
            4096,
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.GpuOnly));
    }

    [Benchmark(Description = "Create and dispose a 256x256 RGBA8 texture")]
    public void CreateAndDisposeTexture()
    {
        using IGraphicsTexture texture = _device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            256, 256, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.CopySource | GraphicsTextureUsage.CopyDestination));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fence?.Dispose();
        _commandList?.Dispose();
        _source?.Dispose();
        _destination?.Dispose();
        _session?.Dispose();
    }

    private void RecordCopy()
    {
        if (_commandList.State == GraphicsCommandListState.Pending)
        {
            if (!_fence.Wait(_fenceValue, TimeSpan.FromSeconds(15)))
                throw new TimeoutException("A prior benchmark submission did not complete.");
        }

        if (_commandList.State == GraphicsCommandListState.Executable)
            _commandList.Reset();

        _commandList.Begin();
        _commandList.CopyBuffer(_source, 0, _destination, 0, 4096);
        _commandList.End();
    }
}
