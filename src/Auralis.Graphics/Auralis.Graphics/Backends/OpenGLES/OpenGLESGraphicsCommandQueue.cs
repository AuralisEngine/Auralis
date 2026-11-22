using System.Buffers;
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
    private readonly Lock _gate = new();
    private readonly List<PendingSubmission> _pending = new(8);

    internal OpenGLESGraphicsCommandQueue(OpenGLESGraphicsDevice device, GraphicsQueueType queueType)
    {
        _device = device;
        QueueType = queueType;
    }

    public IGraphicsDevice Device => _device;
    internal OpenGLESGraphicsDevice GlesDevice => _device;
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

        // Validate the input span before renting storage. Duplicate detection does not need a
        // temporary array and remains bounded by the submission size.
        for (int i = 0; i < commandLists.Length; i++)
        {
            if (commandLists[i] is not OpenGLESGraphicsCommandList list ||
                !ReferenceEquals(list.Device, _device) ||
                list.QueueType != QueueType ||
                list.Lifecycle.State != GraphicsCommandListState.Executable)
                throw new GraphicsValidationException(
                    "Submitted lists must be executable lists created by this GLES device and queue.");

            for (int j = 0; j < i; j++)
                if (ReferenceEquals(commandLists[j], list))
                    throw new GraphicsValidationException("A command list cannot be submitted more than once in one batch.");
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

        int listCount = commandLists.Length;
        OpenGLESGraphicsCommandList[] lists =
            ArrayPool<OpenGLESGraphicsCommandList>.Shared.Rent(listCount);
        for (int i = 0; i < listCount; i++)
            lists[i] = (OpenGLESGraphicsCommandList)commandLists[i];

        int submitted = 0;
        bool commandsStarted = false;
        bool submissionQueued = false;
        nint completionSync = 0;

        try
        {
            using OpenGLESContextScope context = _device.AcquireContext();
            GL gl = context.Gl;
            {
                try
                {
                    // Keep Executable -> Pending and failure recovery serialized with Dispose.
                    for (int i = 0; i < listCount; i++)
                    {
                        lists[i].MarkSubmitted();
                        submitted++;
                    }

                    commandsStarted = true;
                    for (int i = 0; i < listCount; i++)
                        lists[i].Execute(gl);

                    completionSync = gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, (uint)0);
                    if (completionSync == 0)
                        throw new GraphicsException("OpenGL ES failed to create a command-submission sync object.");

                    fence?.SignalFromGpu(gl, signalValue);
                    gl.Flush();

                    lock (_gate)
                    {
                        // Ownership transfers only after the pending record is successfully added.
                        _pending.Add(new PendingSubmission(lists, listCount, completionSync));
                        submissionQueued = true;
                        completionSync = 0;
                    }
                }
                catch (Exception submissionError)
                {
                    // A disposer waiting on the context gate observes either an owning queue record
                    // or command lists whose issued GPU work has been safely finished.
                    Exception? cleanupFailure = null;
                    try
                    {
                        GraphicsCommandListSubmissionRecovery.RecoverFailedSubmission(
                            lists,
                            submitted,
                            commandsStarted,
                            gl.Finish,
                            static list =>
                            {
                                if (list.Lifecycle.State == GraphicsCommandListState.Pending)
                                    list.MarkCompletedAfterGpu();
                            });
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }

                    if (completionSync != 0)
                    {
                        try { gl.DeleteSync(completionSync); }
                        catch (Exception exception) { cleanupFailure ??= exception; }
                        completionSync = 0;
                    }

                    if (cleanupFailure is not null)
                        throw new AggregateException(
                            "GLES submission failed and its in-context recovery also failed.",
                            submissionError,
                            cleanupFailure);

                    ExceptionDispatchInfo.Capture(submissionError).Throw();
                    throw;
                }
            }
        }
        finally
        {
            if (!submissionQueued)
                ArrayPool<OpenGLESGraphicsCommandList>.Shared.Return(lists, clearArray: true);
        }
    }

    public void Wait(IGraphicsFence fence, ulong value)
    {
        ObjectDisposedException.ThrowIf(_device.IsDisposed, _device);
        if (fence is not OpenGLESGraphicsFence glesFence ||
            !ReferenceEquals(glesFence.Device, _device))
            throw new GraphicsValidationException("The wait fence must belong to this GLES device.");
        using OpenGLESContextScope context = _device.AcquireContext();
        glesFence.EnqueueGpuWait(context.Gl, value);
    }

    internal void PollPending()
    {
        if (_device.IsDisposed)
            return;

        PendingSubmission[]? completed = null;
        int completedCount = 0;
        Exception? pollingFailure = null;
        try
        {
            using OpenGLESContextScope context = _device.AcquireContext();
            GL gl = context.Gl;
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

                        // The pool rent happens before ownership is mutated. A rent failure leaves
                        // this record and its live sync in the pending queue.
                        completed ??= ArrayPool<PendingSubmission>.Shared.Rent(_pending.Count);

                        try { gl.DeleteSync(submission.Sync); }
                        catch (Exception exception) { pollingFailure ??= exception; }

                        try { MarkSubmissionCompleted(submission); }
                        catch (Exception exception) { pollingFailure ??= exception; }

                        _pending.RemoveAt(i);
                        completed[completedCount++] = submission;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            pollingFailure ??= exception;
        }

        Exception? completionFailure = null;
        try
        {
            if (completedCount != 0)
            {
                try { CompleteAll(completed!, completedCount); }
                catch (Exception exception) { completionFailure = exception; }
            }
        }
        finally
        {
            if (completed is not null)
                ArrayPool<PendingSubmission>.Shared.Return(completed, clearArray: true);
        }

        if (pollingFailure is not null && completionFailure is not null)
            throw new AggregateException(
                "OpenGL ES submission polling and completion cleanup both reported failures.",
                pollingFailure,
                completionFailure);
        if (pollingFailure is not null)
            ExceptionDispatchInfo.Capture(pollingFailure).Throw();
        if (completionFailure is not null)
            ExceptionDispatchInfo.Capture(completionFailure).Throw();
    }

    internal void WaitForCompletion(OpenGLESGraphicsCommandList commandList)
    {
        while (commandList.Lifecycle.State == GraphicsCommandListState.Pending)
        {
            PendingSubmission completedSubmission = default;
            bool hasCompletedSubmission = false;
            bool recoveredUnqueuedSubmission = false;

            using OpenGLESContextScope context = _device.AcquireContext();
            GL gl = context.Gl;
            bool foundSubmission = false;
            {
                lock (_gate)
                {
                    for (int i = _pending.Count - 1; i >= 0; i--)
                    {
                        PendingSubmission submission = _pending[i];
                        bool contains = false;
                        for (int j = 0; j < submission.Count; j++)
                            contains |= ReferenceEquals(submission.CommandLists[j], commandList);
                        if (!contains)
                            continue;

                        foundSubmission = true;

                        GLEnum status = gl.ClientWaitSync(
                            submission.Sync,
                            (SyncObjectMask)SyncObjectMask.Bit,
                            1_000_000);
                        if (status == GLEnum.WaitFailed)
                            throw new GraphicsException("OpenGL ES failed while waiting for command-list completion.");
                        if (status is not (GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied or GLEnum.TimeoutExpired))
                            throw new GraphicsException($"OpenGL ES returned unexpected sync status {status}.");

                        if (status is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)
                        {
                            gl.DeleteSync(submission.Sync);
                            MarkSubmissionCompleted(submission);
                            _pending.RemoveAt(i);
                            completedSubmission = submission;
                            hasCompletedSubmission = true;
                        }

                        break;
                    }
                }

                if (!foundSubmission && commandList.Lifecycle.State == GraphicsCommandListState.Pending)
                {
                    // This is the exceptional no-record recovery path after submission failure.
                    // Do not drain unrelated queue records.
                    gl.Finish();
                    if (commandList.Lifecycle.State == GraphicsCommandListState.Pending)
                        commandList.MarkCompletedAfterGpu();
                    recoveredUnqueuedSubmission = true;
                }
            }

            if (hasCompletedSubmission)
                CompleteSubmission(completedSubmission);
            else if (recoveredUnqueuedSubmission)
                commandList.ReleaseCompletedResourcesIfDisposed();
        }
    }

    private static void MarkSubmissionCompleted(PendingSubmission submission)
    {
        Exception? failure = null;
        for (int i = 0; i < submission.Count; i++)
        {
            try { submission.CommandLists[i].MarkCompletedAfterGpu(); }
            catch (Exception exception) { failure ??= exception; }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Called with the owning context current after the device has finished the GPU.</summary>
    internal void DrainUnderContext(GL gl)
    {
        PendingSubmission[]? completed = null;
        int completedCount = 0;
        Exception? drainFailure = null;

        lock (_gate)
        {
            if (_pending.Count != 0)
                completed = ArrayPool<PendingSubmission>.Shared.Rent(_pending.Count);

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingSubmission submission = _pending[i];
                try { gl.DeleteSync(submission.Sync); }
                catch (Exception exception) { drainFailure ??= exception; }

                try { MarkSubmissionCompleted(submission); }
                catch (Exception exception) { drainFailure ??= exception; }

                _pending.RemoveAt(i);
                completed![completedCount++] = submission;
            }
        }

        Exception? completionFailure = null;
        try
        {
            if (completedCount != 0)
            {
                try { CompleteAll(completed!, completedCount); }
                catch (Exception exception) { completionFailure = exception; }
            }
        }
        finally
        {
            if (completed is not null)
                ArrayPool<PendingSubmission>.Shared.Return(completed, clearArray: true);
        }

        if (drainFailure is not null && completionFailure is not null)
            throw new AggregateException(
                "OpenGL ES submission draining and resource cleanup both reported failures.",
                drainFailure,
                completionFailure);
        if (drainFailure is not null)
            ExceptionDispatchInfo.Capture(drainFailure).Throw();
        if (completionFailure is not null)
            ExceptionDispatchInfo.Capture(completionFailure).Throw();
    }

    private static void CompleteAll(PendingSubmission[] submissions, int count)
    {
        List<Exception>? failures = null;
        for (int i = 0; i < count; i++)
        {
            try { CompleteSubmission(submissions[i]); }
            catch (Exception exception)
            {
                failures ??= new List<Exception>();
                failures.Add(exception);
            }
        }

        if (failures is not null)
            throw new AggregateException(
                "One or more GLES submissions failed to release retained resources after GPU completion.",
                failures);
    }

    private static void CompleteSubmission(PendingSubmission submission)
    {
        Exception? failure = null;
        try
        {
            for (int i = 0; i < submission.Count; i++)
            {
                try { submission.CommandLists[i].ReleaseCompletedResourcesIfDisposed(); }
                catch (Exception exception) { failure ??= exception; }
            }
        }
        finally
        {
            ArrayPool<OpenGLESGraphicsCommandList>.Shared.Return(
                submission.CommandLists,
                clearArray: true);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private readonly record struct PendingSubmission(
        OpenGLESGraphicsCommandList[] CommandLists,
        int Count,
        nint Sync);
}
