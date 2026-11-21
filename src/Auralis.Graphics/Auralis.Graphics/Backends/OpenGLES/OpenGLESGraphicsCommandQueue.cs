using System.Runtime.ExceptionServices;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Ordered logical queue over the externally supplied GLES context. Queue roles share one native command
/// stream; command lists retain all recorded resources until a GLES fence reports completion.
/// </summary>
internal sealed class OpenGLESGraphicsCommandQueue : IGraphicsCommandQueue
{
    private readonly OpenGLESGraphicsDevice _device;
    private readonly object _gate = new();
    private readonly List<PendingSubmission> _pending = new(8);

    internal OpenGLESGraphicsCommandQueue(OpenGLESGraphicsDevice device, GraphicsQueueType queueType)
    {
        _device = device;
        QueueType = queueType;
    }

    public IGraphicsDevice Device => _device;
    public uint Index => 0;
    public GraphicsQueueType QueueType { get; }

    public IGraphicsCommandList CreateCommandList(GraphicsCommandListUsage usage = GraphicsCommandListUsage.Reusable)
    {
        ObjectDisposedException.ThrowIf(_device.IsDisposed, _device);
        if (!Enum.IsDefined(usage))
            throw new ArgumentOutOfRangeException(nameof(usage));
        PollPending();
        return new OpenGLESGraphicsCommandList(this, usage);
    }

    public void Submit(
        ReadOnlySpan<IGraphicsCommandList> commandLists,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0)
    {
        ObjectDisposedException.ThrowIf(_device.IsDisposed, _device);
        if (commandLists.IsEmpty)
            throw new ArgumentException("At least one command list is required.", nameof(commandLists));

        OpenGLESGraphicsCommandList[] lists = new OpenGLESGraphicsCommandList[commandLists.Length];
        for (int i = 0; i < commandLists.Length; i++)
        {
            if (commandLists[i] is not OpenGLESGraphicsCommandList list ||
                !ReferenceEquals(list.Device, _device) ||
                list.QueueType != QueueType ||
                list.Lifecycle.State != GraphicsCommandListState.Executable)
                throw new GraphicsValidationException(
                    "Submitted lists must be executable lists created by this GLES device and queue.");

            for (int j = 0; j < i; j++)
                if (ReferenceEquals(lists[j], list))
                    throw new GraphicsValidationException("A command list cannot be submitted more than once in one batch.");
            lists[i] = list;
        }

        OpenGLESGraphicsFence? fence = null;
        if (signalFence is not null)
        {
            fence = signalFence as OpenGLESGraphicsFence;
            if (fence is null || !ReferenceEquals(fence.Device, _device) || fence.IsDisposed)
                throw new GraphicsValidationException("The signal fence must be live and owned by this GLES device.");
        }
        else if (signalValue != 0)
        {
            throw new ArgumentException("A non-zero signal value requires a signal fence.", nameof(signalValue));
        }

        int submitted = 0;
        bool commandsStarted = false;
        nint completionSync = 0;
        bool queued = false;

        try
        {
            _device.WithContext(gl =>
            {
                for (int i = 0; i < lists.Length; i++)
                {
                    lists[i].MarkSubmitted();
                    submitted++;
                }

                try
                {
                    commandsStarted = true;
                    for (int i = 0; i < lists.Length; i++)
                        lists[i].Execute(gl);

                    completionSync = gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, (uint)0);
                    if (completionSync == 0)
                        throw new GraphicsException("OpenGL ES failed to create a command-submission sync object.");

                    fence?.SignalFromGpu(gl, signalValue);
                    gl.Flush();

                    lock (_gate)
                    {
                        _pending.Add(new PendingSubmission(lists, completionSync));
                        queued = true;
                    }
                }
                catch
                {
                    if (commandsStarted)
                        gl.Finish();
                    if (completionSync != 0)
                    {
                        gl.DeleteSync(completionSync);
                        completionSync = 0;
                    }
                    throw;
                }
            });
        }
        catch (Exception submissionError)
        {
            Exception? completionError = null;
            if (!queued)
            {
                // Execute has already forced the command stream idle on failure. Release the
                // retained command-list resources only after that point.
                for (int i = 0; i < submitted; i++)
                {
                    try { lists[i].Complete(); }
                    catch (Exception exception) { completionError ??= exception; }
                }
            }

            if (completionError is not null)
                throw new AggregateException(
                    "GLES submission failed and command-list cleanup also failed.",
                    submissionError,
                    completionError);
            ExceptionDispatchInfo.Capture(submissionError).Throw();
            throw;
        }
    }

    public void Wait(IGraphicsFence fence, ulong value)
    {
        ObjectDisposedException.ThrowIf(_device.IsDisposed, _device);
        if (fence is not OpenGLESGraphicsFence glesFence ||
            !ReferenceEquals(glesFence.Device, _device))
            throw new GraphicsValidationException("The wait fence must belong to this GLES device.");
        _device.WithContext(gl => glesFence.EnqueueGpuWait(gl, value));
    }

    internal void PollPending()
    {
        if (_device.IsDisposed)
            return;

        List<OpenGLESGraphicsCommandList>? completed = null;
        _device.WithContext(gl =>
        {
            lock (_gate)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    PendingSubmission submission = _pending[i];
                    GLEnum status = gl.ClientWaitSync(submission.Sync, (uint)0, 0);
                    if (status == GLEnum.TimeoutExpired)
                        continue;
                    if (status == GLEnum.WaitFailed)
                        throw new GraphicsException("OpenGL ES failed while polling a command submission.");

                    if (status is not (GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied))
                        throw new GraphicsException($"OpenGL ES returned unexpected sync status {status}.");

                    gl.DeleteSync(submission.Sync);
                    _pending.RemoveAt(i);
                    completed ??= new List<OpenGLESGraphicsCommandList>(4);
                    completed.AddRange(submission.CommandLists);
                }
            }
        });

        if (completed is null)
            return;
        for (int i = 0; i < completed.Count; i++)
            completed[i].Complete();
    }

    internal void WaitForCompletion(OpenGLESGraphicsCommandList commandList)
    {
        while (commandList.Lifecycle.State == GraphicsCommandListState.Pending)
        {
            bool foundPending = false;
            OpenGLESGraphicsCommandList[]? completedLists = null;

            _device.WithContext(gl =>
            {
                lock (_gate)
                {
                    for (int i = _pending.Count - 1; i >= 0; i--)
                    {
                        PendingSubmission submission = _pending[i];
                        bool contains = false;
                        for (int j = 0; j < submission.CommandLists.Length; j++)
                            contains |= ReferenceEquals(submission.CommandLists[j], commandList);
                        if (!contains)
                            continue;

                        foundPending = true;
                        GLEnum status = gl.ClientWaitSync(submission.Sync, (SyncObjectMask)SyncObjectMask.Bit, 1_000_000);
                        if (status == GLEnum.WaitFailed)
                            throw new GraphicsException("OpenGL ES failed while waiting for command-list completion.");
                        if (status is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)
                        {
                            gl.DeleteSync(submission.Sync);
                            _pending.RemoveAt(i);
                            completedLists = submission.CommandLists;
                        }
                        break;
                    }
                }
            });

            if (!foundPending && commandList.Lifecycle.State == GraphicsCommandListState.Pending)
                throw new GraphicsException("The GLES command list is pending but has no owning queue submission.");

            if (completedLists is not null)
            {
                for (int i = 0; i < completedLists.Length; i++)
                    completedLists[i].Complete();
            }
        }
    }

    /// <summary>Called with the owning context current after the device has finished the GPU.</summary>
    internal void DrainUnderContext(GL gl)
    {
        List<OpenGLESGraphicsCommandList>? completed = null;
        lock (_gate)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingSubmission submission = _pending[i];
                gl.DeleteSync(submission.Sync);
                completed ??= new List<OpenGLESGraphicsCommandList>(4);
                completed.AddRange(submission.CommandLists);
            }
            _pending.Clear();
        }

        if (completed is null)
            return;
        for (int i = 0; i < completed.Count; i++)
            completed[i].Complete();
    }

    private readonly record struct PendingSubmission(
        OpenGLESGraphicsCommandList[] CommandLists,
        nint Sync);
}
