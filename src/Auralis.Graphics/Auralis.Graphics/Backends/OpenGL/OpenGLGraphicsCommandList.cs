using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

internal enum OpenGLCommandKind : byte
{
    MemoryBarrier,
    CopyBuffer,
    CopyTexture
}

internal struct OpenGLRecordedCommand
{
    internal OpenGLCommandKind Kind;
    internal GraphicsResourceBarrier[]? Barriers;
    internal IGraphicsBuffer? SourceBuffer;
    internal IGraphicsBuffer? DestinationBuffer;
    internal IGraphicsTexture? SourceTexture;
    internal IGraphicsTexture? DestinationTexture;
    internal ulong SourceOffset;
    internal ulong DestinationOffset;
    internal ulong SizeInBytes;
}

/// <summary>
/// CPU-side deferred command recorder. Recording does not make the GL context current and never calls GL.
/// Commands retain their referenced resources until the list is reset or disposed.
/// </summary>
internal sealed class OpenGLGraphicsCommandList : GraphicsDeviceObject, IGraphicsCommandList
{
    private readonly OpenGLGraphicsCommandQueue _queue;
    private readonly GraphicsCommandListLifecycle _lifecycle;
    private readonly List<OpenGLRecordedCommand> _commands = new(64);

    internal OpenGLGraphicsCommandList(
        OpenGLGraphicsCommandQueue queue,
        GraphicsCommandListUsage usage) : base(queue.Device)
    {
        _queue = queue;
        Usage = usage;
        _lifecycle = new GraphicsCommandListLifecycle(queue.QueueType, usage);
    }

    public GraphicsQueueType QueueType => _queue.QueueType;
    public GraphicsCommandListUsage Usage { get; }
    public GraphicsCommandListState State
    {
        get
        {
            _queue.PollPending();
            return _lifecycle.State;
        }
    }

    internal ReadOnlySpan<OpenGLRecordedCommand> Commands => CollectionsMarshal.AsSpan(_commands);
    internal GraphicsCommandListLifecycle Lifecycle => _lifecycle;

    public void Begin()
    {
        ThrowIfDisposed();
        _lifecycle.Begin();
    }

    public void End()
    {
        ThrowIfDisposed();
        _lifecycle.End();
    }

    public void Reset()
    {
        ThrowIfDisposed();
        _lifecycle.Reset();
        _commands.Clear();
    }

    public void MemoryBarrier(ReadOnlySpan<GraphicsResourceBarrier> barriers)
    {
        EnsureRecording();
        if (barriers.IsEmpty)
            return;

        GraphicsResourceBarrier[] copy = barriers.ToArray();
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i].Validate();
            EnsureOwned(copy[i].Resource);
        }

        _commands.Add(new OpenGLRecordedCommand
        {
            Kind = OpenGLCommandKind.MemoryBarrier,
            Barriers = copy
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

        if ((source.Description.Usage & GraphicsBufferUsage.CopySource) == 0)
            throw new GraphicsValidationException("The source buffer was not created for copy-source use.");
        if ((destination.Description.Usage & GraphicsBufferUsage.CopyDestination) == 0)
            throw new GraphicsValidationException("The destination buffer was not created for copy-destination use.");

        new GraphicsBufferRange(sourceOffset, sizeInBytes).Validate(source.Description);
        new GraphicsBufferRange(destinationOffset, sizeInBytes).Validate(destination.Description);
        if (sourceOffset > (ulong)nint.MaxValue || destinationOffset > (ulong)nint.MaxValue ||
            sizeInBytes > (ulong)nuint.MaxValue)
            throw new GraphicsValidationException("The buffer copy range exceeds the native addressable size.");

        _commands.Add(new OpenGLRecordedCommand
        {
            Kind = OpenGLCommandKind.CopyBuffer,
            SourceBuffer = source,
            DestinationBuffer = destination,
            SourceOffset = sourceOffset,
            DestinationOffset = destinationOffset,
            SizeInBytes = sizeInBytes
        });
    }

    public void CopyTexture(IGraphicsTexture source, IGraphicsTexture destination)
    {
        EnsureRecording();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        EnsureOwned(source);
        EnsureOwned(destination);

        GraphicsTextureDescription sourceDescription = source.Description;
        GraphicsTextureDescription destinationDescription = destination.Description;
        if ((sourceDescription.Usage & GraphicsTextureUsage.CopySource) == 0 ||
            (destinationDescription.Usage & GraphicsTextureUsage.CopyDestination) == 0)
            throw new GraphicsValidationException("Texture copies require CopySource and CopyDestination usage respectively.");
        if (sourceDescription.Dimension != destinationDescription.Dimension ||
            sourceDescription.Width != destinationDescription.Width ||
            sourceDescription.Height != destinationDescription.Height ||
            sourceDescription.Depth != destinationDescription.Depth ||
            sourceDescription.ArrayLayers != destinationDescription.ArrayLayers ||
            sourceDescription.MipLevels != destinationDescription.MipLevels ||
            sourceDescription.SampleCount != destinationDescription.SampleCount ||
            sourceDescription.Format != destinationDescription.Format)
            throw new GraphicsValidationException("The source and destination texture descriptions must match for a whole-texture copy.");

        if (sourceDescription.SampleCount != 1)
            throw new GraphicsValidationException("OpenGL image copies do not support multisample textures in this backend.");

        _commands.Add(new OpenGLRecordedCommand
        {
            Kind = OpenGLCommandKind.CopyTexture,
            SourceTexture = source,
            DestinationTexture = destination
        });
    }

    public void SetRenderTargets(ReadOnlySpan<IGraphicsTextureView> colorTargets, IGraphicsTextureView? depthStencilTarget = null) =>
        throw Unsupported("Render-target binding");
    public void SetViewport(in GraphicsViewport viewport) => throw Unsupported("Viewport state");
    public void SetScissor(in GraphicsScissorRect scissor) => throw Unsupported("Scissor state");
    public void ClearColor(uint attachmentIndex, in GraphicsClearColor color) => throw Unsupported("Color clear");
    public void ClearColorUInt(uint attachmentIndex, in GraphicsClearColorUInt color) => throw Unsupported("Unsigned color clear");
    public void ClearColorSInt(uint attachmentIndex, in GraphicsClearColorSInt color) => throw Unsupported("Signed color clear");
    public void ClearDepthStencil(float depth, uint stencil, bool clearDepth = true, bool clearStencil = false) =>
        throw Unsupported("Depth/stencil clear");
    public void SetRenderPipeline(IGraphicsRenderPipeline pipeline) => throw Unsupported("Render pipelines");
    public void SetComputePipeline(IGraphicsComputePipeline pipeline) => throw Unsupported("Compute pipelines");
    public void SetBindingSet(uint setIndex, IGraphicsBindingSet bindingSet) => throw Unsupported("Descriptor binding sets");
    public void SetVertexBuffer(uint binding, IGraphicsBuffer buffer, ulong offset = 0) => throw Unsupported("Vertex buffers");
    public void SetIndexBuffer(IGraphicsBuffer buffer, GraphicsIndexFormat format, ulong offset = 0) => throw Unsupported("Index buffers");
    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0) => throw Unsupported("Draw");
    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int baseVertex = 0, uint firstInstance = 0) => throw Unsupported("Indexed draw");
    public void DrawIndirect(IGraphicsBuffer arguments, ulong offset, uint drawCount = 1, uint stride = 0) => throw Unsupported("Indirect draw");
    public void DrawIndexedIndirect(IGraphicsBuffer arguments, ulong offset, uint drawCount = 1, uint stride = 0) => throw Unsupported("Indexed indirect draw");
    public void Dispatch(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1) => throw Unsupported("Compute dispatch");
    public void DispatchIndirect(IGraphicsBuffer arguments, ulong offset) => throw Unsupported("Indirect compute dispatch");

    internal void Execute(GL gl, ArbDirectStateAccess dsa, ArbCopyImage copyImage)
    {
        ReadOnlySpan<OpenGLRecordedCommand> commands = Commands;
        for (int i = 0; i < commands.Length; i++)
        {
            ref readonly OpenGLRecordedCommand command = ref commands[i];
            switch (command.Kind)
            {
                case OpenGLCommandKind.MemoryBarrier:
                    ExecuteBarriers(gl, command.Barriers!);
                    break;
                case OpenGLCommandKind.CopyBuffer:
                    dsa.CopyNamedBufferSubData(
                        ((OpenGLGraphicsBuffer)command.SourceBuffer!).Handle,
                        ((OpenGLGraphicsBuffer)command.DestinationBuffer!).Handle,
                        checked((nint)command.SourceOffset),
                        checked((nint)command.DestinationOffset),
                        checked((nuint)command.SizeInBytes));
                    break;
                case OpenGLCommandKind.CopyTexture:
                    ExecuteTextureCopy(copyImage, command.SourceTexture!, command.DestinationTexture!);
                    break;
                default:
                    throw new GraphicsException($"Unknown recorded OpenGL command kind: {command.Kind}.");
            }
        }
    }

    private static void ExecuteBarriers(GL gl, ReadOnlySpan<GraphicsResourceBarrier> barriers)
    {
        MemoryBarrierMask mask = 0;
        for (int i = 0; i < barriers.Length; i++)
            mask |= OpenGLMemoryBarrierTranslator.ToMask(barriers[i]);

        if (mask != 0)
            gl.MemoryBarrier(mask);
    }

    private static void ExecuteTextureCopy(
        ArbCopyImage copyImage,
        IGraphicsTexture source,
        IGraphicsTexture destination)
    {
        OpenGLGraphicsTexture src = (OpenGLGraphicsTexture)source;
        OpenGLGraphicsTexture dst = (OpenGLGraphicsTexture)destination;
        GraphicsTextureDescription description = source.Description;
        TextureTarget target = OpenGLTextureTargetMapping.ForTexture(description);
        uint depth = description.Dimension switch
        {
            GraphicsTextureDimension.Texture1D => 1,
            GraphicsTextureDimension.Texture2D => 1,
            GraphicsTextureDimension.Texture3D => description.Depth,
            GraphicsTextureDimension.Cube => description.ArrayLayers,
            _ => 1
        };

        for (uint mip = 0; mip < description.MipLevels; mip++)
        {
            uint width = Math.Max(1u, description.Width >> checked((int)mip));
            uint height = description.Dimension == GraphicsTextureDimension.Texture1D
                ? 1u
                : Math.Max(1u, description.Height >> checked((int)mip));
            uint mipDepth = description.Dimension == GraphicsTextureDimension.Texture3D
                ? Math.Max(1u, depth >> checked((int)mip))
                : depth;

            copyImage.CopyImageSubData(
                src.Handle, (CopyImageSubDataTarget)target, checked((int)mip), 0, 0, 0,
                dst.Handle, (CopyImageSubDataTarget)target, checked((int)mip), 0, 0, 0,
                width, height, mipDepth);
        }
    }

    internal void Complete() => _lifecycle.MarkCompleted();

    internal void MarkSubmitted() => _lifecycle.MarkSubmitted();

    private void EnsureRecording()
    {
        ThrowIfDisposed();
        if (State != GraphicsCommandListState.Recording)
            _lifecycle.EnsureRecording();
    }

    private void EnsureOwned(IGraphicsDeviceObject resource)
    {
        if (resource.IsDisposed || resource.Device.IsDisposed)
            throw new ObjectDisposedException(resource.GetType().Name);
        if (!ReferenceEquals(resource.Device, Device))
            throw new GraphicsValidationException("Command resources must belong to the command list's device.");
    }

    private static NotSupportedException Unsupported(string operation) =>
        new($"{operation} is not implemented by the current OpenGL command execution increment.");

    protected override void DisposeCore()
    {
        _queue.WaitForCompletion(this);
        if (_lifecycle.State != GraphicsCommandListState.Disposed)
            _lifecycle.MarkDisposed();
        _commands.Clear();
    }
}
