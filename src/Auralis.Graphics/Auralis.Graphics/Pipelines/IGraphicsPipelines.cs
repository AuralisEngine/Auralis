using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Pipelines;

public interface IGraphicsBindingLayout : IGraphicsDeviceObject
{
    GraphicsBindingLayoutDescription Description { get; }
}

public interface IGraphicsPipelineLayout : IGraphicsDeviceObject
{
    ReadOnlyMemory<IGraphicsBindingLayout> BindingLayouts { get; }
}

public interface IGraphicsBindingSet : IGraphicsDeviceObject
{
    IGraphicsBindingLayout Layout { get; }

    void SetBuffer(uint binding, uint arrayElement, IGraphicsBuffer buffer, ulong offset = 0, ulong range = ulong.MaxValue);

    void SetTexture(uint binding, uint arrayElement, IGraphicsTextureView textureView);

    void SetSampler(uint binding, uint arrayElement, IGraphicsSampler sampler);
}

public interface IGraphicsPipeline : IGraphicsDeviceObject
{
    IGraphicsPipelineLayout Layout { get; }
}

public interface IGraphicsRenderPipeline : IGraphicsPipeline
{
    GraphicsRenderPipelineDescription Description { get; }
}

public interface IGraphicsComputePipeline : IGraphicsPipeline
{
    GraphicsComputePipelineDescription Description { get; }
}
