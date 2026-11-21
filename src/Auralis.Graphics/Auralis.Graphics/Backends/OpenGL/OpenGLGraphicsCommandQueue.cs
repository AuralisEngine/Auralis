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
    private readonly object _gate = new();
    private readonly List<PendingSubmission> _pending = new(8);

    internal OpenGLGraphicsCommandQueue(OpenGLGraphicsDevice device, GraphicsQueueType queueType)
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
        return new OpenGLGraphicsCommandList(this, usage);
    }

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
            if (fence is null || !ReferenceEquals(fence.Device, _device))
                throw new GraphicsValidationException("The signal fence must be an OpenGL fence owned by this device.");
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
        try
        {
            _device.WithContext(gl =>
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
                            _device.MeshShaderApi);

                    completionSync = _device.SyncApi.FenceSync(
                        SyncCondition.SyncGpuCommandsComplete,
                        SyncBehaviorFlags.None);
                    if (completionSync == 0)
                        throw new GraphicsException("OpenGL failed to create a submission completion sync.");

                    if (fence is not null)
                        fence.SignalFromGpu(gl, signalValue);

                    _device.OnSubmissionQueued();
                    try
                    {
                        lock (_gate)
                        {
                            _pending.Add(new PendingSubmission(lists, listCount, completionSync));
                            submissionQueued = true;
                        }
                    }
                    catch
                    {
                        _device.OnSubmissionCompleted();
                        throw;
                    }
                }
                finally
                {
                    if (pushedDebugGroup)
                        _device.PopDebugGroup();
                }
            });
        }
        catch
        {
            // Once queued, the pending-submission record owns the pooled list array and lifecycle
            // transitions. A later ReleaseCurrent failure must not roll back that accepted work
            // or complete/return the same entries twice.
            if (!submissionQueued)
            {
                if (commandsIssued && !_device.IsDisposed)
                {
                    _device.WithContext(gl =>
                    {
                        gl.Finish();
                        if (completionSync != 0)
                            _device.SyncApi.DeleteSync(completionSync);
                    });
                }
                else if (completionSync != 0 && !_device.IsDisposed)
                {
                    _device.WithContext(gl => _device.SyncApi.DeleteSync(completionSync));
                }

                for (int i = 0; i < submittedCount; i++)
                {
                    if (lists[i].Lifecycle.State == GraphicsCommandListState.Pending)
                        lists[i].Complete();
                }
            }

            throw;
        }
        finally
        {
            if (!submissionQueued)
                ArrayPool<OpenGLGraphicsCommandList>.Shared.Return(lists, clearArray: true);
        }
    }

    public void Wait(IGraphicsFence fence, ulong value)
    {
        ObjectDisposedException.ThrowIf(_device.IsDisposed, _device);
        if (fence is not OpenGLGraphicsFence openGlFence ||
            !ReferenceEquals(openGlFence.Device, _device))
            throw new GraphicsValidationException("The wait fence must be an OpenGL fence owned by this device.");

        _device.WithContext(gl => openGlFence.EnqueueGpuWait(gl, value));
    }

    internal void PollPending()
    {
        if (_device.IsDisposed)
            return;

        _device.WithContext(gl =>
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

                    Exception? failure = null;
                    try
                    {
                        _device.SyncApi.DeleteSync(submission.Sync);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }

                    try
                    {
                        CompleteSubmission(submission);
                    }
                    catch (Exception exception)
                    {
                        failure ??= exception;
                    }

                    _pending.RemoveAt(i);
                    if (failure is not null)
                        ExceptionDispatchInfo.Capture(failure).Throw();
                }
            }
        });
    }

    internal void WaitForCompletion(OpenGLGraphicsCommandList commandList)
    {
        if (_device.IsDisposed || commandList.Lifecycle.State != GraphicsCommandListState.Pending)
            return;

        _device.WithContext(gl =>
        {
            gl.Finish();
            Exception? failure = null;
            lock (_gate)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    PendingSubmission submission = _pending[i];
                    try
                    {
                        _device.SyncApi.DeleteSync(submission.Sync);
                    }
                    catch (Exception exception)
                    {
                        failure ??= exception;
                    }

                    try
                    {
                        CompleteSubmission(submission);
                    }
                    catch (Exception exception)
                    {
                        failure ??= exception;
                    }

                    _pending.RemoveAt(i);
                }

                // A submit callback can mark a list Pending and then fail before it successfully
                // inserts its completion record. The context gate guarantees its GL work has exited;
                // glFinish above guarantees that work is complete, so close that unqueued lifecycle
                // transition here rather than leaving a disposed list permanently Pending.
                if (commandList.Lifecycle.State == GraphicsCommandListState.Pending)
                {
                    try
                    {
                        commandList.Complete();
                    }
                    catch (Exception exception)
                    {
                        failure ??= exception;
                    }
                }
            }

            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        });
    }

    internal void DrainUnderContext(GL gl)
    {
        Exception? failure = null;
        lock (_gate)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingSubmission submission = _pending[i];
                try
                {
                    _device.SyncApi.DeleteSync(submission.Sync);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

                try
                {
                    CompleteSubmission(submission);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

                _pending.RemoveAt(i);
            }
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
                submission.CommandLists[i].Complete();
            }
            catch (Exception exception)
            {
                // Continue completing the rest of the submission so one faulty list cannot
                // strand other retained resources or corrupt global submission accounting.
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
