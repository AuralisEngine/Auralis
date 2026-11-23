using System.Buffers;
using System.Runtime.ExceptionServices;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Logical queue over one ordered OpenGL context command stream. Graphics, compute and copy roles
/// share execution serialization; submitted command-list references are pooled until GPU completion.
/// </summary>
internal sealed class OpenGLGraphicsCommandQueue : IGraphicsCommandQueue
{
    private readonly OpenGLGraphicsDevice _device;
    private readonly Lock _gate = new();
    private readonly List<PendingSubmission> _pending = new(8);

    internal OpenGLGraphicsCommandQueue(OpenGLGraphicsDevice device, GraphicsQueueType queueType)
    {
        _device = device;
        QueueType = queueType;
    }

    public IGraphicsDevice Device => _device;
    public uint Index => 0;
    public GraphicsQueueType QueueType { get; }

    /// <inheritdoc />
    /// <remarks>The returned list records commands on the CPU; it does not make the GL context current until submission or another device operation.</remarks>
    public IGraphicsCommandList CreateCommandList(GraphicsCommandListUsage usage = GraphicsCommandListUsage.Reusable)
    {
        ObjectDisposedException.ThrowIf(_device.IsDisposed, _device);
        if (!Enum.IsDefined(usage))
            throw new ArgumentOutOfRangeException(nameof(usage));
        PollPending();
        return new OpenGLGraphicsCommandList(this, usage);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Lists are validated as an entire batch before they are marked pending. Execution occurs on
    /// the device's single serialized context stream. The queue retains each list and its recorded
    /// resources until its submission sync is observed complete and queue retirement runs.
    /// A signal fence is optional; its GPU signal is inserted after the batch's recorded commands.
    /// </remarks>
    public void Submit(
        ReadOnlySpan<IGraphicsCommandList> commandLists,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0)
    {
        ObjectDisposedException.ThrowIf(_device.IsDisposed, _device);
        if (commandLists.IsEmpty)
            throw new ArgumentException("At least one command list is required.", nameof(commandLists));

        // Validate without allocating. This duplicate scan is bounded by the submission size.
        for (int i = 0; i < commandLists.Length; i++)
        {
            if (commandLists[i] is not OpenGLGraphicsCommandList list ||
                !ReferenceEquals(list.Device, _device) ||
                list.QueueType != QueueType ||
                list.Lifecycle.State != GraphicsCommandListState.Executable)
            {
                throw new GraphicsValidationException(
                    "Submitted command lists must be executable lists created by this device and queue.");
            }

            for (int previous = 0; previous < i; previous++)
            {
                if (ReferenceEquals(commandLists[previous], list))
                    throw new GraphicsValidationException("A command list cannot appear more than once in one submission.");
            }
        }

        OpenGLGraphicsFence? fence = null;
        if (signalFence is not null)
        {
            fence = signalFence as OpenGLGraphicsFence;
            if (fence is null || !ReferenceEquals(fence.Device, _device) || fence.IsDisposed)
                throw new GraphicsValidationException("The signal fence must be live and owned by this OpenGL device.");
        }
        else if (signalValue != 0)
        {
            throw new ArgumentException("A non-zero signal value requires a signal fence.", nameof(signalValue));
        }

        int listCount = commandLists.Length;
        OpenGLGraphicsCommandList[] lists = ArrayPool<OpenGLGraphicsCommandList>.Shared.Rent(commandLists.Length);
        for (int i = 0; i < commandLists.Length; i++)
            lists[i] = (OpenGLGraphicsCommandList)commandLists[i];

        nint completionSync = 0;
        int submittedCount = 0;
        bool commandsIssued = false;
        bool submissionQueued = false;
        bool submissionCounted = false;
        try
        {
            using OpenGLContextScope context = _device.AcquireContext();
            GL gl = context.Gl;
            {
                // Serialize the Executable -> Pending transition with command-list disposal.
                // If disposal wins the context gate, it marks the list disposed and this
                // transition fails before any GL command is issued. If submission wins, disposal
                // observes a genuinely in-flight list and waits for its completion.
                for (int i = 0; i < listCount; i++)
                {
                    lists[i].MarkSubmitted();
                    submittedCount++;
                }

                bool pushedDebugGroup = false;
                try
                {
                    if (_device.DebugLayerEnabled)
                    {
                        _device.PushDebugGroup(
                            GLEnum.DebugSourceApplication,
                            (uint)QueueType,
                            "Auralis command queue submission");
                        pushedDebugGroup = true;
                    }

                    commandsIssued = true;
                    for (int i = 0; i < listCount; i++)
                        lists[i].Execute(
                            gl,
                            _device.DirectStateAccess,
                            _device.CopyImageApi,
                            _device.ViewportApi,
                            _device.BaseInstanceApi,
                            _device.DrawIndirectApi,
                            _device.MultiDrawIndirectApi,
                            _device.ImageLoadStoreApi,
                            _device.DrawBuffersBlendApi,
                            _device.ComputeShaderApi,
                            _device.MeshShaderApi,
                            _device.NvMeshShaderApi);

                    completionSync = _device.SyncApi.FenceSync(
                        SyncCondition.SyncGpuCommandsComplete,
                        SyncBehaviorFlags.None);
                    if (completionSync == 0)
                        throw new GraphicsException("OpenGL failed to create a submission completion sync.");

                    if (fence is not null)
                        fence.SignalFromGpu(gl, signalValue);

                    // Make the completion sync observable to nonblocking ClientWaitSync polling
                    // even when the application supplies no fence or performs no later GL calls.
                    gl.Flush();

                    lock (_gate)
                    {
                        // Publish the owning record before incrementing the count. If record
                        // allocation fails, the failed-submit orphan path has no count to balance.
                        _pending.Add(new PendingSubmission(lists, listCount, completionSync));
                        _device.OnSubmissionQueued();
                        submissionCounted = true;
                        submissionQueued = true;
                    }
                }
                finally
                {
                    if (pushedDebugGroup)
                        _device.PopDebugGroup();
                }
            }
        }
        catch (Exception submissionException)
        {
            // Once queued, the pending-submission record owns the pooled list array and lifecycle
            // transitions. A later ReleaseCurrent failure must not roll back that accepted work
            // or complete/return the same entries twice.
            Exception? cleanupFailure = null;
            if (!submissionQueued)
            {
                bool executionFinished = !commandsIssued;
                if (commandsIssued)
                {
                    try
                    {
                        if (_device.IsDisposed)
                        {
                            // Device shutdown owns the context gate and performs Finish/drain before
                            // releasing registered resources. Wait until that teardown is complete.
                            _device.WaitForShutdownCompletion();
                        }
                        else
                        {
                            using (OpenGLContextScope context = _device.AcquireContext())
                            {
                                context.Gl.Finish();
                                if (completionSync != 0)
                                    _device.SyncApi.DeleteSync(completionSync);
                            }
                        }

                        executionFinished = true;
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }
                else if (completionSync != 0 && !_device.IsDisposed)
                {
                    try
                    {
                        using OpenGLContextScope context = _device.AcquireContext();
                        _device.SyncApi.DeleteSync(completionSync);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }

                // Never decrement submission accounting or release a command list's resources
                // unless issued GPU work is known to have finished.
                if (executionFinished)
                {
                    if (submissionCounted)
                    {
                        try
                        {
                            if (_device.IsDisposed)
                                _device.OnSubmissionCompleted();
                            else
                            {
                                using OpenGLContextScope context = _device.AcquireContext();
                                _device.OnSubmissionCompleted();
                            }
                            submissionCounted = false;
                        }
                        catch (Exception exception)
                        {
                            cleanupFailure ??= exception;
                            submissionCounted = false;
                        }
                    }

                    for (int i = 0; i < submittedCount; i++)
                    {
                        if (lists[i].Lifecycle.State != GraphicsCommandListState.Pending)
                            continue;

                        try
                        {
                            lists[i].Complete();
                        }
                        catch (Exception exception)
                        {
                            cleanupFailure ??= exception;
                        }
                    }
                }
            }

            if (cleanupFailure is not null)
                throw new AggregateException(
                    "OpenGL command submission failed and one or more cleanup operations also failed.",
                    submissionException,
                    cleanupFailure);

            ExceptionDispatchInfo.Capture(submissionException).Throw();
            throw;
        }
        finally
        {
            if (!submissionQueued)
                ArrayPool<OpenGLGraphicsCommandList>.Shared.Return(lists, clearArray: true);
        }
    }

    /// <inheritdoc />
    /// <remarks>Enqueues a server-side wait only for a value already represented by a known GL sync or already completed CPU signal; an unknown future sync value is rejected.</remarks>
    public void Wait(IGraphicsFence fence, ulong value)
    {
        ObjectDisposedException.ThrowIf(_device.IsDisposed, _device);
        if (fence is not OpenGLGraphicsFence openGlFence ||
            !ReferenceEquals(openGlFence.Device, _device))
            throw new GraphicsValidationException("The wait fence must be an OpenGL fence owned by this device.");

        using OpenGLContextScope context = _device.AcquireContext();
        openGlFence.EnqueueGpuWait(context.Gl, value);
    }

    internal void PollPending()
    {
        if (_device.IsDisposed)
            return;

        PendingSubmission[]? completed = null;
        int completedCount = 0;
        Exception? pollFailure = null;
        try
        {
            using OpenGLContextScope context = _device.AcquireContext();
            GL gl = context.Gl;
            {
                lock (_gate)
                {
                    for (int i = _pending.Count - 1; i >= 0; i--)
                    {
                        PendingSubmission submission = _pending[i];
                        ARB status = _device.SyncApi.ClientWaitSync(
                            submission.Sync,
                            (SyncObjectMask)0,
                            0);

                        if (status == ARB.TimeoutExpired)
                            continue;
                        if (status == ARB.WaitFailed)
                            throw new GraphicsException("OpenGL failed to poll a command submission sync.");
                        if (status is not (ARB.AlreadySignaled or ARB.ConditionSatisfied))
                            throw new GraphicsException($"OpenGL returned unexpected command submission sync status {status}.");

                        // Rent before mutating queue ownership so an allocation failure leaves the
                        // native sync and its pending record available for a later poll.
                        completed ??= ArrayPool<PendingSubmission>.Shared.Rent(_pending.Count);

                        try { _device.SyncApi.DeleteSync(submission.Sync); }
                        catch (Exception exception) { pollFailure ??= exception; }

                        try { MarkSubmissionCompleted(submission); }
                        catch (Exception exception) { pollFailure ??= exception; }

                        _pending.RemoveAt(i);
                        completed[completedCount++] = submission;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            pollFailure ??= exception;
        }

        Exception? completionFailure = null;
        try
        {
            for (int i = 0; i < completedCount; i++)
            {
                try { CompleteSubmission(completed![i]); }
                catch (Exception exception) { completionFailure ??= exception; }
            }
        }
        finally
        {
            if (completed is not null)
                ArrayPool<PendingSubmission>.Shared.Return(completed, clearArray: true);
        }

        if (pollFailure is not null && completionFailure is not null)
            throw new AggregateException(
                "OpenGL submission polling and completion cleanup both reported failures.",
                pollFailure,
                completionFailure);
        if (pollFailure is not null)
            ExceptionDispatchInfo.Capture(pollFailure).Throw();
        if (completionFailure is not null)
            ExceptionDispatchInfo.Capture(completionFailure).Throw();
    }

    internal void WaitForCompletion(OpenGLGraphicsCommandList commandList)
    {
        if (_device.IsDisposed || commandList.Lifecycle.State != GraphicsCommandListState.Pending)
            return;

        while (commandList.Lifecycle.State == GraphicsCommandListState.Pending)
        {
            PendingSubmission completedSubmission = default;
            bool hasCompletedSubmission = false;
            bool recoveredUnqueuedSubmission = false;

            {
                using OpenGLContextScope context = _device.AcquireContext();
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

                            // Wait only for the submission that owns this command list. The bounded
                            // wait returns the context to other device callers between iterations.
                            ARB status = _device.SyncApi.ClientWaitSync(
                                submission.Sync,
                                (SyncObjectMask)0,
                                1_000_000);
                            if (status == ARB.WaitFailed)
                                throw new GraphicsException("OpenGL failed while waiting for command-list completion.");
                            if (status is not (ARB.AlreadySignaled or ARB.ConditionSatisfied or ARB.TimeoutExpired))
                                throw new GraphicsException($"OpenGL returned unexpected command-list sync status {status}.");

                            if (status is ARB.AlreadySignaled or ARB.ConditionSatisfied)
                            {
                                _device.SyncApi.DeleteSync(submission.Sync);
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
                        // The submission may have failed after marking Pending but before queueing its
                        // record. This exceptional path finishes the stream but leaves unrelated queue
                        // records intact; only the orphaned list is recovered here.
                        gl.Finish();
                        if (commandList.Lifecycle.State == GraphicsCommandListState.Pending)
                            commandList.MarkCompletedAfterGpu();
                        recoveredUnqueuedSubmission = true;
                    }
                }
            }

            if (hasCompletedSubmission)
                CompleteSubmission(completedSubmission);
            else if (recoveredUnqueuedSubmission)
                commandList.ReleaseCompletedResourcesIfDisposed();
        }
    }

    internal void DrainUnderContext(GL gl)
    {
        PendingSubmission[]? completed = null;
        int completedCount = 0;
        Exception? failure = null;

        lock (_gate)
        {
            if (_pending.Count != 0)
                completed = ArrayPool<PendingSubmission>.Shared.Rent(_pending.Count);

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingSubmission submission = _pending[i];
                try { _device.SyncApi.DeleteSync(submission.Sync); }
                catch (Exception exception) { failure ??= exception; }

                try { MarkSubmissionCompleted(submission); }
                catch (Exception exception) { failure ??= exception; }

                _pending.RemoveAt(i);
                completed![completedCount++] = submission;
            }
        }

        try
        {
            for (int i = 0; i < completedCount; i++)
            {
                try { CompleteSubmission(completed![i]); }
                catch (Exception exception) { failure ??= exception; }
            }
        }
        finally
        {
            if (completed is not null)
                ArrayPool<PendingSubmission>.Shared.Return(completed, clearArray: true);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
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

    private void CompleteSubmission(PendingSubmission submission)
    {
        Exception? failure = null;
        for (int i = 0; i < submission.Count; i++)
        {
            try
            {
                submission.CommandLists[i].ReleaseCompletedResourcesIfDisposed();
            }
            catch (Exception exception)
            {
                // Continue releasing each disposed list and balance queue accounting even when one
                // resource wrapper reports a cleanup error.
                failure ??= exception;
            }
        }

        ArrayPool<OpenGLGraphicsCommandList>.Shared.Return(
            submission.CommandLists,
            clearArray: true);

        try
        {
            _device.OnSubmissionCompleted();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private readonly record struct PendingSubmission(
        OpenGLGraphicsCommandList[] CommandLists,
        int Count,
        nint Sync);
}
