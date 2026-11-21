using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>Pure GLES texture compatibility validation, independent of a live driver/context.</summary>
internal static class OpenGLESTextureCompatibility
{
    internal static TextureTarget ValidateAndGetTarget(in GraphicsTextureDescription description)
    {
        description.Validate();

        if (description.SampleCount != 1)
            throw new GraphicsValidationException("OpenGL ES backend does not currently implement multisample texture storage.");
        if (description.Dimension == GraphicsTextureDimension.Texture1D)
            throw new GraphicsValidationException("OpenGL ES has no 1D texture target.");
        if (description.Dimension == GraphicsTextureDimension.Cube && description.ArrayLayers != 6)
            throw new GraphicsValidationException("Cube-map arrays are not implemented by this OpenGL ES backend.");
        if ((description.CreationOptions & GraphicsResourceCreationOptions.AllowAliasing) != 0)
            throw new GraphicsValidationException("OpenGL ES does not expose resource aliasing through this backend.");

        _ = OpenGLESGraphicsTexture.GetInternalFormat(description.Format);
        return OpenGLESGraphicsTexture.GetTextureTarget(description);
    }

    internal static void ValidateCopy(
        in GraphicsTextureDescription source,
        in GraphicsTextureDescription destination)
    {
        source.Validate();
        destination.Validate();

        if ((source.Usage & (GraphicsTextureUsage.CopySource | GraphicsTextureUsage.ColorAttachment)) !=
                (GraphicsTextureUsage.CopySource | GraphicsTextureUsage.ColorAttachment) ||
            (destination.Usage & (GraphicsTextureUsage.CopyDestination | GraphicsTextureUsage.ColorAttachment)) !=
                (GraphicsTextureUsage.CopyDestination | GraphicsTextureUsage.ColorAttachment))
        {
            throw new GraphicsValidationException(
                "The GLES framebuffer-blit copy requires CopySource and ColorAttachment on the source, " +
                "and CopyDestination and ColorAttachment on the destination.");
        }

        if (source.Dimension != GraphicsTextureDimension.Texture2D ||
            destination.Dimension != GraphicsTextureDimension.Texture2D ||
            source.SampleCount != 1 ||
            destination.SampleCount != 1 ||
            source.ArrayLayers != 1 ||
            destination.ArrayLayers != 1 ||
            source.MipLevels != 1 ||
            destination.MipLevels != 1)
        {
            throw new GraphicsValidationException(
                "The GLES framebuffer-blit copy supports only single-sample, single-layer, single-mip 2D textures.");
        }

        if (source.Width != destination.Width ||
            source.Height != destination.Height ||
            source.Format != destination.Format)
        {
            throw new GraphicsValidationException(
                "The GLES framebuffer-blit source and destination must have identical extents and formats.");
        }

        if (source.Format is not (GraphicsFormat.RGBA8UNorm or GraphicsFormat.RGBA8UNormSrgb))
            throw new GraphicsValidationException(
                $"The GLES color framebuffer-blit copy path does not support format {source.Format}.");
    }
}
