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
    private IGraphicsCommandList[] _submitLists = [];
    private IGraphicsFence? _fence;
    private byte[] _expectedContents = [];
    private ulong _fenceValue;

    /// Creates the hidden hardware context and device, allocates and initializes the 4 KiB source/destination buffers, creates a reusable command list and fence, validates a real copy, and prepares the pre-recorded list. These operations are outside benchmark measurements. Results are specific to the OpenGL ES driver and test system.
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
            Memory<byte> initial = SourceBuffer.Map(GraphicsMapMode.Write, 0, BufferSize);
            try
            {
                initial.Span.Fill(SourcePattern);
            }
            finally
            {
                SourceBuffer.Unmap();
            }

            _commandList = _copyQueue.CreateCommandList(GraphicsCommandListUsage.Reusable);
            _submitLists = [CommandList];
            _fence = _device.CreateFence();

            PrintEnvironment();
            ValidateInitialBufferCopy();

            // Keep a pre-recorded reusable list available for submission-only measurements.
            CommandList.Begin();
            CommandList.End();
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

    /// Measures CPU-side Begin/End recording of an empty reusable command list. No GPU command is submitted and no GPU completion is measured. Results are specific to the OpenGL ES driver and test system.
    [Benchmark(Baseline = true, Description = "Command recording: empty reusable list")]
    public int RecordEmptyCommandList()
    {
        PrepareList();
        CommandList.End();
        return CommandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    /// Measures CPU-side recording of one 4 KiB buffer-copy command, including validation and retention of the referenced native resources. It does not execute or submit the copy to the GPU. Results are specific to the OpenGL ES driver and test system.
    [Benchmark(Description = "Command recording: one 4 KiB buffer copy with native resource retention")]
    public int RecordCopyCommand()
    {
        RecordCopy();
        return CommandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    /// Measures recording one 4 KiB copy, submitting it, and waiting for the GPU timeline fence. The result includes both managed/backend submission overhead and GPU completion latency. Results are specific to the OpenGL ES driver and test system.
    [Benchmark(Description = "Command submission plus GPU fence wait")]
    public int SubmitAndWait()
    {
        RecordCopy();
        ulong value = checked(++_fenceValue);
        CopyQueue.Submit(_submitLists, Fence, value);
        if (!Fence.Wait(value, GpuTimeout))
            throw new TimeoutException($"OpenGL ES GPU fence {value} did not complete within {GpuTimeout}.");

        return CommandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    /// Measures submitting a command list that was recorded during setup and waiting for its GPU fence. It excludes command recording but still includes submission, completion synchronization, and the follow-up state observation. Results are specific to the OpenGL ES driver and test system.
    [Benchmark(Description = "Submission: pre-recorded reusable command list plus fence wait")]
    public int SubmitPreRecordedCommandList()
    {
        ulong value = checked(++_fenceValue);
        CopyQueue.Submit(_submitLists, Fence, value);
        if (!Fence.Wait(value, GpuTimeout))
            throw new TimeoutException($"OpenGL ES GPU fence {value} did not complete within {GpuTimeout}.");

        return CommandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    /// Measures reading an already-completed fence value. The getter may still acquire the context gate and poll native sync state; it is not a pure managed-field read. Results are specific to the OpenGL ES driver and test system.
    [Benchmark(Description = "Read an already-completed fence value")]
    public ulong ReadCompletedFenceValue() => Fence.CompletedValue;

    /// Measures recording and submitting one 4 KiB copy, then repeatedly polling the timeline fence on the CPU until GPU completion. It also observes command-list retirement; polling cadence contributes to the measured CPU overhead. Results are specific to the OpenGL ES driver and test system.
    [Benchmark(Description = "Submit and poll fence until GPU completion")]
    public ulong SubmitAndPoll()
    {
        RecordCopy();
        ulong value = checked(++_fenceValue);
        CopyQueue.Submit(_submitLists, Fence, value);

        long start = Stopwatch.GetTimestamp();
        ulong completed;
        while ((completed = Fence.CompletedValue) < value)
        {
            if (Stopwatch.GetElapsedTime(start) > GpuTimeout)
                throw new TimeoutException(
                    $"OpenGL ES GPU fence {value} did not complete while polling within {GpuTimeout}.");

            Thread.SpinWait(16);
        }

        if (CommandList.State != GraphicsCommandListState.Executable)
            throw new InvalidOperationException(
                "The GLES queue did not retire the command list after its completion fence was signaled.");

        return completed;
    }

    /// Measures allocation and disposal of a 4 KiB GPU-only buffer, including backend validation and native object creation/destruction under the device context. Results are specific to the OpenGL ES driver and test system.
    [Benchmark(Description = "Create and dispose a 4 KiB buffer")]
    public void CreateAndDisposeBuffer()
    {
        using IGraphicsBuffer buffer = _device.CreateBuffer(new GraphicsBufferDescription(
            BufferSize,
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.GpuOnly));
    }

    /// Measures allocation and disposal of a 256×256 RGBA8 texture, including backend validation and native texture storage creation/destruction. Results are specific to the OpenGL ES driver and test system.
    [Benchmark(Description = "Create and dispose a 256x256 RGBA8 texture")]
    public void CreateAndDisposeTexture()
    {
        using IGraphicsTexture texture = _device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            256, 256, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.CopySource | GraphicsTextureUsage.CopyDestination));
    }

    /// Waits for pending work when possible, falls back to device shutdown to drain the command stream when bounded waiting cannot establish completion, then disposes the command list, fence, buffers, and hardware session. Results are specific to the OpenGL ES driver and test system.
    [GlobalCleanup]
    public void Cleanup()
    {
        List<Exception> failures = [];
        bool disposeDeviceFirst = false;

        try
        {
            if (_commandList is not null && CommandList.State == GraphicsCommandListState.Pending)
            {
                if (_fence is null || !Fence.Wait(_fenceValue, GpuTimeout) ||
                    CommandList.State == GraphicsCommandListState.Pending)
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

    private IGraphicsCommandList CommandList => _commandList ?? throw new InvalidOperationException("The benchmark command list is not initialized.");

    private IGraphicsFence Fence => _fence ?? throw new InvalidOperationException("The benchmark fence is not initialized.");

    private IGraphicsBuffer SourceBuffer => _source ?? throw new InvalidOperationException("The benchmark source buffer is not initialized.");

    private IGraphicsBuffer DestinationBuffer => _destination ?? throw new InvalidOperationException("The benchmark destination buffer is not initialized.");

    private IGraphicsCommandQueue CopyQueue => _copyQueue;

    private void PrintEnvironment()
    {
        Console.WriteLine($"Graphics backend: {_device.Backend}");
        Console.WriteLine($"Graphics adapter: {_device.Adapter}");
        Console.WriteLine($"GPU vendor: {_device.Info.DriverName}");
        Console.WriteLine($"Renderer: {_device.Info.Name}");
        Console.WriteLine($"Graphics API version: {_device.Info.ApiVersion}");
        Console.WriteLine($"Driver/device information: {_device.Info}");
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
        CopyQueue.Submit(_submitLists, Fence, value);
        if (!Fence.Wait(value, GpuTimeout))
            throw new TimeoutException($"The initial GLES buffer-copy fence {value} did not complete.");

        if (CommandList.State != GraphicsCommandListState.Executable)
            throw new InvalidOperationException("The initial GLES buffer-copy command list did not complete.");

        Memory<byte> copied = DestinationBuffer.Map(GraphicsMapMode.Read, 0, BufferSize);
        try
        {
            if (!copied.Span.SequenceEqual(_expectedContents))
                throw new InvalidOperationException(
                    "The OpenGL ES hardware benchmark copy validation failed: destination bytes differ from the source pattern.");
        }
        finally
        {
            DestinationBuffer.Unmap();
        }

        // Release the validation copy's recorded resource references before measured iterations.
        CommandList.Reset();
    }

    private void PrepareList()
    {
        GraphicsCommandListState state = CommandList.State;
        if (state == GraphicsCommandListState.Pending)
        {
            if (!Fence.Wait(_fenceValue, GpuTimeout))
                throw new TimeoutException("A prior GLES benchmark submission did not complete within the bounded wait.");

            state = CommandList.State;
            if (state == GraphicsCommandListState.Pending)
                throw new InvalidOperationException(
                    "The GLES fence completed but its command queue still reports the reusable list as pending.");
        }

        if (state == GraphicsCommandListState.Executable)
            CommandList.Reset();
        else if (state != GraphicsCommandListState.Initial)
            throw new InvalidOperationException($"Cannot begin GLES benchmark recording while the list is in state {state}.");

        CommandList.Begin();
    }

    private void RecordCopy()
    {
        PrepareList();
        CommandList.CopyBuffer(SourceBuffer, 0, DestinationBuffer, 0, BufferSize);
        CommandList.End();
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
