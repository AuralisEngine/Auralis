using System.Buffers;
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

        OpenGLGraphicsCommandList[] lists = ArrayPool<OpenGLGraphicsCommandList>.Shared.Rent(commandLists.Length);
        for (int i = 0; i < commandLists.Length; i++)
            lists[i] = (OpenGLGraphicsCommandList)commandLists[i];

        nint completionSync = 0;
        int submittedCount = 0;
        bool commandsIssued = false;
        bool submissionQueued = false;
        try
        {
            for (int i = 0; i < commandLists.Length; i++)
            {
                lists[i].MarkSubmitted();
                submittedCount++;
            }

            _device.WithContext(gl =>
            {
                commandsIssued = true;
                for (int i = 0; i < commandLists.Length; i++)
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
                        _device.ComputeShaderApi);

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
                        _pending.Add(new PendingSubmission(lists, commandLists.Length, completionSync));
                        submissionQueued = true;
                    }
                }
                catch
                {
                    _device.OnSubmissionCompleted();
                    throw;
                }
            });
        }
        catch
        {
            if (commandsIssued && !_device.IsDisposed)
            {
                _device.WithContext(gl =>
                {
                    gl.Finish();
                    if (completionSync != 0 && !submissionQueued)
                        _device.SyncApi.DeleteSync(completionSync);
                });
            }
            else if (completionSync != 0 && !_device.IsDisposed && !submissionQueued)
            {
                _device.WithContext(gl => _device.SyncApi.DeleteSync(completionSync));
            }

            for (int i = 0; i < submittedCount; i++)
            {
                if (lists[i].Lifecycle.State == GraphicsCommandListState.Pending)
                    lists[i].Complete();
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

                    _device.SyncApi.DeleteSync(submission.Sync);
                    CompleteSubmission(submission);
                    _pending.RemoveAt(i);
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
            lock (_gate)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    PendingSubmission submission = _pending[i];
                    _device.SyncApi.DeleteSync(submission.Sync);
                    CompleteSubmission(submission);
                    _pending.RemoveAt(i);
                }
            }
        });
    }

    internal void DrainUnderContext(GL gl)
    {
        lock (_gate)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingSubmission submission = _pending[i];
                _device.SyncApi.DeleteSync(submission.Sync);
                CompleteSubmission(submission);
                _pending.RemoveAt(i);
            }
        }
    }

    private void CompleteSubmission(PendingSubmission submission)
    {
        for (int i = 0; i < submission.Count; i++)
            submission.CommandLists[i].Complete();

        ArrayPool<OpenGLGraphicsCommandList>.Shared.Return(
            submission.CommandLists,
            clearArray: true);
        _device.OnSubmissionCompleted();
    }

    private readonly record struct PendingSubmission(
        OpenGLGraphicsCommandList[] CommandLists,
        int Count,
        nint Sync);
}
