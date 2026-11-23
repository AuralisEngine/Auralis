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
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.OpenGL.Benchmarks;

/// <summary>
/// Compares the same real 4 KiB GPU buffer-copy workload through Silk.NET's raw OpenGL ES
/// bindings and through Auralis's graphics API. Both paths submit a GPU fence and wait for it.
/// Context/device/resource creation and one-time readback validation are outside measurement.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public sealed unsafe class OpenGLESApiComparisonBenchmarks
{
    private const int BufferSize = 4096;
    private static readonly ulong FenceTimeoutNanoseconds = 15_000_000_000;

    private GlfwOpenGLESHardwareSession? _session;
    private GL? _gl;
    private IGraphicsDevice? _device;
    private IGraphicsCommandQueue? _copyQueue;
    private IGraphicsBuffer? _auralisSource;
    private IGraphicsBuffer? _auralisDestination;
    private IGraphicsCommandList? _commandList;
    private IGraphicsCommandList[] _submitLists = [];
    private IGraphicsFence? _auralisFence;
    private byte[] _sourceBytes = [];
    private uint _nativeSource;
    private uint _nativeDestination;
    private ulong _auralisFenceValue;

    [GlobalSetup]
    /// Creates the hidden hardware context, buffers, command list, and fence, then validates both copy paths before measurement. All of this initialization and readback work is excluded from benchmark iterations.
    public void Setup()
    {
        _session = GlfwOpenGLESHardwareSession.CreateAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            _device = _session.Device;
            _copyQueue = _device.GetQueue(GraphicsQueueType.Copy);
            _sourceBytes = new byte[BufferSize];
            Array.Fill(_sourceBytes, (byte)0x5A);

            _auralisSource = _device.CreateBuffer(new GraphicsBufferDescription(
                BufferSize, GraphicsBufferUsage.CopySource, GraphicsMemoryAccess.CpuToGpu));
            _auralisDestination = _device.CreateBuffer(new GraphicsBufferDescription(
                BufferSize, GraphicsBufferUsage.CopyDestination, GraphicsMemoryAccess.GpuToCpu));
            Memory<byte> mapped = AuralisSource.Map(GraphicsMapMode.Write, 0, BufferSize);
            try { _sourceBytes.AsSpan().CopyTo(mapped.Span); }
            finally { AuralisSource.Unmap(); }

            _commandList = CopyQueue.CreateCommandList(GraphicsCommandListUsage.Reusable);
            _submitLists = [CommandList];
            _auralisFence = _device.CreateFence();

            _session.ContextProvider.MakeCurrent();
            try
            {
                _gl = GL.GetApi(_session.ContextProvider.GetProcAddress);
                _nativeSource = _gl.GenBuffer();
                _nativeDestination = _gl.GenBuffer();

                _gl.BindBuffer(GLEnum.CopyReadBuffer, _nativeSource);
                fixed (byte* source = _sourceBytes)
                    _gl.BufferData(GLEnum.CopyReadBuffer, (nuint)BufferSize, source, GLEnum.StaticDraw);

                _gl.BindBuffer(GLEnum.CopyWriteBuffer, _nativeDestination);
                _gl.BufferData(GLEnum.CopyWriteBuffer, (nuint)BufferSize, null, GLEnum.StreamRead);

                ValidateNativeCopy();
            }
            finally
            {
                _session.ContextProvider.ReleaseCurrent();
            }

            ValidateAuralisCopy();
            Console.WriteLine("Comparison: Silk.NET raw OpenGL ES vs Auralis OpenGL ES");
            Console.WriteLine($"Renderer: {_device.Info.Name}");
            Console.WriteLine($"Driver: {_device.Info.DriverName}");
            Console.WriteLine($"API: {_device.Info.ApiVersion}");
            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}");
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    [Benchmark(Baseline = true, Description = "Silk.NET raw OpenGL ES: 4 KiB copy + GPU fence wait")]
    /// Measures the raw Silk.NET path for one 4 KiB GPU buffer copy, including context activation, native buffer binding, copy submission, fence creation, flush, CPU-side fence polling, sync deletion, and context release. The result is hardware- and driver-dependent.
    public int SilkNetCopyAndWait()
    {
        _session!.ContextProvider.MakeCurrent();
        try
        {
            GL gl = Gl;
            gl.BindBuffer(GLEnum.CopyReadBuffer, _nativeSource);
            gl.BindBuffer(GLEnum.CopyWriteBuffer, _nativeDestination);
            gl.CopyBufferSubData(GLEnum.CopyReadBuffer, GLEnum.CopyWriteBuffer, 0, 0, (nuint)BufferSize);

            nint sync = gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);
            if (sync == 0)
                throw new InvalidOperationException("glFenceSync returned a null sync object.");

            try
            {
                gl.Flush();
                long started = Stopwatch.GetTimestamp();
                while (true)
                {
                    GLEnum result = gl.ClientWaitSync(sync, (SyncObjectMask)1, 1_000_000_000);
                    if (result is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)
                        return 1;
                    if (result == GLEnum.WaitFailed)
                        throw new InvalidOperationException("glClientWaitSync returned GL_WAIT_FAILED.");
                    if (Stopwatch.GetElapsedTime(started).TotalNanoseconds > FenceTimeoutNanoseconds)
                        throw new TimeoutException("Raw OpenGL ES GPU fence did not complete within 15 seconds.");
                }
            }
            finally
            {
                gl.DeleteSync(sync);
            }
        }
        finally
        {
            _session.ContextProvider.ReleaseCurrent();
        }
    }

    [Benchmark(Description = "Auralis OpenGL ES API: 4 KiB copy + GPU fence wait")]
    /// Measures the Auralis path for the same 4 KiB copy, including command-list reset/record/end, queue submission, timeline-fence waiting, command-list retirement observation, and associated context-gate/managed API overhead. It includes GPU completion, not just command recording.
    public int AuralisCopyAndWait()
    {
        IGraphicsCommandList list = CommandList;
        if (list.State == GraphicsCommandListState.Executable)
            list.Reset();
        else if (list.State != GraphicsCommandListState.Initial)
            throw new InvalidOperationException($"Unexpected Auralis command-list state: {list.State}.");

        list.Begin();
        list.CopyBuffer(AuralisSource, 0, AuralisDestination, 0, BufferSize);
        list.End();

        ulong value = checked(++_auralisFenceValue);
        CopyQueue.Submit(_submitLists, AuralisFence, value);
        if (!AuralisFence.Wait(value, TimeSpan.FromSeconds(15)))
            throw new TimeoutException("Auralis OpenGL ES GPU fence did not complete within 15 seconds.");

        EnsureCommandListRetired(list);
        return 1;
    }

    [GlobalCleanup]
    /// Releases the raw OpenGL ES GL objects and Auralis resources outside the measured iterations, aggregating teardown failures after attempting each cleanup.
    public void Cleanup()
    {
        List<Exception> errors = [];
        if (_session is not null && _gl is not null)
        {
            try
            {
                _session.ContextProvider.MakeCurrent();
                try
                {
                    if (_nativeSource != 0) _gl.DeleteBuffer(_nativeSource);
                    if (_nativeDestination != 0) _gl.DeleteBuffer(_nativeDestination);
                }
                finally { _session.ContextProvider.ReleaseCurrent(); }
                _gl.Dispose();
            }
            catch (Exception exception) { errors.Add(exception); }
            _gl = null;
            _nativeSource = 0;
            _nativeDestination = 0;
        }

        DisposeSafely(ref _commandList, errors);
        DisposeSafely(ref _auralisFence, errors);
        DisposeSafely(ref _auralisSource, errors);
        DisposeSafely(ref _auralisDestination, errors);
        DisposeSafely(ref _session, errors);
        if (errors.Count != 0)
            throw new AggregateException("OpenGL ES comparison benchmark cleanup failed.", errors);
    }

    private GL Gl => _gl ?? throw new InvalidOperationException("Silk.NET OpenGL ES is not initialized.");
    private IGraphicsCommandList CommandList => _commandList ?? throw new InvalidOperationException("Auralis command list is not initialized.");
    private IGraphicsFence AuralisFence => _auralisFence ?? throw new InvalidOperationException("Auralis fence is not initialized.");
    private IGraphicsBuffer AuralisSource => _auralisSource ?? throw new InvalidOperationException("Auralis source buffer is not initialized.");
    private IGraphicsBuffer AuralisDestination => _auralisDestination ?? throw new InvalidOperationException("Auralis destination buffer is not initialized.");
    private IGraphicsCommandQueue CopyQueue => _copyQueue ?? throw new InvalidOperationException("Auralis copy queue is not initialized.");

    private void ValidateNativeCopy()
    {
        GL gl = Gl;
        gl.BindBuffer(GLEnum.CopyReadBuffer, _nativeSource);
        gl.BindBuffer(GLEnum.CopyWriteBuffer, _nativeDestination);
        gl.CopyBufferSubData(GLEnum.CopyReadBuffer, GLEnum.CopyWriteBuffer, 0, 0, (nuint)BufferSize);
        nint sync = gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);
        if (sync == 0) throw new InvalidOperationException("Native OpenGL ES validation could not create a fence.");
        try
        {
            gl.Flush();
            GLEnum result = gl.ClientWaitSync(sync, (SyncObjectMask)1, FenceTimeoutNanoseconds);
            if (result is not (GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied))
                throw new InvalidOperationException($"Native OpenGL ES validation fence returned {result}.");
        }
        finally { gl.DeleteSync(sync); }

        void* data = gl.MapBufferRange(GLEnum.CopyWriteBuffer, 0, (nuint)BufferSize, MapBufferAccessMask.ReadBit);
        if (data is null) throw new InvalidOperationException("Native OpenGL ES validation could not map the copy destination.");
        try
        {
            if (!new ReadOnlySpan<byte>(data, BufferSize).SequenceEqual(_sourceBytes))
                throw new InvalidOperationException("Native OpenGL ES GPU buffer-copy validation failed.");
        }
        finally { gl.UnmapBuffer(GLEnum.CopyWriteBuffer); }
    }

    private void ValidateAuralisCopy()
    {
        IGraphicsCommandList list = CommandList;
        list.Begin();
        list.CopyBuffer(AuralisSource, 0, AuralisDestination, 0, BufferSize);
        list.End();
        ulong value = checked(++_auralisFenceValue);
        CopyQueue.Submit(_submitLists, AuralisFence, value);
        if (!AuralisFence.Wait(value, TimeSpan.FromSeconds(15)))
            throw new TimeoutException("Auralis OpenGL ES validation fence timed out.");

        // Fence completion and queue command-list retirement are separate operations. Reading
        // State polls the backend submission queue before Reset is allowed.
        EnsureCommandListRetired(list);

        Memory<byte> result = AuralisDestination.Map(GraphicsMapMode.Read, 0, BufferSize);
        try
        {
            if (!result.Span.SequenceEqual(_sourceBytes))
                throw new InvalidOperationException("Auralis OpenGL ES GPU buffer-copy validation failed.");
        }
        finally { AuralisDestination.Unmap(); }
        list.Reset();
    }

    private static void EnsureCommandListRetired(IGraphicsCommandList commandList)
    {
        // Both GL backends retire pending submissions when State is observed; waiting only on
        // the separate timeline fence does not mutate the command-list lifecycle.
        GraphicsCommandListState state = commandList.State;
        if (state != GraphicsCommandListState.Executable)
            throw new InvalidOperationException(
                $"The Auralis fence completed, but the OpenGL ES queue did not retire the command list. Current state: {state}.");
    }

    private static void DisposeSafely<T>(ref T? resource, List<Exception> errors) where T : class, IDisposable
    {
        T? owned = resource;
        resource = null;
        if (owned is null) return;
        try { owned.Dispose(); }
        catch (Exception exception) { errors.Add(exception); }
    }
}
