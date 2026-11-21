using System.Runtime.InteropServices;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

internal enum OpenGLESCmdKind : byte
{
    Barrier,
    CopyBuffer,
    CopyTexture,
    RenderTargets,
    Viewport,
    Scissor,
    ClearColor,
    ClearDepthStencil,
    RenderPipeline,
    ComputePipeline,
    BindingSet,
    VertexBuffer,
    IndexBuffer,
    Draw,
    DrawIndexed,
    DrawIndirect,
    DrawIndexedIndirect,
    Dispatch,
    DispatchIndirect
}

internal struct OpenGLESCmd
{
    internal OpenGLESCmdKind Kind;
    internal IGraphicsBuffer? Buffer0;
    internal IGraphicsBuffer? Buffer1;
    internal IGraphicsTexture? Texture0;
    internal IGraphicsTexture? Texture1;
    internal IGraphicsTextureView? DepthTarget;
    internal int ViewOffset;
    internal int ColorCount;
    internal ulong Offset0;
    internal ulong Offset1;
    internal ulong Size;
    internal GraphicsViewport Viewport;
    internal GraphicsScissorRect Scissor;
    internal GraphicsClearColor ClearColor;
    internal float ClearDepth;
    internal uint ClearStencil;
    internal bool ClearDepthEnabled;
    internal bool ClearStencilEnabled;
    internal uint AttachmentIndex;
    internal OpenGLESGraphicsRenderPipeline? RenderPipeline;
    internal OpenGLESGraphicsComputePipeline? ComputePipeline;
    internal uint SetIndex;
    internal OpenGLESGraphicsBindingLayout? BindingLayout;
    internal OpenGLESGraphicsPipelineLayout? PipelineLayout;
    internal int BindingValueOffset;
    internal int BindingValueCount;
    internal uint VertexBinding;
    internal uint IndexFormat;
    internal uint VertexCount;
    internal uint InstanceCount;
    internal uint FirstVertex;
    internal uint FirstInstance;
    internal uint IndexCount;
    internal int BaseVertex;
    internal uint DrawCount;
    internal uint Stride;
    internal uint GroupsX;
    internal uint GroupsY;
    internal uint GroupsZ;
}

internal sealed class OpenGLESGraphicsCommandList : GraphicsDeviceObject, IGraphicsCommandList
{
    private readonly OpenGLESGraphicsCommandQueue _queue;
    private readonly GraphicsCommandListLifecycle _lifecycle;
    private readonly List<OpenGLESCmd> _commands = new(48);
    private readonly List<IGraphicsTextureView> _renderTargetViews = new(4);
    private readonly List<OpenGLESBindingValue> _bindingValues = new(24);
    private readonly HashSet<IOpenGLESNativeResource> _retained = new(ReferenceEqualityComparer.Instance);
    private OpenGLESGraphicsRenderPipeline? _renderPipeline;
    private OpenGLESGraphicsComputePipeline? _computePipeline;
    private OpenGLESGraphicsPipelineLayout? _pipelineLayout;
    private readonly HashSet<uint> _boundSets = [];
    private IGraphicsBuffer? _indexBuffer;
    private GraphicsIndexFormat _indexFormat;
    private ulong _indexOffset;
    private bool _hasTargets;
    private bool _hasViewport;
    private bool _hasDepthTarget;
    private uint _colorCount;
    private int _targetOffset;
    private int _recordingThreadId;

    internal OpenGLESGraphicsCommandList(
        OpenGLESGraphicsCommandQueue queue,
        GraphicsCommandListUsage usage) : base(queue.Device)
    {
        _queue = queue;
        Usage = usage;
        _lifecycle = new GraphicsCommandListLifecycle(queue.QueueType, usage);
    }

    public GraphicsQueueType QueueType => _queue.QueueType;
    public GraphicsCommandListUsage Usage { get; }
    public GraphicsCommandListState State => _lifecycle.State;

    internal GraphicsCommandListLifecycle Lifecycle => _lifecycle;
    internal ReadOnlySpan<OpenGLESCmd> Commands => CollectionsMarshal.AsSpan(_commands);
    internal ReadOnlySpan<IGraphicsTextureView> RenderTargetViews => CollectionsMarshal.AsSpan(_renderTargetViews);
    internal ReadOnlySpan<OpenGLESBindingValue> BindingValues => CollectionsMarshal.AsSpan(_bindingValues);

    public void Begin()
    {
        ThrowIfDisposed();
        _lifecycle.Begin();
        _recordingThreadId = Environment.CurrentManagedThreadId;
    }

    public void End()
    {
        EnsureRecording();
        _lifecycle.End();
        _recordingThreadId = 0;
    }

    public void Reset()
    {
        ThrowIfDisposed();
        _lifecycle.Reset();
        ClearRecordedState();
    }

    public void MemoryBarrier(ReadOnlySpan<GraphicsResourceBarrier> barriers)
    {
        EnsureRecording();
        if (barriers.IsEmpty)
            return;

        for (int i = 0; i < barriers.Length; i++)
        {
            GraphicsResourceBarrier barrier = barriers[i];
            barrier.Validate();
            EnsureOwned(barrier.Resource);
        }

        // GLES exposes a global memory barrier rather than resource/range-specific barriers. The
        // all-bits token is defined by GLES 3.1 and covers every common access transition.
        _commands.Add(new OpenGLESCmd { Kind = OpenGLESCmdKind.Barrier });
    }

    public void SetRenderTargets(
        ReadOnlySpan<IGraphicsTextureView> colorTargets,
        IGraphicsTextureView? depthStencilTarget = null)
    {
        EnsureRecording();
        EnsureGraphicsQueue();

        if (colorTargets.Length > 1)
            throw new GraphicsValidationException("This GLES implementation currently supports one color attachment per framebuffer.");
        if (colorTargets.IsEmpty && depthStencilTarget is null)
            throw new GraphicsValidationException("At least one render attachment is required.");

        uint width = 0, height = 0;
        for (int i = 0; i < colorTargets.Length; i++)
        {
            ValidateRenderTarget(colorTargets[i], ref width, ref height);
            EnsureOwned(colorTargets[i]);
        }

        if (depthStencilTarget is not null)
        {
            ValidateRenderTarget(depthStencilTarget, ref width, ref height);
            EnsureOwned(depthStencilTarget);
            GraphicsFormat format = GetViewFormat(depthStencilTarget);
            if (format is not (GraphicsFormat.D16UNorm or GraphicsFormat.D24UNormS8UInt))
                throw new GraphicsValidationException($"Depth format {format} is not implemented for GLES render targets.");
        }

        int offset = _renderTargetViews.Count;
        for (int i = 0; i < colorTargets.Length; i++)
            _renderTargetViews.Add(colorTargets[i]);

        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.RenderTargets,
            ViewOffset = offset,
            ColorCount = colorTargets.Length,
            DepthTarget = depthStencilTarget
        });

        _targetOffset = offset;
        _colorCount = (uint)colorTargets.Length;
        _hasTargets = true;
        _hasDepthTarget = depthStencilTarget is not null;
    }

    public void SetViewport(in GraphicsViewport viewport)
    {
        EnsureRecording();
        EnsureGraphicsQueue();
        GraphicsViewport copy = viewport;
        copy.Validate();
        _hasViewport = true;
        _commands.Add(new OpenGLESCmd { Kind = OpenGLESCmdKind.Viewport, Viewport = copy });
    }

    public void SetScissor(in GraphicsScissorRect scissor)
    {
        EnsureRecording();
        EnsureGraphicsQueue();
        GraphicsScissorRect copy = scissor;
        copy.Validate();
        _commands.Add(new OpenGLESCmd { Kind = OpenGLESCmdKind.Scissor, Scissor = copy });
    }

    public void ClearColor(uint attachmentIndex, in GraphicsClearColor color)
    {
        EnsureRecording();
        EnsureGraphicsQueue();
        if (attachmentIndex >= _colorCount)
            throw new GraphicsValidationException("The requested color attachment is not bound.");
        if (attachmentIndex != 0)
            throw new GraphicsValidationException("This GLES implementation supports only color attachment zero.");
        GraphicsClearColor copy = color;
        copy.Validate();
        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.ClearColor,
            AttachmentIndex = attachmentIndex,
            ClearColor = copy
        });
    }

    public void ClearColorUInt(uint attachmentIndex, in GraphicsClearColorUInt color)
    {
        EnsureRecording();
        EnsureGraphicsQueue();
        throw new GraphicsValidationException(
            "Unsigned-integer color attachment clears are not implemented by this OpenGL ES backend.");
    }

    public void ClearColorSInt(uint attachmentIndex, in GraphicsClearColorSInt color) =>
        throw new GraphicsValidationException("Integer framebuffer clear operations are not implemented by this GLES backend.");

    public void ClearDepthStencil(
        float depth,
        uint stencil,
        bool clearDepth = true,
        bool clearStencil = false)
    {
        EnsureRecording();
        EnsureGraphicsQueue();
        if (!_hasDepthTarget)
            throw new GraphicsValidationException("A depth attachment must be selected before clearing depth.");
        if (!clearDepth && !clearStencil)
            throw new ArgumentException("At least one depth/stencil aspect must be cleared.");
        if (clearStencil)
            throw new GraphicsValidationException("Stencil attachment clears are not implemented by this GLES backend.");
        if (clearDepth && (!float.IsFinite(depth) || depth < 0 || depth > 1))
            throw new ArgumentOutOfRangeException(nameof(depth));

        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.ClearDepthStencil,
            ClearDepth = depth,
            ClearStencil = stencil,
            ClearDepthEnabled = clearDepth,
            ClearStencilEnabled = clearStencil
        });
    }

    public void SetRenderPipeline(IGraphicsRenderPipeline pipeline)
    {
        EnsureRecording();
        EnsureGraphicsQueue();
        ArgumentNullException.ThrowIfNull(pipeline);
        EnsureOwned(pipeline);
        if (pipeline is not OpenGLESGraphicsRenderPipeline glesPipeline)
            throw new GraphicsValidationException("The render pipeline belongs to another backend.");

        _renderPipeline = glesPipeline;
        _computePipeline = null;
        _pipelineLayout = (OpenGLESGraphicsPipelineLayout)glesPipeline.Layout;
        _boundSets.Clear();
        _indexBuffer = null;
        Retain(glesPipeline);
        _commands.Add(new OpenGLESCmd { Kind = OpenGLESCmdKind.RenderPipeline, RenderPipeline = glesPipeline });
    }

    public void SetComputePipeline(IGraphicsComputePipeline pipeline)
    {
        EnsureRecording();
        EnsureComputeQueue();
        ArgumentNullException.ThrowIfNull(pipeline);
        EnsureOwned(pipeline);
        if (pipeline is not OpenGLESGraphicsComputePipeline glesPipeline)
            throw new GraphicsValidationException("The compute pipeline belongs to another backend.");

        _computePipeline = glesPipeline;
        _renderPipeline = null;
        _pipelineLayout = (OpenGLESGraphicsPipelineLayout)glesPipeline.Layout;
        _boundSets.Clear();
        Retain(glesPipeline);
        _commands.Add(new OpenGLESCmd { Kind = OpenGLESCmdKind.ComputePipeline, ComputePipeline = glesPipeline });
    }

    public void SetBindingSet(uint setIndex, IGraphicsBindingSet bindingSet)
    {
        EnsureRecording();
        ArgumentNullException.ThrowIfNull(bindingSet);
        EnsureOwned(bindingSet);
        if (_pipelineLayout is null)
            throw new GraphicsValidationException("Select a pipeline before binding a descriptor set.");
        if (bindingSet is not OpenGLESGraphicsBindingSet glesSet)
            throw new GraphicsValidationException("The binding set belongs to another backend.");

        ReadOnlyMemory<IGraphicsBindingLayout> layouts = _pipelineLayout.BindingLayouts;
        if (setIndex >= (uint)layouts.Length ||
            !ReferenceEquals(layouts.Span[(int)setIndex], glesSet.Layout))
            throw new GraphicsValidationException($"Binding set {setIndex} does not match the active pipeline layout.");

        int offset = glesSet.SnapshotTo(_bindingValues);
        ReadOnlySpan<OpenGLESBindingValue> snapshot = CollectionsMarshal.AsSpan(_bindingValues).Slice(offset, glesSet.ValueCount);
        ValidateSnapshot((OpenGLESGraphicsBindingLayout)glesSet.Layout, snapshot);
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].Buffer is not null) EnsureOwned(snapshot[i].Buffer!);
            if (snapshot[i].Texture is not null) EnsureOwned(snapshot[i].Texture!);
            if (snapshot[i].Sampler is not null) EnsureOwned(snapshot[i].Sampler!);
        }

        _boundSets.Add(setIndex);
        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.BindingSet,
            SetIndex = setIndex,
            BindingLayout = (OpenGLESGraphicsBindingLayout)glesSet.Layout,
            PipelineLayout = _pipelineLayout,
            BindingValueOffset = offset,
            BindingValueCount = glesSet.ValueCount
        });
    }

    public void SetVertexBuffer(uint binding, IGraphicsBuffer buffer, ulong offset = 0)
    {
        EnsureRecording();
        EnsureGraphicsQueue();
        ArgumentNullException.ThrowIfNull(buffer);
        EnsureOwned(buffer);
        if (_renderPipeline?.Description.VertexInput is not { } layout)
            throw new GraphicsValidationException("The active pipeline has no vertex input layout.");
        if ((buffer.Description.Usage & GraphicsBufferUsage.Vertex) == 0)
            throw new GraphicsValidationException("The buffer was not created for vertex usage.");
        if (offset > buffer.Description.SizeInBytes)
            throw new ArgumentOutOfRangeException(nameof(offset));

        bool found = false;
        foreach (GraphicsVertexBufferLayout item in layout.Buffers)
            found |= item.Binding == binding;
        if (!found)
            throw new GraphicsValidationException($"Vertex binding {binding} is not declared by the pipeline.");

        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.VertexBuffer,
            VertexBinding = binding,
            Buffer0 = buffer,
            Offset0 = offset
        });
    }

    public void SetIndexBuffer(IGraphicsBuffer buffer, GraphicsIndexFormat format, ulong offset = 0)
    {
        EnsureRecording();
        EnsureGraphicsQueue();
        ArgumentNullException.ThrowIfNull(buffer);
        EnsureOwned(buffer);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        if ((buffer.Description.Usage & GraphicsBufferUsage.Index) == 0)
            throw new GraphicsValidationException("The buffer was not created for index usage.");

        uint elementSize = format == GraphicsIndexFormat.UInt16 ? 2u : 4u;
        if (offset > buffer.Description.SizeInBytes || offset % elementSize != 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        _indexBuffer = buffer;
        _indexFormat = format;
        _indexOffset = offset;
        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.IndexBuffer,
            Buffer0 = buffer,
            IndexFormat = (uint)format,
            Offset0 = offset
        });
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        EnsureRecording();
        EnsureGraphicsReady();
        if (firstInstance != 0)
            throw new GraphicsValidationException("OpenGL ES core DrawArraysInstanced does not expose a base-instance parameter.");
        if (firstVertex > int.MaxValue)
            throw new GraphicsValidationException("The first vertex exceeds the GLES integer range.");
        if (instanceCount == 0 || vertexCount == 0)
            return;

        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.Draw,
            VertexCount = vertexCount,
            InstanceCount = instanceCount,
            FirstVertex = firstVertex
        });
    }

    public void DrawIndexed(
        uint indexCount,
        uint instanceCount = 1,
        uint firstIndex = 0,
        int baseVertex = 0,
        uint firstInstance = 0)
    {
        EnsureRecording();
        EnsureGraphicsReady();
        if (_indexBuffer is null)
            throw new GraphicsValidationException("Set an index buffer before issuing an indexed draw.");
        if (baseVertex != 0)
            throw new GraphicsValidationException("Base-vertex indexed drawing is not implemented in this GLES backend.");
        if (firstInstance != 0)
            throw new GraphicsValidationException("OpenGL ES core DrawElementsInstanced does not expose a base-instance parameter.");

        uint indexSize = _indexFormat == GraphicsIndexFormat.UInt16 ? 2u : 4u;
        ulong byteOffset = checked(_indexOffset + (ulong)firstIndex * indexSize);
        ulong bytes = checked((ulong)indexCount * indexSize);
        if (byteOffset > _indexBuffer.Description.SizeInBytes ||
            bytes > _indexBuffer.Description.SizeInBytes - byteOffset)
            throw new GraphicsValidationException("The indexed draw reads past the index buffer.");
        if (instanceCount == 0 || indexCount == 0)
            return;

        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.DrawIndexed,
            Buffer0 = _indexBuffer,
            IndexFormat = (uint)_indexFormat,
            Offset0 = byteOffset,
            IndexCount = indexCount,
            InstanceCount = instanceCount
        });
    }

    public void DrawIndirect(IGraphicsBuffer arguments, ulong offset, uint drawCount = 1, uint stride = 0)
    {
        EnsureRecording();
        EnsureGraphicsReady();
        ValidateIndirect(arguments, offset, drawCount, 16);
        if (drawCount > 1)
            throw new GraphicsFeatureNotSupportedException(GraphicsFeature.MultiDrawIndirect, GraphicsFeature.MultiDrawIndirect);
        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.DrawIndirect,
            Buffer0 = arguments,
            Offset0 = offset
        });
    }

    public void DrawIndexedIndirect(IGraphicsBuffer arguments, ulong offset, uint drawCount = 1, uint stride = 0)
    {
        EnsureRecording();
        EnsureGraphicsReady();
        if (_indexBuffer is null)
            throw new GraphicsValidationException("Set an index buffer before issuing indexed indirect drawing.");
        ValidateIndirect(arguments, offset, drawCount, 20);
        if (drawCount > 1)
            throw new GraphicsFeatureNotSupportedException(GraphicsFeature.MultiDrawIndirect, GraphicsFeature.MultiDrawIndirect);
        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.DrawIndexedIndirect,
            Buffer0 = arguments,
            Buffer1 = _indexBuffer,
            Offset0 = offset,
            IndexFormat = (uint)_indexFormat
        });
    }

    public void DrawMeshTasks(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1) =>
        throw new GraphicsFeatureNotSupportedException(GraphicsFeature.MeshShaders, GraphicsFeature.MeshShaders);

    public void DrawMeshTasksIndirect(IGraphicsBuffer arguments, ulong offset, uint drawCount = 1, uint stride = 0) =>
        throw new GraphicsFeatureNotSupportedException(GraphicsFeature.MeshShaders, GraphicsFeature.MeshShaders);

    public void Dispatch(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1)
    {
        EnsureRecording();
        EnsureComputeReady();
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            throw new ArgumentOutOfRangeException(nameof(groupCountX));
        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.Dispatch,
            GroupsX = groupCountX,
            GroupsY = groupCountY,
            GroupsZ = groupCountZ
        });
    }

    public void DispatchIndirect(IGraphicsBuffer arguments, ulong offset)
    {
        EnsureRecording();
        EnsureComputeReady();
        ArgumentNullException.ThrowIfNull(arguments);
        EnsureOwned(arguments);
        if ((arguments.Description.Usage & GraphicsBufferUsage.IndirectArguments) == 0)
            throw new GraphicsValidationException("Indirect dispatch requires IndirectArguments buffer usage.");
        if ((offset & 3) != 0 || offset > arguments.Description.SizeInBytes ||
            12UL > arguments.Description.SizeInBytes - offset)
            throw new GraphicsValidationException("Indirect dispatch arguments are unaligned or outside the buffer bounds.");
        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.DispatchIndirect,
            Buffer0 = arguments,
            Offset0 = offset
        });
    }

    public void CopyBuffer(
        IGraphicsBuffer source,
        ulong sourceOffset,
        IGraphicsBuffer destination,
        ulong destinationOffset,
        ulong sizeInBytes)
    {
        EnsureRecording();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        EnsureOwned(source);
        EnsureOwned(destination);

        if ((source.Description.Usage & GraphicsBufferUsage.CopySource) == 0 ||
            (destination.Description.Usage & GraphicsBufferUsage.CopyDestination) == 0)
            throw new GraphicsValidationException("Buffer copy usage flags are missing.");
        new GraphicsBufferRange(sourceOffset, sizeInBytes).Validate(source.Description);
        new GraphicsBufferRange(destinationOffset, sizeInBytes).Validate(destination.Description);

        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.CopyBuffer,
            Buffer0 = source,
            Buffer1 = destination,
            Offset0 = sourceOffset,
            Offset1 = destinationOffset,
            Size = sizeInBytes
        });
    }

    public void CopyTexture(IGraphicsTexture source, IGraphicsTexture destination)
    {
        EnsureRecording();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        EnsureOwned(source);
        EnsureOwned(destination);
        GraphicsTextureDescription src = source.Description;
        GraphicsTextureDescription dst = destination.Description;
        if ((src.Usage & GraphicsTextureUsage.CopySource) == 0 ||
            (dst.Usage & GraphicsTextureUsage.CopyDestination) == 0)
            throw new GraphicsValidationException("Texture copy usage flags are missing.");
        if (src != dst)
            throw new GraphicsValidationException("The GLES copy path requires identical whole-texture descriptions.");
        if (src.Dimension != GraphicsTextureDimension.Texture2D || src.SampleCount != 1)
            throw new GraphicsValidationException("The GLES framebuffer blit copy path currently supports 2D textures only.");

        _commands.Add(new OpenGLESCmd
        {
            Kind = OpenGLESCmdKind.CopyTexture,
            Texture0 = source,
            Texture1 = destination
        });
    }

    internal void MarkSubmitted() => _lifecycle.MarkSubmitted();

    internal void Complete()
    {
        _lifecycle.MarkCompleted();
        if (IsDisposed)
        {
            try { _lifecycle.MarkDisposed(); }
            catch (InvalidOperationException) { }
            ClearRecordedState();
        }
    }

    internal void Execute(GL gl)
    {
        uint framebuffer = 0;
        uint readFramebuffer = 0;
        uint drawFramebuffer = 0;
        OpenGLESGraphicsRenderPipeline? activeRenderPipeline = null;
        try
        {
            for (int i = 0; i < _commands.Count; i++)
            {
                OpenGLESCmd command = _commands[i];
                switch (command.Kind)
                {
                    case OpenGLESCmdKind.Barrier:
                        gl.MemoryBarrier((uint)MemoryBarrierMask.AllBarrierBits);
                        break;

                    case OpenGLESCmdKind.CopyBuffer:
                        gl.BindBuffer(BufferTargetARB.CopyReadBuffer, ((OpenGLESGraphicsBuffer)command.Buffer0!).Handle);
                        gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, ((OpenGLESGraphicsBuffer)command.Buffer1!).Handle);
                        gl.CopyBufferSubData(
                            BufferTargetARB.CopyReadBuffer,
                            BufferTargetARB.CopyWriteBuffer,
                            checked((nint)command.Offset0),
                            checked((nint)command.Offset1),
                            checked((nuint)command.Size));
                        gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
                        gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
                        break;

                    case OpenGLESCmdKind.CopyTexture:
                        readFramebuffer = gl.GenFramebuffer();
                        drawFramebuffer = gl.GenFramebuffer();
                        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
                        gl.FramebufferTexture2D(
                            FramebufferTarget.ReadFramebuffer,
                            GLEnum.ColorAttachment0,
                            TextureTarget.Texture2D,
                            ((OpenGLESGraphicsTexture)command.Texture0!).Handle,
                            0);
                        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFramebuffer);
                        gl.FramebufferTexture2D(
                            FramebufferTarget.DrawFramebuffer,
                            GLEnum.ColorAttachment0,
                            TextureTarget.Texture2D,
                            ((OpenGLESGraphicsTexture)command.Texture1!).Handle,
                            0);
                        uint copyWidth = command.Texture0!.Description.Width;
                        uint copyHeight = command.Texture0.Description.Height;
                        gl.BlitFramebuffer(
                            0, 0, (int)copyWidth, (int)copyHeight,
                            0, 0, (int)copyWidth, (int)copyHeight,
                            GLEnum.ColorBufferBit,
                            GLEnum.Nearest);
                        gl.DeleteFramebuffer(readFramebuffer);
                        gl.DeleteFramebuffer(drawFramebuffer);
                        readFramebuffer = drawFramebuffer = 0;
                        break;

                    case OpenGLESCmdKind.RenderTargets:
                        if (framebuffer != 0)
                            gl.DeleteFramebuffer(framebuffer);
                        framebuffer = CreateFramebuffer(gl, command);
                        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                        break;

                    case OpenGLESCmdKind.Viewport:
                        gl.Viewport(
                            command.Viewport.X,
                            command.Viewport.Y,
                            command.Viewport.Width,
                            command.Viewport.Height);
                        gl.DepthRangef(command.Viewport.MinDepth, command.Viewport.MaxDepth);
                        break;

                    case OpenGLESCmdKind.Scissor:
                        gl.Enable(GLEnum.ScissorTest);
                        gl.Scissor(
                            command.Scissor.X,
                            command.Scissor.Y,
                            command.Scissor.Width,
                            command.Scissor.Height);
                        break;

                    case OpenGLESCmdKind.ClearColor:
                        gl.ClearColor(
                            command.ClearColor.Red,
                            command.ClearColor.Green,
                            command.ClearColor.Blue,
                            command.ClearColor.Alpha);
                        gl.Clear(GLEnum.ColorBufferBit);
                        break;

                    case OpenGLESCmdKind.ClearDepthStencil:
                        if (command.ClearDepthEnabled)
                        {
                            gl.ClearDepthf(command.ClearDepth);
                            gl.Clear(GLEnum.DepthBufferBit);
                        }
                        break;

                    case OpenGLESCmdKind.RenderPipeline:
                        activeRenderPipeline = command.RenderPipeline!;
                        activeRenderPipeline.BindPipelineState(gl);
                        break;

                    case OpenGLESCmdKind.ComputePipeline:
                        activeRenderPipeline = null;
                        command.ComputePipeline!.BindPipelineState(gl);
                        break;

                    case OpenGLESCmdKind.BindingSet:
                        BindSet(gl, command);
                        break;

                    case OpenGLESCmdKind.VertexBuffer:
                        (activeRenderPipeline ?? throw new GraphicsValidationException(
                            "A render pipeline must be bound before configuring vertex input.")).ConfigureVertexBinding(
                            gl,
                            command.VertexBinding,
                            ((OpenGLESGraphicsBuffer)command.Buffer0!).Handle,
                            command.Offset0,
                            GetVertexStride(activeRenderPipeline.Description.VertexInput!, command.VertexBinding));
                        break;

                    case OpenGLESCmdKind.IndexBuffer:
                        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ((OpenGLESGraphicsBuffer)command.Buffer0!).Handle);
                        break;

                    case OpenGLESCmdKind.Draw:
                        gl.DrawArraysInstanced(
                            GetPrimitive((activeRenderPipeline ?? throw new GraphicsValidationException(
                                "A render pipeline must be bound before drawing.")).Description.Topology),
                            checked((int)command.FirstVertex),
                            command.VertexCount,
                            command.InstanceCount);
                        break;

                    case OpenGLESCmdKind.DrawIndexed:
                        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ((OpenGLESGraphicsBuffer)command.Buffer0!).Handle);
                        gl.DrawElementsInstanced(
                            GetPrimitive((activeRenderPipeline ?? throw new GraphicsValidationException(
                                "A render pipeline must be bound before drawing.")).Description.Topology),
                            command.IndexCount,
                            command.IndexFormat == (uint)GraphicsIndexFormat.UInt16
                                ? DrawElementsType.UnsignedShort
                                : DrawElementsType.UnsignedInt,
                            (void*)checked((nint)command.Offset0),
                            command.InstanceCount);
                        break;

                    case OpenGLESCmdKind.DrawIndirect:
                        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, ((OpenGLESGraphicsBuffer)command.Buffer0!).Handle);
                        gl.DrawArraysIndirect(GetPrimitive((activeRenderPipeline ?? throw new GraphicsValidationException(
                            "A render pipeline must be bound before drawing.")).Description.Topology), (void*)checked((nint)command.Offset0));
                        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
                        break;

                    case OpenGLESCmdKind.DrawIndexedIndirect:
                        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ((OpenGLESGraphicsBuffer)command.Buffer1!).Handle);
                        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, ((OpenGLESGraphicsBuffer)command.Buffer0!).Handle);
                        gl.DrawElementsIndirect(
                            GetPrimitive((activeRenderPipeline ?? throw new GraphicsValidationException(
                                "A render pipeline must be bound before drawing.")).Description.Topology),
                            command.IndexFormat == (uint)GraphicsIndexFormat.UInt16
                                ? DrawElementsType.UnsignedShort
                                : DrawElementsType.UnsignedInt,
                            (void*)checked((nint)command.Offset0));
                        gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
                        break;

                    case OpenGLESCmdKind.Dispatch:
                        gl.DispatchCompute(command.GroupsX, command.GroupsY, command.GroupsZ);
                        break;

                    case OpenGLESCmdKind.DispatchIndirect:
                        gl.BindBuffer(BufferTargetARB.DispatchIndirectBuffer, ((OpenGLESGraphicsBuffer)command.Buffer0!).Handle);
                        gl.DispatchComputeIndirect(checked((nint)command.Offset0));
                        gl.BindBuffer(BufferTargetARB.DispatchIndirectBuffer, 0);
                        break;
                }
            }
        }
        finally
        {
            if (framebuffer != 0)
                gl.DeleteFramebuffer(framebuffer);
            if (readFramebuffer != 0)
                gl.DeleteFramebuffer(readFramebuffer);
            if (drawFramebuffer != 0)
                gl.DeleteFramebuffer(drawFramebuffer);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
            gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
            gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
            gl.BindBuffer(BufferTargetARB.DispatchIndirectBuffer, 0);
            gl.Disable(GLEnum.ScissorTest);
        }
    }

    private void BindSet(GL gl, in OpenGLESCmd command)
    {
        if (command.PipelineLayout is null || command.BindingLayout is null)
            throw new GraphicsValidationException("The recorded GLES descriptor set has no pipeline layout.");

        ReadOnlySpan<OpenGLESBindingValue> values = CollectionsMarshal.AsSpan(_bindingValues)
            .Slice(command.BindingValueOffset, command.BindingValueCount);
        ReadOnlySpan<GraphicsBindingLayoutEntry> entries = command.BindingLayout.Entries;
        int cursor = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            GraphicsBindingLayoutEntry entry = entries[i];
            if (!command.PipelineLayout.TryGetMapping(command.SetIndex, entry.Binding, out OpenGLESDescriptorMapping mapping))
                throw new GraphicsValidationException("The active GLES pipeline layout is missing a binding mapping.");

            for (uint element = 0; element < entry.Count; element++)
            {
                OpenGLESBindingValue value = values[cursor++];
                uint slot = mapping.BaseSlot + element;
                switch (entry.Type)
                {
                    case GraphicsDescriptorType.UniformBuffer:
                        if (value.Buffer is null) { gl.BindBufferBase(BufferTargetARB.UniformBuffer, slot, 0); break; }
                        gl.BindBufferRange(
                            BufferTargetARB.UniformBuffer, slot,
                            ((OpenGLESGraphicsBuffer)value.Buffer).Handle,
                            checked((nint)value.Offset), checked((nuint)value.Range));
                        break;

                    case GraphicsDescriptorType.StorageBuffer:
                        if (value.Buffer is null) { gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, slot, 0); break; }
                        gl.BindBufferRange(
                            BufferTargetARB.ShaderStorageBuffer, slot,
                            ((OpenGLESGraphicsBuffer)value.Buffer).Handle,
                            checked((nint)value.Offset), checked((nuint)value.Range));
                        break;

                    case GraphicsDescriptorType.SampledTexture:
                    case GraphicsDescriptorType.Sampler:
                    case GraphicsDescriptorType.CombinedTextureSampler:
                        gl.ActiveTexture((TextureUnit)(0x84C0 + slot));
                        gl.BindTexture(
                            value.Texture is null ? TextureTarget.Texture2D : ((OpenGLESGraphicsTexture)value.Texture.Texture).Target,
                            value.Texture is null ? 0 : ((OpenGLESGraphicsTexture)value.Texture.Texture).Handle);
                        gl.BindSampler(slot, value.Sampler is null ? 0 : ((OpenGLESGraphicsSampler)value.Sampler).Handle);
                        break;

                    case GraphicsDescriptorType.StorageTexture:
                        if (value.Texture is null)
                        {
                            gl.BindImageTexture(slot, 0, 0, false, 0, BufferAccessARB.ReadWrite, SizedInternalFormat.Rgba8);
                            break;
                        }
                        OpenGLESGraphicsTexture storageTexture = (OpenGLESGraphicsTexture)value.Texture.Texture;
                        gl.BindImageTexture(
                            slot,
                            storageTexture.Handle,
                            0,
                            false,
                            0,
                            BufferAccessARB.ReadWrite,
                            OpenGLESGraphicsTexture.GetInternalFormat(storageTexture.Description.Format));
                        break;

                    default:
                        throw new GraphicsValidationException($"Unsupported GLES descriptor type {entry.Type}.");
                }
            }
        }
    }

    private uint CreateFramebuffer(GL gl, in OpenGLESCmd command)
    {
        uint framebuffer = gl.GenFramebuffer();
        if (framebuffer == 0)
            throw new GraphicsException("OpenGL ES failed to create a framebuffer.");
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            for (int i = 0; i < command.ColorCount; i++)
            {
                OpenGLESGraphicsTexture texture = (OpenGLESGraphicsTexture)_renderTargetViews[command.ViewOffset + i].Texture;
                if (texture.Target != TextureTarget.Texture2D)
                    throw new GraphicsValidationException("GLES offscreen color attachments currently require 2D textures.");
                gl.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    GLEnum.ColorAttachment0,
                    TextureTarget.Texture2D,
                    texture.Handle,
                    0);
            }

            if (command.DepthTarget is not null)
            {
                OpenGLESGraphicsTexture texture = (OpenGLESGraphicsTexture)command.DepthTarget.Texture;
                if (texture.Target != TextureTarget.Texture2D)
                    throw new GraphicsValidationException("GLES depth attachments currently require 2D textures.");
                GraphicsFormat format = GetViewFormat(command.DepthTarget);
                GLEnum attachment = format == GraphicsFormat.D24UNormS8UInt
                    ? GLEnum.DepthStencilAttachment
                    : GLEnum.DepthAttachment;
                gl.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    attachment,
                    TextureTarget.Texture2D,
                    texture.Handle,
                    0);
            }

            GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
                throw new GraphicsException($"OpenGL ES framebuffer is incomplete: {status}.");
            return framebuffer;
        }
        catch
        {
            gl.DeleteFramebuffer(framebuffer);
            throw;
        }
    }

    private static void ValidateSnapshot(
        OpenGLESGraphicsBindingLayout layout,
        ReadOnlySpan<OpenGLESBindingValue> values)
    {
        ReadOnlySpan<GraphicsBindingLayoutEntry> entries = layout.Entries;
        int offset = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            GraphicsBindingLayoutEntry entry = entries[i];
            for (uint element = 0; element < entry.Count; element++)
            {
                OpenGLESBindingValue value = values[offset++];
                bool complete = entry.Type switch
                {
                    GraphicsDescriptorType.UniformBuffer or GraphicsDescriptorType.StorageBuffer => value.Buffer is not null,
                    GraphicsDescriptorType.SampledTexture or GraphicsDescriptorType.StorageTexture => value.Texture is not null,
                    GraphicsDescriptorType.Sampler => value.Sampler is not null,
                    GraphicsDescriptorType.CombinedTextureSampler => value.Texture is not null && value.Sampler is not null,
                    _ => false
                };
                bool empty = entry.Type switch
                {
                    GraphicsDescriptorType.UniformBuffer or GraphicsDescriptorType.StorageBuffer => value.Buffer is null,
                    GraphicsDescriptorType.SampledTexture or GraphicsDescriptorType.StorageTexture => value.Texture is null,
                    GraphicsDescriptorType.Sampler => value.Sampler is null,
                    GraphicsDescriptorType.CombinedTextureSampler => value.Texture is null && value.Sampler is null,
                    _ => false
                };
                if (!complete && !((entry.Flags & GraphicsBindingFlags.PartiallyBound) != 0 && empty))
                    throw new GraphicsValidationException(
                        $"GLES descriptor {entry.Binding}[{element}] is incomplete.");
            }
        }
    }

    private void ValidateIndirect(
        IGraphicsBuffer arguments,
        ulong offset,
        uint drawCount,
        uint commandSize)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        EnsureOwned(arguments);
        if ((arguments.Description.Usage & GraphicsBufferUsage.IndirectArguments) == 0)
            throw new GraphicsValidationException("Indirect commands require IndirectArguments buffer usage.");
        if ((offset & 3) != 0 || offset > arguments.Description.SizeInBytes ||
            commandSize > arguments.Description.SizeInBytes - offset)
            throw new GraphicsValidationException("The indirect command offset is unaligned or outside the buffer.");
        if (drawCount == 0)
            throw new ArgumentOutOfRangeException(nameof(drawCount));
    }

    private void ValidateRenderTarget(IGraphicsTextureView view, ref uint width, ref uint height)
    {
        ArgumentNullException.ThrowIfNull(view);
        EnsureOwned(view);
        if (view.Texture is not OpenGLESGraphicsTexture texture)
            throw new GraphicsValidationException("The render attachment is not an OpenGL ES texture.");
        if (texture.Target != TextureTarget.Texture2D ||
            texture.Description.SampleCount != 1 ||
            (texture.Description.Usage & (GraphicsTextureUsage.ColorAttachment | GraphicsTextureUsage.DepthStencilAttachment)) == 0)
            throw new GraphicsValidationException("The render attachment is not a supported single-sample 2D attachment.");
        if (width == 0) { width = texture.Description.Width; height = texture.Description.Height; }
        else if (width != texture.Description.Width || height != texture.Description.Height)
            throw new GraphicsValidationException("All framebuffer attachments must have matching dimensions.");
    }

    private void EnsureGraphicsReady()
    {
        EnsureGraphicsQueue();
        if (_renderPipeline is null || !_hasTargets || !_hasViewport)
            throw new GraphicsValidationException("A render pipeline, target and viewport must be selected before drawing.");
        for (uint set = 0; set < (uint)_pipelineLayout!.BindingLayouts.Length; set++)
            if (!_boundSets.Contains(set))
                throw new GraphicsValidationException($"Pipeline descriptor set {set} has not been bound.");
    }

    private void EnsureComputeReady()
    {
        EnsureComputeQueue();
        if (_computePipeline is null)
            throw new GraphicsValidationException("A compute pipeline must be selected before dispatch.");
        for (uint set = 0; set < (uint)_pipelineLayout!.BindingLayouts.Length; set++)
            if (!_boundSets.Contains(set))
                throw new GraphicsValidationException($"Compute descriptor set {set} has not been bound.");
    }

    private void EnsureGraphicsQueue()
    {
        if (QueueType != GraphicsQueueType.Graphics)
            throw new GraphicsValidationException("Render commands require the graphics queue.");
    }

    private void EnsureComputeQueue()
    {
        if (QueueType != GraphicsQueueType.Compute)
            throw new GraphicsValidationException("Compute commands require the compute queue.");
    }

    private void EnsureRecording()
    {
        ThrowIfDisposed();
        _lifecycle.EnsureRecording();
        if (_recordingThreadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("A GLES command list may only be recorded by the thread that called Begin.");
    }

    private void EnsureOwned(IGraphicsDeviceObject resource)
    {
        if (resource.IsDisposed || resource.Device.IsDisposed)
            throw new ObjectDisposedException(resource.GetType().Name);
        if (!ReferenceEquals(resource.Device, Device))
            throw new GraphicsValidationException("Command resources must belong to the command list's device.");

        if (resource is IOpenGLESNativeResource native)
            Retain(native);
        else if (resource is OpenGLESGraphicsTextureView view)
            Retain((IOpenGLESNativeResource)view.Texture);
    }

    private void Retain(IOpenGLESNativeResource resource)
    {
        if (_retained.Add(resource) && !resource.TryRetain())
        {
            _retained.Remove(resource);
            throw new ObjectDisposedException("OpenGL ES native resource");
        }
    }

    private static uint GetVertexStride(GraphicsVertexInputLayout layout, uint binding)
    {
        foreach (GraphicsVertexBufferLayout buffer in layout.Buffers)
            if (buffer.Binding == binding)
                return buffer.Stride;
        throw new GraphicsValidationException($"Vertex binding {binding} is not declared.");
    }

    private static GraphicsFormat GetViewFormat(IGraphicsTextureView view) =>
        view.Description.Format == GraphicsFormat.Unknown ? view.Texture.Description.Format : view.Description.Format;

    private static PrimitiveType GetPrimitive(GraphicsPrimitiveTopology topology) => topology switch
    {
        GraphicsPrimitiveTopology.PointList => PrimitiveType.Points,
        GraphicsPrimitiveTopology.LineList => PrimitiveType.Lines,
        GraphicsPrimitiveTopology.LineStrip => PrimitiveType.LineStrip,
        GraphicsPrimitiveTopology.TriangleList => PrimitiveType.Triangles,
        GraphicsPrimitiveTopology.TriangleStrip => PrimitiveType.TriangleStrip,
        GraphicsPrimitiveTopology.TriangleFan => PrimitiveType.TriangleFan,
        GraphicsPrimitiveTopology.PatchList => PrimitiveType.Patches,
        _ => throw new GraphicsValidationException($"Primitive topology {topology} is not supported by OpenGL ES.")
    };

    private void ClearRecordedState()
    {
        _commands.Clear();
        _renderTargetViews.Clear();
        _bindingValues.Clear();
        _renderPipeline = null;
        _computePipeline = null;
        _pipelineLayout = null;
        _boundSets.Clear();
        _indexBuffer = null;
        _indexOffset = 0;
        _hasTargets = false;
        _hasViewport = false;
        _hasDepthTarget = false;
        _colorCount = 0;
        _targetOffset = 0;
        foreach (IOpenGLESNativeResource resource in _retained)
            resource.ReleaseReference();
        _retained.Clear();
    }

    protected override void DisposeCore()
    {
        if (_lifecycle.State == GraphicsCommandListState.Pending)
            _queue.WaitForCompletion(this);

        if (_lifecycle.State is GraphicsCommandListState.Executable or GraphicsCommandListState.Initial)
        {
            _lifecycle.MarkDisposed();
        }
        ClearRecordedState();
    }
}
