using Auralis.Graphics.Devices;
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
        if (width == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Rendering width must be non-zero.");
        if (height == 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Rendering height must be non-zero.");
        if (layerCount == 0)
            throw new ArgumentOutOfRangeException(nameof(layerCount), "Rendering layer count must be non-zero.");
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
        if (ViewMask != 0 && LayerCount != 1)
            throw new ArgumentException("Multiview rendering requires LayerCount to be one.", nameof(LayerCount));

        uint requiredLayers = ViewMask == 0 ? LayerCount : HighestSetBit(ViewMask) + 1;
        IGraphicsDevice? device = null;
        uint? sampleCount = null;

        for (int i = 0; i < _colors.Length; i++)
        {
            GraphicsColorAttachment attachment = _colors[i];
            ValidateAttachmentView(attachment.View, GraphicsTextureUsage.ColorAttachment,
                requiredLayers, ref device, ref sampleCount);
            if (!Enum.IsDefined(attachment.LoadOperation) || !Enum.IsDefined(attachment.StoreOperation))
                throw new ArgumentOutOfRangeException(nameof(ColorAttachments));

            GraphicsTextureDescription sourceTexture = attachment.View.Texture.Description;
            if (attachment.View.Description.GetEffectiveAspects(sourceTexture) != GraphicsTextureAspect.Color)
                throw new ArgumentException("Color attachments must expose only the color aspect.", nameof(ColorAttachments));
            if (attachment.LoadOperation == GraphicsAttachmentLoadOperation.Clear &&
                (!float.IsFinite(attachment.ClearValue.Red) || !float.IsFinite(attachment.ClearValue.Green) ||
                 !float.IsFinite(attachment.ClearValue.Blue) || !float.IsFinite(attachment.ClearValue.Alpha)))
                throw new ArgumentOutOfRangeException(nameof(ColorAttachments), "Clear color components must be finite.");

            if (attachment.ResolveView is not null)
            {
                if (ReferenceEquals(attachment.View, attachment.ResolveView) ||
                    ReferenceEquals(attachment.View.Texture, attachment.ResolveView.Texture))
                    throw new ArgumentException("A resolve target must use a different texture from its multisample source.");
                if (!ReferenceEquals(attachment.View.Device, attachment.ResolveView.Device))
                    throw new ArgumentException("Resolve source and destination must belong to the same device.");
                IGraphicsDevice? ignoredDevice = null;
                uint? ignoredSamples = null;
                ValidateAttachmentView(attachment.ResolveView, GraphicsTextureUsage.ColorAttachment,
                    requiredLayers, ref ignoredDevice, ref ignoredSamples, false, false);
                GraphicsTextureDescription resolveTexture = attachment.ResolveView.Texture.Description;
                if (sourceTexture.SampleCount <= 1 || resolveTexture.SampleCount != 1)
                    throw new ArgumentException("Resolve requires a multisampled source and a single-sample destination.", nameof(ColorAttachments));
                if (GetViewFormat(attachment.View) != GetViewFormat(attachment.ResolveView) ||
                    GetViewWidth(attachment.View) != GetViewWidth(attachment.ResolveView) ||
                    GetViewHeight(attachment.View) != GetViewHeight(attachment.ResolveView) ||
                    attachment.View.Description.ArrayLayerCount != attachment.ResolveView.Description.ArrayLayerCount)
                    throw new ArgumentException("Resolve source and destination formats, extents and layer counts must match.", nameof(ColorAttachments));
            }
        }

        if (ViewMask != 0)
        {
            IGraphicsDevice renderingDevice = device ??
                throw new ArgumentException("Multiview rendering requires at least one valid attachment.");
            if (!renderingDevice.Capabilities.Features.Supports(GraphicsFeature.Multiview))
                throw new GraphicsFeatureNotSupportedException(
                    GraphicsFeature.Multiview, GraphicsFeature.Multiview);
            uint maximumViewCount = renderingDevice.Capabilities.Limits.MaxMultiviewViewCount;
            if (maximumViewCount == 0 || requiredLayers > maximumViewCount)
                throw new GraphicsException(
                    $"View mask requires {requiredLayers} view indices, but the device reports a maximum multiview view count of {maximumViewCount}.");
        }

        if (DepthStencilAttachment is { } depth)
        {
            ValidateAttachmentView(depth.View, GraphicsTextureUsage.DepthStencilAttachment,
                requiredLayers, ref device, ref sampleCount);
            if (!Enum.IsDefined(depth.DepthLoadOperation) || !Enum.IsDefined(depth.DepthStoreOperation) ||
                !Enum.IsDefined(depth.StencilLoadOperation) || !Enum.IsDefined(depth.StencilStoreOperation))
                throw new ArgumentOutOfRangeException(nameof(DepthStencilAttachment));

            GraphicsTextureAspect aspects = depth.View.Description.GetEffectiveAspects(depth.View.Texture.Description);
            bool hasDepth = (aspects & GraphicsTextureAspect.Depth) != 0;
            bool hasStencil = (aspects & GraphicsTextureAspect.Stencil) != 0;
            if (aspects == GraphicsTextureAspect.None || (aspects & GraphicsTextureAspect.Color) != 0)
                throw new ArgumentException("Depth/stencil attachments must expose depth and/or stencil aspects.", nameof(DepthStencilAttachment));
            if ((!hasDepth && (depth.DepthLoadOperation != GraphicsAttachmentLoadOperation.DontCare ||
                               depth.DepthStoreOperation != GraphicsAttachmentStoreOperation.DontCare)) ||
                (!hasStencil && (depth.StencilLoadOperation != GraphicsAttachmentLoadOperation.DontCare ||
                                 depth.StencilStoreOperation != GraphicsAttachmentStoreOperation.DontCare)))
                throw new ArgumentException("Depth/stencil operations must target aspects present in the attachment format.", nameof(DepthStencilAttachment));
            if (depth.DepthLoadOperation == GraphicsAttachmentLoadOperation.Clear &&
                (!float.IsFinite(depth.ClearValue.Depth) || depth.ClearValue.Depth < 0 || depth.ClearValue.Depth > 1))
                throw new ArgumentOutOfRangeException(nameof(DepthStencilAttachment), "Clear depth must be within [0, 1].");
            if (depth.StencilLoadOperation == GraphicsAttachmentLoadOperation.Clear && depth.ClearValue.Stencil > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(DepthStencilAttachment), "Stencil clear values are limited to eight bits.");
        }
    }

    private void ValidateAttachmentView(
        IGraphicsTextureView view,
        GraphicsTextureUsage requiredUsage,
        uint requiredLayers,
        ref IGraphicsDevice? commonDevice,
        ref uint? commonSamples,
        bool enforceCommonDevice = true,
        bool enforceCommonSamples = true)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(view.Texture);
        if (view.IsDisposed || view.Device.IsDisposed || view.Texture.IsDisposed || view.Texture.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(view));
        if (!ReferenceEquals(view.Device, view.Texture.Device))
            throw new ArgumentException("Attachment views and their textures must belong to the same device.");
        if (enforceCommonDevice)
        {
            if (commonDevice is null) commonDevice = view.Device;
            else if (!ReferenceEquals(commonDevice, view.Device))
                throw new ArgumentException("All rendering attachments must belong to the same graphics device.");
        }

        GraphicsTextureDescription texture = view.Texture.Description;
        view.Device.Capabilities.ValidateTextureViewDescription(texture, view.Description);
        if (view.Description.MipLevelCount != 1)
            throw new ArgumentException("Rendering attachment views must select exactly one mip level.");
        if ((texture.Usage & requiredUsage) != requiredUsage)
            throw new ArgumentException($"Attachment texture must declare {requiredUsage} usage.");
        if (view.Description.ArrayLayerCount < requiredLayers)
            throw new ArgumentException("Attachment view does not expose enough array layers for the rendering layer count or view mask.");

        uint samples = texture.SampleCount;
        if (enforceCommonSamples)
        {
            if (commonSamples is null) commonSamples = samples;
            else if (commonSamples.Value != samples)
                throw new ArgumentException("All rendering attachments must use the same sample count.");
        }
        GraphicsTextureAspect selectedAspects = view.Description.GetEffectiveAspects(texture);
        if (requiredUsage == GraphicsTextureUsage.ColorAttachment)
        {
            if (selectedAspects != GraphicsTextureAspect.Color)
                throw new ArgumentException("Color attachments must expose only the color aspect.");
        }
        else if (selectedAspects == GraphicsTextureAspect.None ||
                 (selectedAspects & GraphicsTextureAspect.Color) != 0)
        {
            throw new ArgumentException("Depth/stencil attachments must expose depth and/or stencil aspects.");
        }

        if (GetViewWidth(view) < Width || GetViewHeight(view) < Height)
            throw new ArgumentException("The rendering extent exceeds an attachment view's selected mip extent.");
    }

    private static GraphicsFormat GetViewFormat(IGraphicsTextureView view) =>
        view.Description.Format == GraphicsFormat.Unknown ? view.Texture.Description.Format : view.Description.Format;

    private static uint GetViewWidth(IGraphicsTextureView view) =>
        Math.Max(1u, view.Texture.Description.Width >> (int)view.Description.BaseMipLevel);

    private static uint GetViewHeight(IGraphicsTextureView view) =>
        Math.Max(1u, view.Texture.Description.Height >> (int)view.Description.BaseMipLevel);

    private static uint HighestSetBit(uint mask)
    {
        uint index = 0;
        while ((mask >>= 1) != 0) index++;
        return index;
    }

}

/// <summary>
/// Portable viewport contract. Width and height must be positive; negative-height Y inversion is not
/// currently exposed as a common feature because backend support is capability-dependent.
/// </summary>
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
public interface IGraphicsRenderingCommandList : IGraphicsCommandList
{
    void BeginRendering(GraphicsRenderingDescription description);
    void EndRendering();
    void SetViewports(uint firstViewport, ReadOnlySpan<GraphicsViewport> viewports);
    void SetScissors(uint firstScissor, ReadOnlySpan<GraphicsScissorRect> scissors);
    void SetBlendConstants(float red, float green, float blue, float alpha);
    void SetStencilReference(uint reference);
}
