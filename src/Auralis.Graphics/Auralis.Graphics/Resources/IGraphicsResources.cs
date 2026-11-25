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
    /// A zero size is invalid. An explicit size greater than <see cref="int.MaxValue"/> is rejected.
    /// When size is <see cref="ulong.MaxValue"/>, the window extends from offset to the end of the
    /// buffer, capped at <see cref="int.MaxValue"/> bytes. Map validates the requested range against
    /// <see cref="GraphicsBufferDescription.SizeInBytes"/> and returns exactly the selected window.
    /// To access a larger buffer, map successive windows and call Unmap between windows.
    /// </summary>
    Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue);

    /// <summary>Ends the active mapping, if any. All Memory returned by the preceding Map becomes invalid.</summary>
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
