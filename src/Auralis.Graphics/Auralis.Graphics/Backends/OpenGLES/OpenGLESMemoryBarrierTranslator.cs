using Auralis.Graphics.Commands;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Translates backend-neutral destination access into GLES 3.1 core memory-barrier bits.
/// The constants below are the GLES 3.1 bit assignments.
/// </summary>
internal static class OpenGLESMemoryBarrierTranslator
{
    internal const uint VertexAttribArrayBarrierBit = 0x0000_0001;
    internal const uint ElementArrayBarrierBit = 0x0000_0002;
    internal const uint UniformBarrierBit = 0x0000_0004;
    internal const uint TextureFetchBarrierBit = 0x0000_0008;
    internal const uint ShaderImageAccessBarrierBit = 0x0000_0020;
    internal const uint CommandBarrierBit = 0x0000_0040;
    internal const uint TextureUpdateBarrierBit = 0x0000_0100;
    internal const uint BufferUpdateBarrierBit = 0x0000_0200;
    internal const uint FramebufferBarrierBit = 0x0000_0400;
    internal const uint ShaderStorageBarrierBit = 0x0000_2000;
    internal const uint AllBarrierBits = uint.MaxValue;

    /// <summary>Returns the GLES visibility bits required by a resource's destination access.</summary>
    internal static uint ToMask(in GraphicsResourceBarrier barrier)
    {
        GraphicsResourceAccess access = barrier.DestinationAccess;
        if (access == GraphicsResourceAccess.None)
            return 0;
        if ((access & (GraphicsResourceAccess.MemoryRead | GraphicsResourceAccess.MemoryWrite)) != 0)
            return AllBarrierBits;

        IGraphicsResource resource = barrier.Resource;
        if (resource is IGraphicsBuffer buffer)
        {
            GraphicsBufferUsage usage = buffer.Description.Usage;
            uint mask = 0;
            if ((access & GraphicsResourceAccess.IndirectRead) != 0)
                mask |= CommandBarrierBit;
            if ((access & GraphicsResourceAccess.IndexRead) != 0)
                mask |= ElementArrayBarrierBit;
            if ((access & GraphicsResourceAccess.VertexRead) != 0)
                mask |= VertexAttribArrayBarrierBit;
            if ((access & GraphicsResourceAccess.UniformRead) != 0)
                mask |= UniformBarrierBit;
            if ((access & (GraphicsResourceAccess.ShaderRead | GraphicsResourceAccess.ShaderWrite)) != 0 &&
                (usage & GraphicsBufferUsage.Storage) != 0)
                mask |= ShaderStorageBarrierBit;
            if ((access & (GraphicsResourceAccess.TransferRead | GraphicsResourceAccess.TransferWrite)) != 0)
                mask |= BufferUpdateBarrierBit;
            return mask;
        }

        GraphicsTextureUsage textureUsage = resource switch
        {
            IGraphicsTexture texture => texture.Description.Usage,
            IGraphicsTextureView view => view.Texture.Description.Usage,
            _ => GraphicsTextureUsage.None
        };

        uint textureMask = 0;
        if ((access & GraphicsResourceAccess.ShaderRead) != 0)
        {
            if ((textureUsage & GraphicsTextureUsage.Sampled) != 0)
                textureMask |= TextureFetchBarrierBit;
            if ((textureUsage & GraphicsTextureUsage.Storage) != 0)
                textureMask |= ShaderImageAccessBarrierBit;
        }

        if ((access & GraphicsResourceAccess.ShaderWrite) != 0 &&
            (textureUsage & GraphicsTextureUsage.Storage) != 0)
            textureMask |= ShaderImageAccessBarrierBit;

        if ((access & (GraphicsResourceAccess.ColorAttachmentRead |
                       GraphicsResourceAccess.ColorAttachmentWrite)) != 0 &&
            (textureUsage & GraphicsTextureUsage.ColorAttachment) != 0)
            textureMask |= FramebufferBarrierBit;

        if ((access & (GraphicsResourceAccess.DepthStencilRead |
                       GraphicsResourceAccess.DepthStencilWrite)) != 0 &&
            (textureUsage & GraphicsTextureUsage.DepthStencilAttachment) != 0)
            textureMask |= FramebufferBarrierBit;

        if ((access & (GraphicsResourceAccess.TransferRead | GraphicsResourceAccess.TransferWrite)) != 0)
            textureMask |= TextureUpdateBarrierBit;

        return textureMask;
    }

    /// <summary>Combines GLES visibility requirements for all supplied barriers.</summary>
    internal static uint ToMask(ReadOnlySpan<GraphicsResourceBarrier> barriers)
    {
        uint mask = 0;
        for (int i = 0; i < barriers.Length; i++)
            mask |= ToMask(barriers[i]);
        return mask;
    }
}
