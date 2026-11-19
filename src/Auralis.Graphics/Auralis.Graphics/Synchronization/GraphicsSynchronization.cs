using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Synchronization;

/// <summary>Abstract execution stages used when describing resource dependencies.</summary>
[Flags]
public enum GraphicsPipelineStage
{
    None = 0,
    TopOfPipe = 1 << 0,
    DrawIndirect = 1 << 1,
    VertexInput = 1 << 2,
    VertexShader = 1 << 3,
    FragmentShader = 1 << 4,
    ComputeShader = 1 << 5,
    ColorAttachmentOutput = 1 << 6,
    DepthStencil = 1 << 7,
    Transfer = 1 << 8,
    Host = 1 << 9,
    BottomOfPipe = 1 << 10,
    AllGraphics = VertexInput | VertexShader | FragmentShader | ColorAttachmentOutput | DepthStencil,
    AllCommands = 1 << 11
}

/// <summary>Abstract read/write intents used by resource barriers.</summary>
[Flags]
public enum GraphicsResourceAccess
{
    None = 0,
    IndirectRead = 1 << 0,
    IndexRead = 1 << 1,
    VertexRead = 1 << 2,
    UniformRead = 1 << 3,
    ShaderRead = 1 << 4,
    ShaderWrite = 1 << 5,
    ColorAttachmentRead = 1 << 6,
    ColorAttachmentWrite = 1 << 7,
    DepthStencilRead = 1 << 8,
    DepthStencilWrite = 1 << 9,
    TransferRead = 1 << 10,
    TransferWrite = 1 << 11,
    HostRead = 1 << 12,
    HostWrite = 1 << 13,
    MemoryRead = 1 << 14,
    MemoryWrite = 1 << 15
}

/// <summary>
/// Semantic texture layouts. These are not native API layouts or Direct3D resource states;
/// backends translate them to the synchronization model of their API.
/// </summary>
public enum GraphicsTextureLayout
{
    NotApplicable,
    Undefined,
    General,
    ShaderReadOnly,
    ColorAttachment,
    DepthStencilAttachment,
    TransferSource,
    TransferDestination,
    Present
}

/// <summary>One resource dependency to be translated into backend-native synchronization.</summary>
public readonly record struct GraphicsResourceBarrier(
    IGraphicsResource Resource,
    GraphicsPipelineStage SourceStages,
    GraphicsPipelineStage DestinationStages,
    GraphicsResourceAccess SourceAccess,
    GraphicsResourceAccess DestinationAccess,
    GraphicsTextureLayout OldLayout = GraphicsTextureLayout.NotApplicable,
    GraphicsTextureLayout NewLayout = GraphicsTextureLayout.NotApplicable)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Resource);

        const GraphicsPipelineStage allStages =
            GraphicsPipelineStage.TopOfPipe | GraphicsPipelineStage.DrawIndirect |
            GraphicsPipelineStage.VertexInput | GraphicsPipelineStage.VertexShader |
            GraphicsPipelineStage.FragmentShader | GraphicsPipelineStage.ComputeShader |
            GraphicsPipelineStage.ColorAttachmentOutput | GraphicsPipelineStage.DepthStencil |
            GraphicsPipelineStage.Transfer | GraphicsPipelineStage.Host |
            GraphicsPipelineStage.BottomOfPipe | GraphicsPipelineStage.AllCommands;
        const GraphicsResourceAccess allAccess =
            GraphicsResourceAccess.IndirectRead | GraphicsResourceAccess.IndexRead |
            GraphicsResourceAccess.VertexRead | GraphicsResourceAccess.UniformRead |
            GraphicsResourceAccess.ShaderRead | GraphicsResourceAccess.ShaderWrite |
            GraphicsResourceAccess.ColorAttachmentRead | GraphicsResourceAccess.ColorAttachmentWrite |
            GraphicsResourceAccess.DepthStencilRead | GraphicsResourceAccess.DepthStencilWrite |
            GraphicsResourceAccess.TransferRead | GraphicsResourceAccess.TransferWrite |
            GraphicsResourceAccess.HostRead | GraphicsResourceAccess.HostWrite |
            GraphicsResourceAccess.MemoryRead | GraphicsResourceAccess.MemoryWrite;

        if (Resource.IsDisposed || Resource.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(Resource));
        if (SourceStages == GraphicsPipelineStage.None || DestinationStages == GraphicsPipelineStage.None ||
            (SourceStages & ~allStages) != 0 || (DestinationStages & ~allStages) != 0)
            throw new ArgumentException("Resource barriers require valid, non-empty source and destination stages.");
        if ((SourceAccess & ~allAccess) != 0 || (DestinationAccess & ~allAccess) != 0)
            throw new ArgumentOutOfRangeException(nameof(SourceAccess), "Resource access flags contain unknown values.");
        if (!Enum.IsDefined(OldLayout) || !Enum.IsDefined(NewLayout))
            throw new ArgumentOutOfRangeException(nameof(OldLayout));

        bool isTexture = Resource is IGraphicsTexture;
        if (isTexture && (OldLayout == GraphicsTextureLayout.NotApplicable ||
                          NewLayout == GraphicsTextureLayout.NotApplicable))
            throw new ArgumentException("Texture barriers must specify old and new semantic layouts.");
        if (!isTexture && (OldLayout != GraphicsTextureLayout.NotApplicable ||
                           NewLayout != GraphicsTextureLayout.NotApplicable))
            throw new ArgumentException("Buffer barriers must use NotApplicable texture layouts.");
    }
}

/// <summary>
/// Timeline-style synchronization primitive. Wait is a CPU wait; queue Wait methods enqueue GPU waits.
/// Native backends may implement this contract with fences, timeline semaphores, or equivalent primitives.
/// </summary>
public interface IGraphicsFence : IGraphicsDeviceObject
{
    ulong CompletedValue { get; }

    void Signal(ulong value);

    bool Wait(ulong value, TimeSpan timeout, CancellationToken cancellationToken = default);
}
