using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Commands;

public enum GraphicsAttachmentLoadOperation { Load, Clear, DontCare }
public enum GraphicsAttachmentStoreOperation { Store, DontCare }

public readonly record struct GraphicsClearColor(float Red, float Green, float Blue, float Alpha);
public readonly record struct GraphicsClearDepthStencil(float Depth, uint Stencil);

/// <summary>Color attachment for a dynamic rendering scope. Resolve is optional and requires compatible multisample resources.</summary>
public readonly record struct GraphicsColorAttachment(
    IGraphicsTextureView View,
    GraphicsAttachmentLoadOperation LoadOperation,
    GraphicsAttachmentStoreOperation StoreOperation,
    GraphicsClearColor ClearValue,
    IGraphicsTextureView? ResolveView = null);

/// <summary>Depth/stencil attachment. A view may expose depth, stencil, or both aspects.</summary>
public readonly record struct GraphicsDepthStencilAttachment(
    IGraphicsTextureView View,
    GraphicsAttachmentLoadOperation DepthLoadOperation,
    GraphicsAttachmentStoreOperation DepthStoreOperation,
    GraphicsAttachmentLoadOperation StencilLoadOperation,
    GraphicsAttachmentStoreOperation StencilStoreOperation,
    GraphicsClearDepthStencil ClearValue);

/// <summary>Extent and attachment set for a dynamic rendering scope; no render-pass object is required.</summary>
public sealed class GraphicsRenderingDescription
{
    private readonly GraphicsColorAttachment[] _colors;
    public ReadOnlySpan<GraphicsColorAttachment> ColorAttachments => _colors;
    public GraphicsDepthStencilAttachment? DepthStencilAttachment { get; }
    public uint Width { get; }
    public uint Height { get; }
    public uint LayerCount { get; }
    public uint ViewMask { get; }

    public GraphicsRenderingDescription(
        uint width,
        uint height,
        ReadOnlySpan<GraphicsColorAttachment> colorAttachments,
        GraphicsDepthStencilAttachment? depthStencilAttachment = null,
        uint layerCount = 1,
        uint viewMask = 0)
    {
        if (width == 0 || height == 0 || layerCount == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Rendering extent and layer count must be non-zero.");
        if (colorAttachments.IsEmpty && depthStencilAttachment is null)
            throw new ArgumentException("A rendering scope requires at least one attachment.");
        Width = width;
        Height = height;
        LayerCount = layerCount;
        ViewMask = viewMask;
        _colors = colorAttachments.ToArray();
        DepthStencilAttachment = depthStencilAttachment;
        Validate();
    }

    public void Validate()
    {
        for (int i = 0; i < _colors.Length; i++)
        {
            GraphicsColorAttachment attachment = _colors[i];
            ArgumentNullException.ThrowIfNull(attachment.View);
            if (!Enum.IsDefined(attachment.LoadOperation) || !Enum.IsDefined(attachment.StoreOperation))
                throw new ArgumentOutOfRangeException(nameof(ColorAttachments));
            if (attachment.ResolveView is not null &&
                ReferenceEquals(attachment.View, attachment.ResolveView))
                throw new ArgumentException("A resolve view must be distinct from its multisample source view.");
        }

        if (DepthStencilAttachment is { } depth)
        {
            ArgumentNullException.ThrowIfNull(depth.View);
            if (!Enum.IsDefined(depth.DepthLoadOperation) || !Enum.IsDefined(depth.DepthStoreOperation) ||
                !Enum.IsDefined(depth.StencilLoadOperation) || !Enum.IsDefined(depth.StencilStoreOperation))
                throw new ArgumentOutOfRangeException(nameof(DepthStencilAttachment));
            if (!float.IsFinite(depth.ClearValue.Depth) || depth.ClearValue.Depth < 0 || depth.ClearValue.Depth > 1)
                throw new ArgumentOutOfRangeException(nameof(DepthStencilAttachment), "Clear depth must be within [0, 1].");
        }
    }
}

public readonly record struct GraphicsViewport(
    float X, float Y, float Width, float Height, float MinimumDepth = 0, float MaximumDepth = 1)
{
    public void Validate()
    {
        if (!float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Width) || !float.IsFinite(Height) ||
            !float.IsFinite(MinimumDepth) || !float.IsFinite(MaximumDepth) ||
            Width <= 0 || Height <= 0 || MinimumDepth < 0 || MaximumDepth > 1 || MinimumDepth > MaximumDepth)
            throw new ArgumentOutOfRangeException(nameof(Width));
    }
}

public readonly record struct GraphicsScissorRect(int X, int Y, uint Width, uint Height);

/// <summary>Optional dynamic-rendering extension implemented by command lists supporting graphics rendering.</summary>
public interface IGraphicsRenderingCommandList
{
    void BeginRendering(GraphicsRenderingDescription description);
    void EndRendering();
    void SetViewports(uint firstViewport, ReadOnlySpan<GraphicsViewport> viewports);
    void SetScissors(uint firstScissor, ReadOnlySpan<GraphicsScissorRect> scissors);
    void SetBlendConstants(float red, float green, float blue, float alpha);
    void SetStencilReference(uint reference);
    void BeginQuery(IGraphicsQueryPool queryPool, uint queryIndex);
    void EndQuery(IGraphicsQueryPool queryPool, uint queryIndex);
    void WriteTimestamp(IGraphicsQueryPool queryPool, uint queryIndex);
}
