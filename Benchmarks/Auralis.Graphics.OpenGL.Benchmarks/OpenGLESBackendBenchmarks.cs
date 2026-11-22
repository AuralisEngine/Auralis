using System.Diagnostics;
using System.Runtime.InteropServices;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Auralis.Graphics.OpenGL.Benchmarks;

/// <summary>
/// Hardware-dependent OpenGL ES measurements. Results are specific to the driver, GPU, CPU,
/// operating system, context creation stack, runtime, and power/clock state. MemoryDiagnoser
/// reports managed allocations; BenchmarkDotNet reports the runtime environment.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class OpenGLESBackendBenchmarks
{
    private const int BufferSize = 4096;
    private const byte SourcePattern = 0x5A;
    private static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(15);

    private GlfwOpenGLESHardwareSession? _session;
    private IGraphicsDevice _device = null!;
    private IGraphicsCommandQueue _copyQueue = null!;
    private IGraphicsBuffer? _source;
    private IGraphicsBuffer? _destination;
    private IGraphicsCommandList? _commandList;
    private IGraphicsFence? _fence;
    private byte[] _expectedContents = [];
    private ulong _fenceValue;

    [GlobalSetup]
    public void Setup()
    {
        try
        {
            _session = GlfwOpenGLESHardwareSession.CreateAsync().AsTask().GetAwaiter().GetResult();
            _device = _session.Device;
            _copyQueue = _device.GetQueue(GraphicsQueueType.Copy);

            _source = _device.CreateBuffer(new GraphicsBufferDescription(
                BufferSize,
                GraphicsBufferUsage.CopySource,
                GraphicsMemoryAccess.CpuToGpu));
            _destination = _device.CreateBuffer(new GraphicsBufferDescription(
                BufferSize,
                GraphicsBufferUsage.CopyDestination,
                GraphicsMemoryAccess.GpuToCpu));

            _expectedContents = new byte[BufferSize];
            Array.Fill(_expectedContents, SourcePattern);
            Memory<byte> initial = _source.Map(GraphicsMapMode.Write, 0, BufferSize);
            try
            {
                initial.Span.Fill(SourcePattern);
            }
            finally
            {
                _source.Unmap();
            }

            _commandList = _copyQueue.CreateCommandList(GraphicsCommandListUsage.Reusable);
            _fence = _device.CreateFence();

            PrintEnvironment();
            ValidateInitialBufferCopy();
        }
        catch (Exception setupError)
        {
            try
            {
                Cleanup();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "OpenGL ES benchmark setup failed and cleanup also reported an error.",
                    setupError,
                    cleanupError);
            }

            throw;
        }
    }

    [Benchmark(Baseline = true, Description = "Command recording: empty reusable list")]
    public int RecordEmptyCommandList()
    {
        PrepareList();
        _commandList.End();
        return _commandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    [Benchmark(Description = "Command recording: one 4 KiB buffer copy with native resource retention")]
    public int RecordCopyCommand()
    {
        RecordCopy();
        return _commandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    [Benchmark(Description = "Command submission plus GPU fence wait")]
    public int SubmitAndWait()
    {
        RecordCopy();
        ulong value = checked(++_fenceValue);
        _copyQueue.Submit([_commandList], _fence, value);
        if (!_fence.Wait(value, GpuTimeout))
            throw new TimeoutException($"OpenGL ES GPU fence {value} did not complete within {GpuTimeout}.");

        return _commandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    [Benchmark(Description = "Read an already-completed fence value")]
    public ulong ReadCompletedFenceValue() => _fence.CompletedValue;

    [Benchmark(Description = "Submit and poll fence until GPU completion")]
    public ulong SubmitAndPoll()
    {
        RecordCopy();
        ulong value = checked(++_fenceValue);
        _copyQueue.Submit([_commandList], _fence, value);

        long start = Stopwatch.GetTimestamp();
        ulong completed;
        while ((completed = _fence.CompletedValue) < value)
        {
            if (Stopwatch.GetElapsedTime(start) > GpuTimeout)
                throw new TimeoutException(
                    $"OpenGL ES GPU fence {value} did not complete while polling within {GpuTimeout}.");

            Thread.SpinWait(16);
        }

        if (_commandList.State != GraphicsCommandListState.Executable)
            throw new InvalidOperationException(
                "The GLES queue did not retire the command list after its completion fence was signaled.");

        return completed;
    }

    [Benchmark(Description = "Create and dispose a 4 KiB buffer")]
    public void CreateAndDisposeBuffer()
    {
        using IGraphicsBuffer buffer = _device.CreateBuffer(new GraphicsBufferDescription(
            BufferSize,
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
        List<Exception> failures = [];
        bool disposeDeviceFirst = false;

        try
        {
            if (_commandList is not null && _commandList.State == GraphicsCommandListState.Pending)
            {
                if (_fence is null || !_fence.Wait(_fenceValue, GpuTimeout) ||
                    _commandList.State == GraphicsCommandListState.Pending)
                {
                    failures.Add(new TimeoutException(
                        "The GLES benchmark command list was still pending during cleanup; device shutdown will drain it."));
                    disposeDeviceFirst = true;
                }
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            disposeDeviceFirst = true;
        }

        // If bounded waiting could not establish completion, let device shutdown finish the GLES
        // stream and drain its submissions before disposing the list's managed references.
        if (disposeDeviceFirst)
            DisposeSafely(ref _session, failures);

        DisposeSafely(ref _commandList, failures);
        DisposeSafely(ref _fence, failures);
        DisposeSafely(ref _source, failures);
        DisposeSafely(ref _destination, failures);
        DisposeSafely(ref _session, failures);

        if (failures.Count != 0)
            throw new AggregateException("OpenGL ES benchmark cleanup encountered one or more failures.", failures);
    }

    private void PrintEnvironment()
    {
        Console.WriteLine($"Graphics backend: {_device.Backend}");
        Console.WriteLine($"Graphics adapter: {_device.Adapter}");
        Console.WriteLine($"Graphics device/API: {_device.Info}");
        Console.WriteLine($".NET runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Runtime version: {Environment.Version}");
        Console.WriteLine($"Operating system: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"64-bit process: {Environment.Is64BitProcess}");
        Console.WriteLine($"BenchmarkDotNet version: {typeof(BenchmarkAttribute).Assembly.GetName().Version}");
    }

    private void ValidateInitialBufferCopy()
    {
        RecordCopy();
        ulong value = checked(++_fenceValue);
        _copyQueue.Submit([_commandList], _fence, value);
        if (!_fence.Wait(value, GpuTimeout))
            throw new TimeoutException($"The initial GLES buffer-copy fence {value} did not complete.");

        if (_commandList.State != GraphicsCommandListState.Executable)
            throw new InvalidOperationException("The initial GLES buffer-copy command list did not complete.");

        Memory<byte> copied = _destination.Map(GraphicsMapMode.Read, 0, BufferSize);
        try
        {
            if (!copied.Span.SequenceEqual(_expectedContents))
                throw new InvalidOperationException(
                    "The OpenGL ES hardware benchmark copy validation failed: destination bytes differ from the source pattern.");
        }
        finally
        {
            _destination.Unmap();
        }

        // Release the validation copy's recorded resource references before measured iterations.
        _commandList.Reset();
    }

    private void PrepareList()
    {
        GraphicsCommandListState state = _commandList.State;
        if (state == GraphicsCommandListState.Pending)
        {
            if (!_fence.Wait(_fenceValue, GpuTimeout))
                throw new TimeoutException("A prior GLES benchmark submission did not complete within the bounded wait.");

            state = _commandList.State;
            if (state == GraphicsCommandListState.Pending)
                throw new InvalidOperationException(
                    "The GLES fence completed but its command queue still reports the reusable list as pending.");
        }

        if (state == GraphicsCommandListState.Executable)
            _commandList.Reset();
        else if (state != GraphicsCommandListState.Initial)
            throw new InvalidOperationException($"Cannot begin GLES benchmark recording while the list is in state {state}.");

        _commandList.Begin();
    }

    private void RecordCopy()
    {
        PrepareList();
        _commandList.CopyBuffer(_source, 0, _destination, 0, BufferSize);
        _commandList.End();
    }

    private static void DisposeSafely<T>(ref T? resource, List<Exception> failures)
        where T : class, IDisposable
    {
        T? owned = resource;
        resource = null;
        if (owned is null)
            return;

        try
        {
            owned.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
