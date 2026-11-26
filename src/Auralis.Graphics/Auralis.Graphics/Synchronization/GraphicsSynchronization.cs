using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
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
    AllCommands = 1 << 11,
    GeometryShader = 1 << 12,
    TessellationControlShader = 1 << 13,
    TessellationEvaluationShader = 1 << 14,
    TaskShader = 1 << 15,
    MeshShader = 1 << 16,
    FragmentShadingRate = 1 << 17,
    /// <summary>Ray-generation, miss, hit, and callable shader execution.</summary>
    RayTracingShader = 1 << 18,
    /// <summary>Acceleration-structure build and update execution.</summary>
    AccelerationStructureBuild = 1 << 19,
    /// <summary>Acceleration-structure clone and compaction copy execution.</summary>
    AccelerationStructureCopy = 1 << 20,

    /// <summary>All graphics execution stages, including indirect drawing and mesh/task shading.</summary>
    AllGraphics =
        DrawIndirect |
        VertexInput |
        VertexShader |
        TessellationControlShader |
        TessellationEvaluationShader |
        GeometryShader |
        TaskShader |
        MeshShader |
        FragmentShadingRate |
        FragmentShader |
        DepthStencil |
        ColorAttachmentOutput
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
    MemoryWrite = 1 << 15,
    ShadingRateAttachmentRead = 1 << 16,
    /// <summary>Reads an acceleration structure from a ray-tracing shader, build, or AS copy operation.</summary>
    AccelerationStructureRead = 1 << 17,
    /// <summary>Writes an acceleration structure during a build, update, or AS copy operation.</summary>
    AccelerationStructureWrite = 1 << 18
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
    Present,
    ShadingRateAttachment
}

/// <summary>A byte range within a buffer. A null range on a barrier represents the entire buffer.</summary>
public readonly record struct GraphicsBufferRange(ulong Offset, ulong Size)
{
    public void Validate(in GraphicsBufferDescription buffer)
    {
        buffer.Validate();

        if (Size == 0)
            throw new ArgumentOutOfRangeException(nameof(Size), "A buffer range must contain at least one byte.");
        if (Offset > buffer.SizeInBytes || Size > buffer.SizeInBytes - Offset)
            throw new ArgumentOutOfRangeException(nameof(Size), "The buffer range exceeds the buffer bounds.");
    }
}

/// <summary>Texture aspects that can be selected by a subresource range.</summary>
[Flags]
public enum GraphicsTextureAspect
{
    None = 0,
    Color = 1 << 0,
    Depth = 1 << 1,
    Stencil = 1 << 2
}

/// <summary>
/// A range of texture mip levels and array layers. A null range on a barrier represents the entire texture.
/// Array layers are texture array layers (cube faces are individual layers); 3D textures use their single array layer.
/// </summary>
public readonly record struct GraphicsTextureSubresourceRange(
    GraphicsTextureAspect Aspects,
    uint BaseMipLevel,
    uint MipLevelCount,
    uint BaseArrayLayer,
    uint ArrayLayerCount)
{
    public void Validate(in GraphicsTextureDescription texture)
    {
        texture.Validate();

        const GraphicsTextureAspect allAspects =
            GraphicsTextureAspect.Color | GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil;

        if (Aspects == GraphicsTextureAspect.None || (Aspects & ~allAspects) != 0)
            throw new ArgumentOutOfRangeException(nameof(Aspects), "A texture range must select one or more valid aspects.");

        GraphicsTextureAspect availableAspects = GetAvailableAspects(texture.Format);
        if ((Aspects & ~availableAspects) != 0)
            throw new ArgumentException(
                $"Format {texture.Format} does not contain all requested texture aspects ({Aspects}).",
                nameof(Aspects));

        if (MipLevelCount == 0 || BaseMipLevel >= texture.MipLevels ||
            MipLevelCount > texture.MipLevels - BaseMipLevel)
            throw new ArgumentOutOfRangeException(nameof(MipLevelCount), "The mip range exceeds the texture bounds.");

        if (ArrayLayerCount == 0 || BaseArrayLayer >= texture.ArrayLayers ||
            ArrayLayerCount > texture.ArrayLayers - BaseArrayLayer)
            throw new ArgumentOutOfRangeException(nameof(ArrayLayerCount), "The array-layer range exceeds the texture bounds.");
    }

    private static GraphicsTextureAspect GetAvailableAspects(GraphicsFormat format) => format switch
    {
        GraphicsFormat.D16UNorm or GraphicsFormat.D32Float =>
            GraphicsTextureAspect.Depth,
        GraphicsFormat.S8UInt => GraphicsTextureAspect.Stencil,
        GraphicsFormat.D24UNormS8UInt or GraphicsFormat.D32FloatS8UInt =>
            GraphicsTextureAspect.Depth | GraphicsTextureAspect.Stencil,
        GraphicsFormat.Unknown => GraphicsTextureAspect.None,
        _ => GraphicsTextureAspect.Color
    };
}

/// <summary>
/// Compatibility rules between access intents, resource kinds/usages, and pipeline execution stages.
/// Keeping the rules in one table makes additions auditable and avoids silently accepting unsupported pairs.
/// </summary>
internal static class GraphicsBarrierCompatibility
{
    private const GraphicsPipelineStage ShaderStages =
        GraphicsPipelineStage.VertexShader |
        GraphicsPipelineStage.TessellationControlShader |
        GraphicsPipelineStage.TessellationEvaluationShader |
        GraphicsPipelineStage.GeometryShader |
        GraphicsPipelineStage.TaskShader |
        GraphicsPipelineStage.MeshShader |
        GraphicsPipelineStage.FragmentShader |
        GraphicsPipelineStage.ComputeShader |
        GraphicsPipelineStage.RayTracingShader |
        GraphicsPipelineStage.AllCommands;

    private const GraphicsPipelineStage ExecutionStages =
        GraphicsPipelineStage.DrawIndirect |
        GraphicsPipelineStage.VertexInput |
        GraphicsPipelineStage.VertexShader |
        GraphicsPipelineStage.TessellationControlShader |
        GraphicsPipelineStage.TessellationEvaluationShader |
        GraphicsPipelineStage.GeometryShader |
        GraphicsPipelineStage.TaskShader |
        GraphicsPipelineStage.MeshShader |
        GraphicsPipelineStage.FragmentShadingRate |
        GraphicsPipelineStage.FragmentShader |
        GraphicsPipelineStage.ComputeShader |
        GraphicsPipelineStage.ColorAttachmentOutput |
        GraphicsPipelineStage.DepthStencil |
        GraphicsPipelineStage.Transfer |
        GraphicsPipelineStage.Host |
        GraphicsPipelineStage.RayTracingShader |
        GraphicsPipelineStage.AccelerationStructureBuild |
        GraphicsPipelineStage.AccelerationStructureCopy |
        GraphicsPipelineStage.AllCommands;

    private readonly record struct AccessRule(
        GraphicsResourceAccess Access,
        bool SupportsBuffer,
        bool SupportsTexture,
        GraphicsBufferUsage BufferUsage,
        GraphicsTextureUsage TextureUsage,
        GraphicsPipelineStage Stages,
        bool AnyTextureUsage = false,
        bool SupportsAccelerationStructure = false);

    private static readonly AccessRule[] s_accessRules =
    [
        new(GraphicsResourceAccess.IndirectRead, true, false,
            GraphicsBufferUsage.IndirectArguments, GraphicsTextureUsage.None,
            GraphicsPipelineStage.DrawIndirect | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.IndexRead, true, false,
            GraphicsBufferUsage.Index, GraphicsTextureUsage.None,
            GraphicsPipelineStage.VertexInput | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.VertexRead, true, false,
            GraphicsBufferUsage.Vertex, GraphicsTextureUsage.None,
            GraphicsPipelineStage.VertexInput | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.UniformRead, true, false,
            GraphicsBufferUsage.Constant, GraphicsTextureUsage.None,
            ShaderStages),
        new(GraphicsResourceAccess.ShaderRead, true, true,
            GraphicsBufferUsage.Storage, GraphicsTextureUsage.Sampled | GraphicsTextureUsage.Storage,
            ShaderStages, AnyTextureUsage: true),
        new(GraphicsResourceAccess.ShaderWrite, true, true,
            GraphicsBufferUsage.Storage, GraphicsTextureUsage.Storage,
            ShaderStages),
        new(GraphicsResourceAccess.ColorAttachmentRead, false, true,
            GraphicsBufferUsage.None, GraphicsTextureUsage.ColorAttachment,
            GraphicsPipelineStage.ColorAttachmentOutput | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.ColorAttachmentWrite, false, true,
            GraphicsBufferUsage.None, GraphicsTextureUsage.ColorAttachment,
            GraphicsPipelineStage.ColorAttachmentOutput | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.DepthStencilRead, false, true,
            GraphicsBufferUsage.None, GraphicsTextureUsage.DepthStencilAttachment,
            GraphicsPipelineStage.DepthStencil | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.DepthStencilWrite, false, true,
            GraphicsBufferUsage.None, GraphicsTextureUsage.DepthStencilAttachment,
            GraphicsPipelineStage.DepthStencil | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.TransferRead, true, true,
            GraphicsBufferUsage.CopySource, GraphicsTextureUsage.CopySource,
            GraphicsPipelineStage.Transfer | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.TransferWrite, true, true,
            GraphicsBufferUsage.CopyDestination, GraphicsTextureUsage.CopyDestination,
            GraphicsPipelineStage.Transfer | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.HostRead, true, false,
            GraphicsBufferUsage.None, GraphicsTextureUsage.None,
            GraphicsPipelineStage.Host),
        new(GraphicsResourceAccess.HostWrite, true, false,
            GraphicsBufferUsage.None, GraphicsTextureUsage.None,
            GraphicsPipelineStage.Host),
        new(GraphicsResourceAccess.MemoryRead, true, true,
            GraphicsBufferUsage.None, GraphicsTextureUsage.None, ExecutionStages,
            SupportsAccelerationStructure: true),
        new(GraphicsResourceAccess.MemoryWrite, true, true,
            GraphicsBufferUsage.None, GraphicsTextureUsage.None, ExecutionStages,
            SupportsAccelerationStructure: true),
        new(GraphicsResourceAccess.ShadingRateAttachmentRead, false, true,
            GraphicsBufferUsage.None, GraphicsTextureUsage.ShadingRateAttachment,
            GraphicsPipelineStage.FragmentShadingRate | GraphicsPipelineStage.AllCommands),
        new(GraphicsResourceAccess.AccelerationStructureRead, false, false,
            GraphicsBufferUsage.None, GraphicsTextureUsage.None,
            GraphicsPipelineStage.RayTracingShader | GraphicsPipelineStage.AccelerationStructureBuild |
            GraphicsPipelineStage.AccelerationStructureCopy | GraphicsPipelineStage.AllCommands,
            SupportsAccelerationStructure: true),
        new(GraphicsResourceAccess.AccelerationStructureWrite, false, false,
            GraphicsBufferUsage.None, GraphicsTextureUsage.None,
            GraphicsPipelineStage.AccelerationStructureBuild | GraphicsPipelineStage.AccelerationStructureCopy |
            GraphicsPipelineStage.AllCommands,
            SupportsAccelerationStructure: true)
    ];

    public static void ValidateAccess(
        IGraphicsResource resource,
        GraphicsPipelineStage stages,
        GraphicsResourceAccess access)
    {
        if (access == GraphicsResourceAccess.None)
            return;

        bool isBuffer = resource is IGraphicsBuffer;
        bool isTexture = resource is IGraphicsTexture;
        bool isAccelerationStructure = resource is IGraphicsAccelerationStructure;
        if (!isBuffer && !isTexture && !isAccelerationStructure)
            throw new ArgumentException("Barrier access validation supports buffers, textures, and acceleration structures.", nameof(resource));

        GraphicsBufferDescription buffer = isBuffer
            ? ((IGraphicsBuffer)resource).Description
            : default;
        GraphicsTextureDescription texture = isTexture
            ? ((IGraphicsTexture)resource).Description
            : default;

        for (int i = 0; i < s_accessRules.Length; i++)
        {
            AccessRule rule = s_accessRules[i];
            if ((access & rule.Access) == 0)
                continue;

            if ((isBuffer && !rule.SupportsBuffer) ||
                (isTexture && !rule.SupportsTexture) ||
                (isAccelerationStructure && !rule.SupportsAccelerationStructure))
                throw new ArgumentException(
                    $"Access {rule.Access} is not valid for resource type {resource.GetType().Name}.",
                    nameof(access));

            if ((stages & rule.Stages) == 0)
                throw new ArgumentException(
                    $"Access {rule.Access} is not compatible with pipeline stages {stages}.",
                    nameof(stages));

            if (isBuffer && rule.BufferUsage != GraphicsBufferUsage.None &&
                (buffer.Usage & rule.BufferUsage) != rule.BufferUsage)
                throw new ArgumentException(
                    $"Buffer access {rule.Access} requires usage {rule.BufferUsage}.",
                    nameof(resource));

            if (isTexture && rule.TextureUsage != GraphicsTextureUsage.None)
            {
                bool usageSupported = rule.AnyTextureUsage
                    ? (texture.Usage & rule.TextureUsage) != 0
                    : (texture.Usage & rule.TextureUsage) == rule.TextureUsage;
                if (!usageSupported)
                    throw new ArgumentException(
                        $"Texture access {rule.Access} requires one of the declared usages {rule.TextureUsage}.",
                        nameof(resource));
            }

            if (rule.Access == GraphicsResourceAccess.HostRead &&
                buffer.MemoryAccess is not (GraphicsMemoryAccess.GpuToCpu or GraphicsMemoryAccess.CpuReadWrite))
                throw new ArgumentException("HostRead requires a CPU-readable buffer memory access mode.", nameof(resource));

            if (rule.Access == GraphicsResourceAccess.HostWrite &&
                buffer.MemoryAccess is not (GraphicsMemoryAccess.CpuToGpu or GraphicsMemoryAccess.CpuReadWrite))
                throw new ArgumentException("HostWrite requires a CPU-writable buffer memory access mode.", nameof(resource));
        }
    }

    public static void ValidateTextureLayout(
        GraphicsTextureLayout layout,
        GraphicsTextureUsage usage,
        bool isNewLayout)
    {
        if (layout == GraphicsTextureLayout.NotApplicable)
            throw new ArgumentException("Texture barriers must specify texture layouts.");

        if (layout == GraphicsTextureLayout.Undefined)
        {
            if (isNewLayout)
                throw new ArgumentException("Undefined is valid only as an old texture layout.");
            return;
        }

        GraphicsTextureUsage requiredUsage = layout switch
        {
            GraphicsTextureLayout.General => GraphicsTextureUsage.None,
            GraphicsTextureLayout.ShaderReadOnly => GraphicsTextureUsage.Sampled,
            GraphicsTextureLayout.ColorAttachment => GraphicsTextureUsage.ColorAttachment,
            GraphicsTextureLayout.DepthStencilAttachment => GraphicsTextureUsage.DepthStencilAttachment,
            GraphicsTextureLayout.TransferSource => GraphicsTextureUsage.CopySource,
            GraphicsTextureLayout.TransferDestination => GraphicsTextureUsage.CopyDestination,
            GraphicsTextureLayout.Present => GraphicsTextureUsage.Present,
            GraphicsTextureLayout.ShadingRateAttachment => GraphicsTextureUsage.ShadingRateAttachment,
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };

        if (requiredUsage != GraphicsTextureUsage.None && (usage & requiredUsage) != requiredUsage)
            throw new ArgumentException(
                $"Texture layout {layout} requires declared texture usage {requiredUsage}.",
                nameof(layout));
    }
}

/// <summary>
/// Identifies which queue-family ownership transfer half the barrier represents.
/// A Release barrier is recorded on SourceQueue and supplies only the source stage/access scope; its
/// destination scope is ignored (None). An Acquire barrier is recorded on DestinationQueue and supplies
/// only the destination stage/access scope; its source scope is ignored (None). The application orders
/// those submissions using a source signal and destination queue wait.
/// </summary>
public enum GraphicsQueueOwnershipTransferPhase
{
    /// <summary>Record the release half on SourceQueue.</summary>
    Release,
    /// <summary>Record the acquire half on DestinationQueue.</summary>
    Acquire
}

/// <summary>One half of a split queue-family ownership transfer. Both halves describe the same resource range and layout transition.</summary>
public readonly record struct GraphicsQueueOwnershipTransfer(
    IGraphicsQueue SourceQueue,
    IGraphicsQueue DestinationQueue,
    GraphicsQueueOwnershipTransferPhase Phase);

/// <summary>
/// One resource dependency to be translated into backend-native synchronization.
/// Range fields are mutually exclusive. Null ranges select the whole resource. Acceleration structures are
/// first-class synchronization resources with dedicated access intents and execution stages, but no buffer
/// byte ranges or texture layouts. Generic MemoryRead/MemoryWrite dependencies are also valid. AS creation
/// owns full description validation, so barrier validation checks resource kind and synchronization semantics
/// without walking BLAS geometry again. Texture views must be resolved to their underlying texture.
/// </summary>
public readonly record struct GraphicsResourceBarrier(
    IGraphicsResource Resource,
    GraphicsPipelineStage SourceStages,
    GraphicsPipelineStage DestinationStages,
    GraphicsResourceAccess SourceAccess,
    GraphicsResourceAccess DestinationAccess,
    GraphicsTextureLayout OldLayout = GraphicsTextureLayout.NotApplicable,
    GraphicsTextureLayout NewLayout = GraphicsTextureLayout.NotApplicable,
    GraphicsBufferRange? BufferRange = null,
    GraphicsTextureSubresourceRange? TextureRange = null,
    GraphicsQueueOwnershipTransfer? OwnershipTransfer = null)
{
    /// <summary>Validates the barrier and verifies that this queue is the queue required for its ownership-transfer phase.</summary>
    public void Validate(IGraphicsQueue recordingQueue)
    {
        ArgumentNullException.ThrowIfNull(recordingQueue);
        ValidateCore();
        if (!ReferenceEquals(recordingQueue.Device, Resource.Device))
            throw new ArgumentException("The recording queue and barrier resource must belong to the same device.", nameof(recordingQueue));

        if (OwnershipTransfer is { } transfer)
        {
            IGraphicsQueue expectedQueue = transfer.Phase == GraphicsQueueOwnershipTransferPhase.Release
                ? transfer.SourceQueue
                : transfer.DestinationQueue;
            if (recordingQueue.QueueFamilyIndex != expectedQueue.QueueFamilyIndex ||
                recordingQueue.Index != expectedQueue.Index)
                throw new ArgumentException(
                    $"The {transfer.Phase} ownership barrier must be recorded on the corresponding source/destination queue.",
                    nameof(recordingQueue));
        }
    }

    public void Validate()
    {
        ValidateCore();
        if (OwnershipTransfer is not null)
            throw new InvalidOperationException("Queue ownership-transfer barriers must be validated with Validate(recordingQueue).");
    }

    private void ValidateCore()
    {
        ArgumentNullException.ThrowIfNull(Resource);

        const GraphicsPipelineStage allStages =
            GraphicsPipelineStage.TopOfPipe | GraphicsPipelineStage.DrawIndirect |
            GraphicsPipelineStage.VertexInput | GraphicsPipelineStage.VertexShader |
            GraphicsPipelineStage.FragmentShader | GraphicsPipelineStage.ComputeShader |
            GraphicsPipelineStage.ColorAttachmentOutput | GraphicsPipelineStage.DepthStencil |
            GraphicsPipelineStage.Transfer | GraphicsPipelineStage.Host |
            GraphicsPipelineStage.BottomOfPipe | GraphicsPipelineStage.AllCommands |
            GraphicsPipelineStage.GeometryShader | GraphicsPipelineStage.TessellationControlShader |
            GraphicsPipelineStage.TessellationEvaluationShader | GraphicsPipelineStage.TaskShader |
            GraphicsPipelineStage.MeshShader | GraphicsPipelineStage.FragmentShadingRate |
            GraphicsPipelineStage.RayTracingShader | GraphicsPipelineStage.AccelerationStructureBuild |
            GraphicsPipelineStage.AccelerationStructureCopy;
        const GraphicsResourceAccess allAccess =
            GraphicsResourceAccess.IndirectRead | GraphicsResourceAccess.IndexRead |
            GraphicsResourceAccess.VertexRead | GraphicsResourceAccess.UniformRead |
            GraphicsResourceAccess.ShaderRead | GraphicsResourceAccess.ShaderWrite |
            GraphicsResourceAccess.ColorAttachmentRead | GraphicsResourceAccess.ColorAttachmentWrite |
            GraphicsResourceAccess.DepthStencilRead | GraphicsResourceAccess.DepthStencilWrite |
            GraphicsResourceAccess.TransferRead | GraphicsResourceAccess.TransferWrite |
            GraphicsResourceAccess.HostRead | GraphicsResourceAccess.HostWrite |
            GraphicsResourceAccess.MemoryRead | GraphicsResourceAccess.MemoryWrite |
            GraphicsResourceAccess.ShadingRateAttachmentRead |
            GraphicsResourceAccess.AccelerationStructureRead | GraphicsResourceAccess.AccelerationStructureWrite;

        if (Resource.IsDisposed || Resource.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(Resource));
        if (OwnershipTransfer is { } ownership)
        {
            ArgumentNullException.ThrowIfNull(ownership.SourceQueue);
            ArgumentNullException.ThrowIfNull(ownership.DestinationQueue);
            if (!Enum.IsDefined(ownership.Phase))
                throw new ArgumentOutOfRangeException(nameof(OwnershipTransfer));
            if (!ReferenceEquals(ownership.SourceQueue.Device, Resource.Device) ||
                !ReferenceEquals(ownership.DestinationQueue.Device, Resource.Device))
                throw new ArgumentException("Ownership-transfer queues and resource must belong to the same device.", nameof(OwnershipTransfer));
            if (ownership.SourceQueue.QueueFamilyIndex == ownership.DestinationQueue.QueueFamilyIndex)
                throw new ArgumentException("Ownership transfers require different queue families.", nameof(OwnershipTransfer));
            if (ownership.Phase == GraphicsQueueOwnershipTransferPhase.Release &&
                DestinationAccess != GraphicsResourceAccess.None)
                throw new ArgumentException("Release barriers must not declare destination access.", nameof(DestinationAccess));
            if (ownership.Phase == GraphicsQueueOwnershipTransferPhase.Acquire &&
                SourceAccess != GraphicsResourceAccess.None)
                throw new ArgumentException("Acquire barriers must not declare source access.", nameof(SourceAccess));
        }
        if ((SourceStages & ~allStages) != 0 || (DestinationStages & ~allStages) != 0)
            throw new ArgumentException("Resource barriers contain unknown pipeline stages.");

        if (OwnershipTransfer is not { } stageTransfer)
        {
            if (SourceStages == GraphicsPipelineStage.None || DestinationStages == GraphicsPipelineStage.None)
                throw new ArgumentException("Ordinary barriers require non-empty source and destination stage scopes.");
        }
        else if (stageTransfer.Phase == GraphicsQueueOwnershipTransferPhase.Release)
        {
            if (SourceStages == GraphicsPipelineStage.None || DestinationStages != GraphicsPipelineStage.None)
                throw new ArgumentException(
                    "Release barriers require a source stage scope and use None for the ignored destination stage scope.",
                    nameof(DestinationStages));
        }
        else if (stageTransfer.Phase == GraphicsQueueOwnershipTransferPhase.Acquire)
        {
            if (SourceStages != GraphicsPipelineStage.None || DestinationStages == GraphicsPipelineStage.None)
                throw new ArgumentException(
                    "Acquire barriers use None for the ignored source stage scope and require a destination stage scope.",
                    nameof(SourceStages));
        }
        if ((SourceAccess & ~allAccess) != 0)
            throw new ArgumentOutOfRangeException(nameof(SourceAccess), "Source access flags contain unknown values.");
        if ((DestinationAccess & ~allAccess) != 0)
            throw new ArgumentOutOfRangeException(nameof(DestinationAccess), "Destination access flags contain unknown values.");
        if (!Enum.IsDefined(OldLayout))
            throw new ArgumentOutOfRangeException(nameof(OldLayout));
        if (!Enum.IsDefined(NewLayout))
            throw new ArgumentOutOfRangeException(nameof(NewLayout));

        switch (Resource)
        {
            case IGraphicsBuffer buffer:
                buffer.Description.Validate();
                if (BufferRange is not null)
                    BufferRange.Value.Validate(buffer.Description);
                if (TextureRange is not null)
                    throw new ArgumentException("Buffer barriers cannot specify a texture subresource range.", nameof(TextureRange));
                if (OldLayout != GraphicsTextureLayout.NotApplicable ||
                    NewLayout != GraphicsTextureLayout.NotApplicable)
                    throw new ArgumentException("Buffer barriers must use NotApplicable texture layouts.");
                break;

            case IGraphicsTexture texture:
                texture.Description.Validate();
                if (BufferRange is not null)
                    throw new ArgumentException("Texture barriers cannot specify a buffer byte range.", nameof(BufferRange));
                TextureRange?.Validate(texture.Description);
                if (OldLayout == GraphicsTextureLayout.Undefined && SourceAccess != GraphicsResourceAccess.None)
                    throw new ArgumentException(
                        "An Undefined old texture layout discards prior contents and requires no source access.",
                        nameof(SourceAccess));
                GraphicsBarrierCompatibility.ValidateTextureLayout(
                    OldLayout, texture.Description.Usage, isNewLayout: false);
                GraphicsBarrierCompatibility.ValidateTextureLayout(
                    NewLayout, texture.Description.Usage, isNewLayout: true);
                break;

            case IGraphicsAccelerationStructure accelerationStructure:
                ArgumentNullException.ThrowIfNull(accelerationStructure.Description);
                if (accelerationStructure.IsTopLevel != accelerationStructure.Description.IsTopLevel)
                    throw new ArgumentException("The acceleration-structure kind does not match its description.", nameof(Resource));
                if (BufferRange is not null)
                    throw new ArgumentException("Acceleration-structure barriers cannot specify buffer ranges.", nameof(BufferRange));
                if (TextureRange is not null)
                    throw new ArgumentException("Acceleration-structure barriers cannot specify texture subresource ranges.", nameof(TextureRange));
                if (OldLayout != GraphicsTextureLayout.NotApplicable ||
                    NewLayout != GraphicsTextureLayout.NotApplicable)
                    throw new ArgumentException("Acceleration-structure barriers must use NotApplicable texture layouts.");
                break;

            case IGraphicsTextureView:
                throw new ArgumentException(
                    "Texture-view barriers are not supported. Specify the underlying texture and an explicit subresource range.",
                    nameof(Resource));

            default:
                throw new ArgumentException(
                    $"Barriers support buffers and textures, not resources of type {Resource.GetType().Name}.",
                    nameof(Resource));
        }

        GraphicsBarrierCompatibility.ValidateAccess(Resource, SourceStages, SourceAccess);
        GraphicsBarrierCompatibility.ValidateAccess(Resource, DestinationStages, DestinationAccess);
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

    ValueTask<bool> WaitAsync(ulong value, TimeSpan timeout, CancellationToken cancellationToken = default);
}
