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
}
