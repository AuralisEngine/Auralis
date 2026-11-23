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
/// Hardware-dependent OpenGL measurements. Results are specific to the driver, GPU, CPU,
/// operating system, context creation stack, runtime, and power/clock state. MemoryDiagnoser
/// reports managed allocations; BenchmarkDotNet reports the runtime environment.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class OpenGLBackendBenchmarks
{
    private const int BufferSize = 4096;
    private const byte SourcePattern = 0x5A;
    private static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(15);

    private GlfwOpenGLHardwareSession? _session;
    private IGraphicsDevice _device = null!;
    private IGraphicsCommandQueue _copyQueue = null!;
    private IGraphicsBuffer? _source;
    private IGraphicsBuffer? _destination;
    private IGraphicsCommandList? _commandList;
    private IGraphicsCommandList[] _submitLists = [];
    private IGraphicsFence? _fence;
    private byte[] _expectedContents = [];
    private ulong _fenceValue;

    private IGraphicsCommandQueue? _graphicsQueue;
    private readonly List<IDisposable> _framebufferResources = new(12);
    private readonly List<IGraphicsCommandList> _framebufferLists = new(5);
    private readonly List<IGraphicsCommandList[]> _framebufferSubmissions = new(5);

    /// Creates the hidden hardware context and device, allocates and initializes the 4 KiB source/destination buffers, creates a reusable command list and fence, validates a real copy, and prepares the pre-recorded list. These operations are outside benchmark measurements. Results are specific to the desktop OpenGL driver and test system.
    [GlobalSetup]
    public void Setup()
    {
        try
        {
            _session = GlfwOpenGLHardwareSession.CreateAsync().AsTask().GetAwaiter().GetResult();
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

            SetupFramebufferBenchmarks();
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
                    "Desktop OpenGL benchmark setup failed and cleanup also reported an error.",
                    setupError,
                    cleanupError);
            }

            throw;
        }
    }

    /// Measures CPU-side Begin/End recording of an empty reusable command list. No GPU command is submitted and no GPU completion is measured. Results are specific to the desktop OpenGL driver and test system.
    [Benchmark(Baseline = true, Description = "Command recording: empty reusable list")]
    public int RecordEmptyCommandList()
    {
        PrepareList();
        CommandList.End();
        return CommandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    /// Measures CPU-side recording of one 4 KiB buffer-copy command, including validation and retention of the referenced native resources. It does not execute or submit the copy to the GPU. Results are specific to the desktop OpenGL driver and test system.
    [Benchmark(Description = "Command recording: one 4 KiB buffer copy with native resource retention")]
    public int RecordCopyCommand()
    {
        RecordCopy();
        return CommandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    /// Measures recording one 4 KiB copy, submitting it, and waiting for the GPU timeline fence. The result includes both managed/backend submission overhead and GPU completion latency. Results are specific to the desktop OpenGL driver and test system.
    [Benchmark(Description = "Command submission plus GPU fence wait")]
    public int SubmitAndWait()
    {
        RecordCopy();
        ulong value = checked(++_fenceValue);
        CopyQueue.Submit(_submitLists, Fence, value);
        if (!Fence.Wait(value, GpuTimeout))
            throw new TimeoutException($"Desktop OpenGL GPU fence {value} did not complete within {GpuTimeout}.");

        return CommandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    /// Measures submitting a command list that was recorded during setup and waiting for its GPU fence. It excludes command recording but still includes submission, completion synchronization, and the follow-up state observation. Results are specific to the desktop OpenGL driver and test system.
    [Benchmark(Description = "Submission: pre-recorded reusable command list plus fence wait")]
    public int SubmitPreRecordedCommandList()
    {
        ulong value = checked(++_fenceValue);
        CopyQueue.Submit(_submitLists, Fence, value);
        if (!Fence.Wait(value, GpuTimeout))
            throw new TimeoutException($"Desktop OpenGL GPU fence {value} did not complete within {GpuTimeout}.");

        return CommandList.State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    /// Measures reading an already-completed fence value. The getter may still acquire the context gate and poll native sync state; it is not a pure managed-field read. Results are specific to the desktop OpenGL driver and test system.
    [Benchmark(Description = "Read an already-completed fence value")]
    public ulong ReadCompletedFenceValue() => Fence.CompletedValue;

    /// Measures recording and submitting one 4 KiB copy, then repeatedly polling the timeline fence on the CPU until GPU completion. It also observes command-list retirement; polling cadence contributes to the measured CPU overhead. Results are specific to the desktop OpenGL driver and test system.
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
                    $"Desktop OpenGL GPU fence {value} did not complete while polling within {GpuTimeout}.");

            Thread.SpinWait(16);
        }

        if (CommandList.State != GraphicsCommandListState.Executable)
            throw new InvalidOperationException(
                "The desktop OpenGL queue did not retire the command list after its completion fence was signaled.");

        return completed;
    }

    /// Measures allocation and disposal of a 4 KiB GPU-only buffer, including backend validation and native object creation/destruction under the device context. Results are specific to the desktop OpenGL driver and test system.
    [Benchmark(Description = "Create and dispose a 4 KiB buffer")]
    public void CreateAndDisposeBuffer()
    {
        using IGraphicsBuffer buffer = _device.CreateBuffer(new GraphicsBufferDescription(
            BufferSize,
            GraphicsBufferUsage.CopySource | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.GpuOnly));
    }

    /// Measures allocation and disposal of a 256×256 RGBA8 texture, including backend validation and native texture storage creation/destruction. Results are specific to the desktop OpenGL driver and test system.
    [Benchmark(Description = "Create and dispose a 256x256 RGBA8 texture")]
    public void CreateAndDisposeTexture()
    {
        using IGraphicsTexture texture = _device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            256, 256, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.CopySource | GraphicsTextureUsage.CopyDestination));
    }

    /// <summary>Measures execution-time framebuffer creation/destruction for repeated identical render-target bindings.</summary>
    [Benchmark(Description = "Framebuffer lifecycle: repeated identical color target bindings")]
    public int RepeatedIdenticalRenderTargetBindings() => SubmitFramebufferWorkload(0);

    /// <summary>Measures framebuffer lifecycle while switching between two color target configurations.</summary>
    [Benchmark(Description = "Framebuffer lifecycle: switch between two color targets")]
    public int SwitchingRenderTargetConfigurations() => SubmitFramebufferWorkload(1);

    /// <summary>Measures framebuffer lifecycle for a multiple-color-attachment configuration.</summary>
    [Benchmark(Description = "Framebuffer lifecycle: multiple color attachments")]
    public int MultipleRenderTargetAttachments() => SubmitFramebufferWorkload(2);

    /// <summary>Measures framebuffer lifecycle for a depth-only configuration.</summary>
    [Benchmark(Description = "Framebuffer lifecycle: depth-only target")]
    public int DepthOnlyRenderTarget() => SubmitFramebufferWorkload(3);

    /// <summary>Measures framebuffer lifecycle for a color-plus-depth configuration.</summary>
    [Benchmark(Description = "Framebuffer lifecycle: color plus depth target")]
    public int ColorAndDepthRenderTarget() => SubmitFramebufferWorkload(4);

    /// <summary>
    /// Waits for pending command lists when possible, then disposes recorded lists and their target
    /// views/textures before disposing the session. A timed-out submission falls back to device
    /// shutdown to drain the shared command stream.
    /// </summary>
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
                        "Desktop OpenGL benchmark copy command list was still pending during cleanup; device shutdown will drain it."));
                    disposeDeviceFirst = true;
                }
            }

            foreach (IGraphicsCommandList list in _framebufferLists)
            {
                if (list.State != GraphicsCommandListState.Pending)
                    continue;

                if (_fence is null || !Fence.Wait(_fenceValue, GpuTimeout) ||
                    list.State == GraphicsCommandListState.Pending)
                {
                    failures.Add(new TimeoutException(
                        "Desktop OpenGL framebuffer benchmark command list was still pending during cleanup; device shutdown will drain it."));
                    disposeDeviceFirst = true;
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            disposeDeviceFirst = true;
        }

        if (disposeDeviceFirst)
            DisposeSafely(ref _session, failures);

        DisposeFramebufferBenchmarks(failures);
        DisposeSafely(ref _commandList, failures);
        DisposeSafely(ref _fence, failures);
        DisposeSafely(ref _source, failures);
        DisposeSafely(ref _destination, failures);
        DisposeSafely(ref _session, failures);

        if (failures.Count != 0)
            throw new AggregateException("Desktop OpenGL benchmark cleanup encountered one or more failures.", failures);
    }

    private void SetupFramebufferBenchmarks()
    {
        _graphicsQueue = _device.GetQueue(GraphicsQueueType.Graphics);

        if (!_device.Capabilities.SupportsFormat(
                GraphicsFormat.RGBA8UNorm,
                GraphicsFormatSupport.ColorAttachment))
            throw new NotSupportedException("Desktop OpenGL framebuffer benchmarks require RGBA8 color-attachment support.");
        if (!_device.Capabilities.SupportsFormat(
                GraphicsFormat.D16UNorm,
                GraphicsFormatSupport.DepthStencilAttachment))
            throw new NotSupportedException("Desktop OpenGL framebuffer benchmarks require D16 depth-attachment support.");

        IGraphicsTexture colorTextureA = CreateFramebufferTexture(
            GraphicsFormat.RGBA8UNorm, GraphicsTextureUsage.ColorAttachment);
        IGraphicsTexture colorTextureB = CreateFramebufferTexture(
            GraphicsFormat.RGBA8UNorm, GraphicsTextureUsage.ColorAttachment);
        IGraphicsTextureView colorViewA = CreateFramebufferView(colorTextureA);
        IGraphicsTextureView colorViewB = CreateFramebufferView(colorTextureB);

        IGraphicsTexture depthTexture = CreateFramebufferTexture(
            GraphicsFormat.D16UNorm, GraphicsTextureUsage.DepthStencilAttachment);
        IGraphicsTextureView depthView = CreateFramebufferView(depthTexture);

        AddFramebufferCommandList(list =>
        {
            list.SetRenderTargets([colorViewA]);
            list.SetRenderTargets([colorViewA]);
            list.SetRenderTargets([colorViewA]);
        });

        AddFramebufferCommandList(list =>
        {
            list.SetRenderTargets([colorViewA]);
            list.SetRenderTargets([colorViewB]);
            list.SetRenderTargets([colorViewA]);
            list.SetRenderTargets([colorViewB]);
        });

        AddFramebufferCommandList(list =>
        {
            list.SetRenderTargets([colorViewA, colorViewB]);
            list.SetRenderTargets([colorViewA, colorViewB]);
            list.SetRenderTargets([colorViewA, colorViewB]);
        });

        AddFramebufferCommandList(list =>
        {
            list.SetRenderTargets(ReadOnlySpan<IGraphicsTextureView>.Empty, depthView);
            list.SetRenderTargets(ReadOnlySpan<IGraphicsTextureView>.Empty, depthView);
            list.SetRenderTargets(ReadOnlySpan<IGraphicsTextureView>.Empty, depthView);
        });

        AddFramebufferCommandList(list =>
        {
            list.SetRenderTargets([colorViewA], depthView);
            list.SetRenderTargets([colorViewA], depthView);
            list.SetRenderTargets([colorViewA], depthView);
        });
    }

    private IGraphicsTexture CreateFramebufferTexture(
        GraphicsFormat format,
        GraphicsTextureUsage usage)
    {
        IGraphicsTexture texture = _device.CreateTexture(new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            format,
            256, 256, 1, 1, 1, 1,
            usage));
        _framebufferResources.Add(texture);
        return texture;
    }

    private IGraphicsTextureView CreateFramebufferView(IGraphicsTexture texture)
    {
        IGraphicsTextureView view = texture.CreateView(new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown,
            GraphicsTextureViewType.Texture2D,
            0, 1, 0, 1));
        _framebufferResources.Add(view);
        return view;
    }

    private void AddFramebufferCommandList(Action<IGraphicsCommandList> record)
    {
        IGraphicsCommandList list = _graphicsQueue!.CreateCommandList(GraphicsCommandListUsage.Reusable);
        try
        {
            list.Begin();
            record(list);
            list.End();
        }
        catch
        {
            list.Dispose();
            throw;
        }

        _framebufferLists.Add(list);
        _framebufferSubmissions.Add([list]);
    }

    private int SubmitFramebufferWorkload(int workloadIndex)
    {
        IGraphicsCommandQueue queue = _graphicsQueue ??
            throw new InvalidOperationException("The framebuffer benchmark queue is not initialized.");
        ulong value = checked(++_fenceValue);
        queue.Submit(_framebufferSubmissions[workloadIndex], Fence, value);
        if (!Fence.Wait(value, GpuTimeout))
            throw new TimeoutException($"Desktop OpenGL framebuffer workload {workloadIndex} did not complete within {GpuTimeout}.");

        return _framebufferLists[workloadIndex].State == GraphicsCommandListState.Executable ? 1 : 0;
    }

    private void DisposeFramebufferBenchmarks(List<Exception> failures)
    {
        for (int i = _framebufferLists.Count - 1; i >= 0; i--)
        {
            try
            {
                _framebufferLists[i].Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        _framebufferLists.Clear();
        _framebufferSubmissions.Clear();

        for (int i = _framebufferResources.Count - 1; i >= 0; i--)
        {
            try
            {
                _framebufferResources[i].Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        _framebufferResources.Clear();
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
            throw new TimeoutException($"The initial desktop OpenGL buffer-copy fence {value} did not complete.");

        if (CommandList.State != GraphicsCommandListState.Executable)
            throw new InvalidOperationException("The initial desktop OpenGL buffer-copy command list did not complete.");

        Memory<byte> copied = DestinationBuffer.Map(GraphicsMapMode.Read, 0, BufferSize);
        try
        {
            if (!copied.Span.SequenceEqual(_expectedContents))
                throw new InvalidOperationException(
                    "The desktop OpenGL hardware benchmark copy validation failed: destination bytes differ from the source pattern.");
        }
        finally
        {
            DestinationBuffer.Unmap();
        }

        CommandList.Reset();
    }

    private void PrepareList()
    {
        GraphicsCommandListState state = CommandList.State;
        if (state == GraphicsCommandListState.Pending)
        {
            if (!Fence.Wait(_fenceValue, GpuTimeout))
                throw new TimeoutException("A prior desktop OpenGL benchmark submission did not complete within the bounded wait.");

            state = CommandList.State;
            if (state == GraphicsCommandListState.Pending)
                throw new InvalidOperationException(
                    "The desktop OpenGL fence completed but its command queue still reports the reusable list as pending.");
        }

        if (state == GraphicsCommandListState.Executable)
            CommandList.Reset();
        else if (state != GraphicsCommandListState.Initial)
            throw new InvalidOperationException(
                $"Cannot begin desktop OpenGL benchmark recording while the list is in state {state}.");

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
