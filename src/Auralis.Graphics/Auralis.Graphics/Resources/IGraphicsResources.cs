using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Resources;

/// <summary>Base interface for GPU resources owned by a graphics device.</summary>
public interface IGraphicsResource : IGraphicsDeviceObject
{
    string? DebugName { get; }
}

public interface IGraphicsBuffer : IGraphicsResource
{
    GraphicsBufferDescription Description { get; }

    /// <summary>
    /// Maps one CPU-visible buffer window. The returned memory is valid only until Unmap is called.
    /// Mapping GPU-only memory is an error. Backends perform any required cache maintenance.
    /// Memory&lt;byte&gt; has an Int32 length, so one window cannot exceed Int32.MaxValue bytes; larger
    /// buffers must be mapped through multiple explicit windows.
    /// </summary>
    Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue);

    void Unmap();
}

public interface IGraphicsTexture : IGraphicsResource
{
    GraphicsTextureDescription Description { get; }

    IGraphicsTextureView CreateView(in GraphicsTextureViewDescription description);
}

public interface IGraphicsTextureView : IGraphicsResource
{
    IGraphicsTexture Texture { get; }

    GraphicsTextureViewDescription Description { get; }
}

public interface IGraphicsSampler : IGraphicsResource
{
    GraphicsSamplerDescription Description { get; }
}

public interface IGraphicsShader : IGraphicsResource
{
    GraphicsShaderDescription Description { get; }
}
