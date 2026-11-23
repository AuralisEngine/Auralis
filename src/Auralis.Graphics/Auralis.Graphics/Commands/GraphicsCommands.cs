using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;

namespace Auralis.Graphics.Commands;

/// <summary>Current lifecycle state of a recorded command list.</summary>
public enum GraphicsCommandListState
{
    /// <summary>The list has no recorded commands and can begin recording.</summary>
    Initial,
    /// <summary>The caller is recording commands; the list cannot be submitted yet.</summary>
    Recording,
    /// <summary>Recording has ended and the list is eligible for submission.</summary>
    Executable,
    /// <summary>The list has been submitted and its GPU work has not yet been retired.</summary>
    Pending,
    /// <summary>The list has been disposed and cannot be used again.</summary>
    Disposed
}

/// <summary>Reuse policy selected when a command list is allocated.</summary>
public enum GraphicsCommandListUsage
{
    /// <summary>Allows an executable list to be reset and recorded again after its submission completes.</summary>
    Reusable,
    /// <summary>Allows at most one successful submission and forbids resetting after that submission.</summary>
    OneTimeSubmit
}

/// <summary>Element width used when interpreting an index buffer for indexed draws.</summary>
public enum GraphicsIndexFormat
{
    /// <summary>Each index is an unsigned 16-bit integer.</summary>
    UInt16,
    /// <summary>Each index is an unsigned 32-bit integer.</summary>
    UInt32
}

/// <summary>
/// Small, allocation-free state tracker for backend command-list implementations. Backends should
/// call MarkSubmitted only after successful queue submission and MarkCompleted after GPU completion.
/// </summary>
public sealed class GraphicsCommandListLifecycle
{
    private int _state;
    private int _hasBeenSubmitted;

    /// <summary>Gets the queue role for which this lifecycle tracker was created.</summary>
    public GraphicsQueueType QueueType { get; }

    /// <summary>Gets whether the list may be reset and resubmitted.</summary>
    public GraphicsCommandListUsage Usage { get; }

    /// <summary>Gets the current lifecycle state using a volatile read.</summary>
    public GraphicsCommandListState State => (GraphicsCommandListState)Volatile.Read(ref _state);

    /// <summary>Creates a lifecycle tracker for one queue role and reuse policy.</summary>
    /// <param name="queueType">Queue role whose command legality is enforced by the tracker.</param>
    /// <param name="usage">Whether the list is reusable or may be submitted only once.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either enum value is undefined.</exception>
    public GraphicsCommandListLifecycle(
        GraphicsQueueType queueType,
        GraphicsCommandListUsage usage = GraphicsCommandListUsage.Reusable)
    {
        if (!Enum.IsDefined(queueType))
            throw new ArgumentOutOfRangeException(nameof(queueType));
        if (!Enum.IsDefined(usage))
            throw new ArgumentOutOfRangeException(nameof(usage));

        QueueType = queueType;
        Usage = usage;
    }

    /// <summary>Transitions an unused or reset list into the recording state.</summary>
    /// <exception cref="InvalidOperationException">The list is not in the <see cref="GraphicsCommandListState.Initial" /> state.</exception>
    public void Begin() => Transition(GraphicsCommandListState.Initial, GraphicsCommandListState.Recording);

    /// <summary>Ends recording and makes the list eligible for submission.</summary>
    /// <exception cref="InvalidOperationException">The list is not currently recording.</exception>
    public void End() => Transition(GraphicsCommandListState.Recording, GraphicsCommandListState.Executable);

    /// <summary>Marks an executable list as submitted and therefore pending GPU completion.</summary>
    /// <remarks>For one-time lists, the single-submission reservation is rolled back if the lifecycle transition fails.</remarks>
    /// <exception cref="InvalidOperationException">The list is not executable, or a one-time list was already submitted.</exception>
    public void MarkSubmitted()
    {
        if (Usage == GraphicsCommandListUsage.OneTimeSubmit &&
            Interlocked.CompareExchange(ref _hasBeenSubmitted, 1, 0) != 0)
            throw new InvalidOperationException("A one-time command list cannot be submitted more than once.");

        try
        {
            Transition(GraphicsCommandListState.Executable, GraphicsCommandListState.Pending);
        }
        catch
        {
            if (Usage == GraphicsCommandListUsage.OneTimeSubmit)
                Volatile.Write(ref _hasBeenSubmitted, 0);
            throw;
        }
    }

    /// <summary>Transitions a pending list back to executable after its GPU work has completed.</summary>
    /// <exception cref="InvalidOperationException">The list is not pending.</exception>
    public void MarkCompleted() =>
        Transition(GraphicsCommandListState.Pending, GraphicsCommandListState.Executable);

    /// <summary>Attempts the Pending-to-Executable transition without failing if another owner won it.</summary>
    internal bool TryMarkCompleted() =>
        Interlocked.CompareExchange(
            ref _state,
            (int)GraphicsCommandListState.Executable,
            (int)GraphicsCommandListState.Pending) == (int)GraphicsCommandListState.Pending;

    /// <summary>Clears the lifecycle for a new recording after completion.</summary>
    /// <remarks>A reusable list may be reset from the executable state. A one-time list cannot be reset after it has been submitted.</remarks>
    /// <exception cref="InvalidOperationException">The list is recording or pending, or a submitted one-time list is being reset.</exception>
    public void Reset()
    {
        if (Usage == GraphicsCommandListUsage.OneTimeSubmit && Volatile.Read(ref _hasBeenSubmitted) != 0)
            throw new InvalidOperationException("A one-time command list cannot be reset after submission.");

        while (true)
        {
            GraphicsCommandListState state = State;
            if (state == GraphicsCommandListState.Initial)
                return;
            if (state != GraphicsCommandListState.Executable)
                throw new InvalidOperationException($"A command list in state {state} cannot be reset.");

            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)GraphicsCommandListState.Initial,
                    (int)GraphicsCommandListState.Executable) == (int)GraphicsCommandListState.Executable)
                return;
        }
    }

    /// <summary>Verifies that a command may be appended in the current lifecycle state.</summary>
    /// <exception cref="InvalidOperationException">The list is not recording.</exception>
    public void EnsureRecording()
    {
        if (State != GraphicsCommandListState.Recording)
            throw new InvalidOperationException("Commands can only be recorded between Begin and End.");
    }

    /// <summary>Verifies recording state and that the list belongs to a graphics queue.</summary>
    /// <exception cref="InvalidOperationException">The list is not recording or its queue role is not graphics.</exception>
    public void EnsureGraphicsCommands()
    {
        EnsureRecording();
        if (QueueType != GraphicsQueueType.Graphics)
            throw new InvalidOperationException("Draw commands require a graphics command list.");
    }

    /// <summary>Verifies recording state and that the list is not restricted to copy commands.</summary>
    /// <exception cref="InvalidOperationException">The list is not recording or belongs to a copy queue.</exception>
    public void EnsureComputeCommands()
    {
        EnsureRecording();
        if (QueueType == GraphicsQueueType.Copy)
            throw new InvalidOperationException("Dispatch commands are not legal on a copy command list.");
    }

    /// <summary>Verifies recording state for a copy command.</summary>
    /// <exception cref="InvalidOperationException">The list is not recording.</exception>
    public void EnsureCopyCommands() => EnsureRecording();

    /// <summary>Marks the tracker disposed after backend-owned in-flight work has completed.</summary>
    public void MarkDisposed()
    {
        while (true)
        {
            GraphicsCommandListState state = State;
            if (state is GraphicsCommandListState.Pending or GraphicsCommandListState.Recording)
                throw new InvalidOperationException("A pending or recording command list cannot be destroyed yet.");
            if (state == GraphicsCommandListState.Disposed)
                return;

            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)GraphicsCommandListState.Disposed,
                    (int)state) == (int)state)
                return;
        }
    }

    private void Transition(GraphicsCommandListState expected, GraphicsCommandListState next)
    {
        if (Interlocked.CompareExchange(ref _state, (int)next, (int)expected) != (int)expected)
            throw new InvalidOperationException($"Cannot transition a command list from {State}; expected {expected}.");
    }
}

/// <summary>
/// A device-owned queue identity. A queue may support presentation without supporting command recording.
/// </summary>
public interface IGraphicsQueue
{
    IGraphicsDevice Device { get; }

    uint Index { get; }
}

/// <summary>
/// A device-owned command-capable queue. Presentation-only queues intentionally do not implement this interface.
/// </summary>
public interface IGraphicsCommandQueue : IGraphicsQueue
{
    /// <summary>Gets this queue's logical role; distinct roles may still share one native command stream.</summary>
    GraphicsQueueType QueueType { get; }

    /// <summary>Allocates a command recorder associated with this queue.</summary>
    /// <param name="usage">Whether the returned list can be reused after completion or submitted once.</param>
    /// <returns>A new list in the initial state.</returns>
    IGraphicsCommandList CreateCommandList(GraphicsCommandListUsage usage = GraphicsCommandListUsage.Reusable);

    /// <summary>Submits one or more executable command lists in the supplied order.</summary>
    /// <param name="commandLists">Non-empty batch of distinct executable lists created for this device and queue.</param>
    /// <param name="signalFence">Optional timeline fence to signal after the batch's commands.</param>
    /// <param name="signalValue">Value to signal when <paramref name="signalFence" /> is supplied; must be zero otherwise.</param>
    /// <exception cref="ArgumentException">The batch is empty, or a non-zero signal value is supplied without a fence.</exception>
    /// <exception cref="GraphicsValidationException">A list is foreign, duplicated, or not executable, or the fence is invalid for the queue's device.</exception>
    void Submit(
        ReadOnlySpan<IGraphicsCommandList> commandLists,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0);

    /// <summary>Enqueues a GPU-side wait. This does not block the calling CPU thread.</summary>
    /// <remarks>
    /// OpenGL and OpenGL ES can enqueue a wait only for a synchronization object that has already
    /// been created by a submission or CPU signal. A signal that has not yet been submitted or
    /// CPU-signaled is an unknown future object and cannot be waited on by a GL queue.
    /// </remarks>
    void Wait(IGraphicsFence fence, ulong value);
}

/// <summary>
/// Backend command recorder. Begin/End/Reset and operation legality are enforced by the implementation.
/// Recording is caller-thread-owned; separate command lists may be recorded concurrently.
/// </summary>
public interface IGraphicsCommandList : IGraphicsDeviceObject
{
    GraphicsQueueType QueueType { get; }

    GraphicsCommandListUsage Usage { get; }

    GraphicsCommandListState State { get; }

    /// <summary>Begins recording commands into the list.</summary>
    /// <exception cref="ObjectDisposedException">The list or its device is disposed.</exception>
    /// <exception cref="InvalidOperationException">The list is not in the initial state.</exception>
    void Begin();

    /// <summary>Ends recording and makes the list executable for submission.</summary>
    /// <exception cref="InvalidOperationException">The list is not recording.</exception>
    void End();

    /// <summary>Resets an executable reusable list and releases its recorded resource references.</summary>
    /// <exception cref="InvalidOperationException">The list is recording, pending, or is a one-time list that has already been submitted.</exception>
    void Reset();

    /// <summary>Records memory-visibility barriers for the specified resource access transitions.</summary>
    /// <param name="barriers">Resource barriers describing the producer and subsequent consumer accesses.</param>
    /// <remarks>Backends may translate these logical barriers into broader native barriers when the API does not support resource-scoped barriers.</remarks>
    void MemoryBarrier(ReadOnlySpan<GraphicsResourceBarrier> barriers);

    /// <summary>
    /// Selects the color and optional depth/stencil attachments for subsequent draw and clear commands.
    /// The backend retains references to these views until the recording is reset or completed.
    /// An empty color span is valid when a depth/stencil-only target is supplied.
    /// </summary>
    void SetRenderTargets(
        ReadOnlySpan<IGraphicsTextureView> colorTargets,
        IGraphicsTextureView? depthStencilTarget = null);

    /// <summary>Sets the viewport used by subsequent draw commands.</summary>
    /// <param name="viewport">Viewport rectangle and depth range.</param>
    void SetViewport(in GraphicsViewport viewport);

    /// <summary>Sets the scissor rectangle used by subsequent draw commands.</summary>
    /// <param name="scissor">Scissor rectangle in framebuffer coordinates.</param>
    void SetScissor(in GraphicsScissorRect scissor);

    /// <summary>Records a floating-point clear of one selected color attachment.</summary>
    /// <param name="attachmentIndex">Zero-based color attachment index in the active render-target set.</param>
    /// <param name="color">Floating-point clear components.</param>
    void ClearColor(uint attachmentIndex, in GraphicsClearColor color);

    /// <summary>Records an unsigned-integer clear of one selected color attachment.</summary>
    /// <param name="attachmentIndex">Zero-based color attachment index in the active render-target set.</param>
    /// <param name="color">Unsigned integer clear components.</param>
    void ClearColorUInt(uint attachmentIndex, in GraphicsClearColorUInt color);

    /// <summary>Records a signed-integer clear of one selected color attachment.</summary>
    /// <param name="attachmentIndex">Zero-based color attachment index in the active render-target set.</param>
    /// <param name="color">Signed integer clear components.</param>
    void ClearColorSInt(uint attachmentIndex, in GraphicsClearColorSInt color);

    /// <summary>Records a clear of the active depth and/or stencil attachment.</summary>
    /// <param name="depth">Depth value used when <paramref name="clearDepth" /> is enabled.</param>
    /// <param name="stencil">Stencil value used when <paramref name="clearStencil" /> is enabled.</param>
    /// <param name="clearDepth">Whether to clear depth.</param>
    /// <param name="clearStencil">Whether to clear stencil.</param>
    void ClearDepthStencil(
        float depth,
        uint stencil,
        bool clearDepth = true,
        bool clearStencil = false);

    /// <summary>Selects the render pipeline for subsequent graphics commands.</summary>
    /// <param name="pipeline">Render pipeline owned by the same device.</param>
    void SetRenderPipeline(IGraphicsRenderPipeline pipeline);

    /// <summary>Selects the compute pipeline for subsequent dispatch commands.</summary>
    /// <param name="pipeline">Compute pipeline owned by the same device.</param>
    void SetComputePipeline(IGraphicsComputePipeline pipeline);

    /// <summary>Records a snapshot of a binding set at the specified pipeline-layout set index.</summary>
    /// <param name="setIndex">Zero-based set index.</param>
    /// <param name="bindingSet">Binding set compatible with the active pipeline layout.</param>
    /// <remarks>The recorded snapshot is not retroactively changed by later updates to the public binding set.</remarks>
    void SetBindingSet(uint setIndex, IGraphicsBindingSet bindingSet);

    /// <summary>Binds a vertex buffer to one vertex-input binding slot.</summary>
    /// <param name="binding">Vertex-buffer binding index.</param>
    /// <param name="buffer">Buffer created by the same device with vertex-buffer usage.</param>
    /// <param name="offset">Byte offset to the first vertex record.</param>
    void SetVertexBuffer(uint binding, IGraphicsBuffer buffer, ulong offset = 0);

    /// <summary>Binds an index buffer and selects the element width used by indexed draws.</summary>
    /// <param name="buffer">Buffer created by the same device with index-buffer usage.</param>
    /// <param name="format">16-bit or 32-bit unsigned index representation.</param>
    /// <param name="offset">Byte offset of the first index; alignment is validated by the backend.</param>
    void SetIndexBuffer(IGraphicsBuffer buffer, GraphicsIndexFormat format, ulong offset = 0);

    /// <summary>Records a non-indexed draw using the current render pipeline and vertex bindings.</summary>
    /// <param name="vertexCount">Number of vertices per instance.</param>
    /// <param name="instanceCount">Number of instances.</param>
    /// <param name="firstVertex">First vertex index.</param>
    /// <param name="firstInstance">First instance index.</param>
    void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0);

    /// <summary>Records an indexed draw using the current index and vertex-buffer bindings.</summary>
    /// <param name="indexCount">Number of indices per instance.</param>
    /// <param name="instanceCount">Number of instances.</param>
    /// <param name="firstIndex">First index element, relative to the bound index-buffer offset.</param>
    /// <param name="baseVertex">Signed adjustment applied to each fetched vertex index.</param>
    /// <param name="firstInstance">First instance index.</param>
    void DrawIndexed(
        uint indexCount,
        uint instanceCount = 1,
        uint firstIndex = 0,
        int baseVertex = 0,
        uint firstInstance = 0);

    /// <summary>Records non-indexed draw commands from a GPU-resident indirect-argument buffer.</summary>
    /// <param name="arguments">Buffer with indirect-argument usage and the backend's native draw-record layout.</param>
    /// <param name="offset">Byte offset of the first argument record.</param>
    /// <param name="drawCount">Number of records; zero is a validated no-op.</param>
    /// <param name="stride">Byte distance between records; zero selects the tightly packed native record size.</param>
    void DrawIndirect(IGraphicsBuffer arguments, ulong offset, uint drawCount = 1, uint stride = 0);

    /// <summary>Records indexed draw commands from a GPU-resident indirect-argument buffer.</summary>
    /// <param name="arguments">Buffer with indirect-argument usage and the native indexed-draw record layout.</param>
    /// <param name="offset">Byte offset of the first argument record.</param>
    /// <param name="drawCount">Number of records; zero is a validated no-op.</param>
    /// <param name="stride">Byte distance between records; zero selects the tightly packed native record size.</param>
    void DrawIndexedIndirect(IGraphicsBuffer arguments, ulong offset, uint drawCount = 1, uint stride = 0);

    /// <summary>Records a mesh-shader work-group dispatch. Requires a mesh pipeline.</summary>
    void DrawMeshTasks(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1);

    /// <summary>Records one or more mesh-task commands from a GPU indirect-argument buffer.</summary>
    /// <remarks>
    /// Indirect argument layout follows the active mesh-shader dialect. OpenGL EXT uses three
    /// uint values ({ groupCountX, groupCountY, groupCountZ }); OpenGL NV uses its native two
    /// uint values ({ count, first }). Other backends may use their native command layout.
    /// </remarks>
    void DrawMeshTasksIndirect(IGraphicsBuffer arguments, ulong offset, uint drawCount = 1, uint stride = 0);

    /// <summary>
    /// Records a compute dispatch. If any group count is zero, the operation is a valid no-op and no GPU dispatch is recorded.
    /// </summary>
    void Dispatch(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1);

    /// <summary>Records a compute dispatch from a GPU-resident indirect argument.</summary>
    /// <param name="arguments">Buffer containing the native three-axis dispatch command.</param>
    /// <param name="offset">Byte offset of the dispatch argument, subject to native alignment requirements.</param>
    void DispatchIndirect(IGraphicsBuffer arguments, ulong offset);

    /// <summary>Records a byte-range copy between two buffers on the same device.</summary>
    /// <param name="source">Source buffer with copy-source usage.</param>
    /// <param name="sourceOffset">Byte offset into the source.</param>
    /// <param name="destination">Destination buffer with copy-destination usage.</param>
    /// <param name="destinationOffset">Byte offset into the destination.</param>
    /// <param name="sizeInBytes">Number of bytes to copy.</param>
        IGraphicsBuffer source,
        ulong sourceOffset,
        IGraphicsBuffer destination,
        ulong destinationOffset,
        ulong sizeInBytes);

    /// <summary>Records a full-resource texture copy supported by the backend's texture-copy path.</summary>
    /// <param name="source">Source texture with copy-source usage.</param>
    /// <param name="destination">Destination texture with copy-destination usage.</param>
    /// <remarks>Backends may impose additional compatibility requirements on target, format, extent, mip, layer, and sample count.</remarks>
    void CopyTexture(IGraphicsTexture source, IGraphicsTexture destination);
}
