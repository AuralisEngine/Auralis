using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;

namespace Auralis.Graphics.Devices;

/// <summary>
/// A device is independent of windows and presentation targets. It can be used for compute,
/// resource processing, and offscreen rendering without creating a native surface.
/// </summary>
public interface IGraphicsDevice : IDisposable
{
    GraphicsBackend Backend { get; }

    GraphicsAdapterInfo Adapter { get; }

    GraphicsDeviceInfo Info { get; }

    GraphicsDeviceCapabilities Capabilities { get; }

    bool IsDisposed { get; }

    /// <summary>Gets a command-capable graphics, compute, or copy queue.</summary>
    IGraphicsCommandQueue GetQueue(GraphicsQueueType queueType, uint index = 0);

    /// <summary>
    /// Gets a presentation-queue candidate. It may not implement IGraphicsCommandQueue and is not
    /// guaranteed to support every surface. Query the actual surface before creating a swapchain.
    /// </summary>
    IGraphicsQueue GetPresentationQueue(uint index = 0);

    IGraphicsBuffer CreateBuffer(in GraphicsBufferDescription description);

    IGraphicsTexture CreateTexture(in GraphicsTextureDescription description);

    IGraphicsSampler CreateSampler(in GraphicsSamplerDescription description);

    IGraphicsShader CreateShader(in GraphicsShaderDescription description);

    IGraphicsBindingLayout CreateBindingLayout(GraphicsBindingLayoutDescription description);

    IGraphicsPipelineLayout CreatePipelineLayout(ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts);

    IGraphicsBindingSet CreateBindingSet(IGraphicsBindingLayout bindingLayout);

    IGraphicsRenderPipeline CreateRenderPipeline(GraphicsRenderPipelineDescription description);

    IGraphicsComputePipeline CreateComputePipeline(GraphicsComputePipelineDescription description);

    IGraphicsFence CreateFence(ulong initialValue = 0);

    IGraphicsPresentationSurface CreatePresentationSurface(IGraphicsPresentationTarget target);
}
