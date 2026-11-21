using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// GLES immutable-storage texture. The initial backend supports the GLES 3.1 2D/array/3D/cubemap
/// storage targets; unsupported dimensionalities and multisampling are rejected explicitly.
/// </summary>
internal sealed class OpenGLESGraphicsTexture : OpenGLESNativeDeviceObject, IGraphicsTexture
{
    private uint _handle;

    private OpenGLESGraphicsTexture(
        OpenGLESGraphicsDevice device,
        in GraphicsTextureDescription description,
        TextureTarget target,
        uint handle) : base(device)
    {
        Description = description;
        Target = target;
        _handle = handle;
    }

    public GraphicsTextureDescription Description { get; }

    public string? DebugName => null;

    internal TextureTarget Target { get; }

    internal uint Handle
    {
        get
        {
            uint value = Volatile.Read(ref _handle);
            return value != 0 ? value : throw new ObjectDisposedException(nameof(OpenGLESGraphicsTexture));
        }
    }

    internal static OpenGLESGraphicsTexture CreateUnderContext(
        OpenGLESGraphicsDevice device,
        GL gl,
        in GraphicsTextureDescription description)
    {
        GraphicsTextureDescription copy = description;
        if (copy.SampleCount != 1)
            throw new GraphicsValidationException("OpenGL ES backend does not currently implement multisample texture storage.");
        if (copy.Dimension == GraphicsTextureDimension.Texture1D)
            throw new GraphicsValidationException("OpenGL ES has no 1D texture target.");
        if (copy.Dimension == GraphicsTextureDimension.Cube && copy.ArrayLayers != 6)
            throw new GraphicsValidationException("Cube-map arrays are not implemented by this OpenGL ES backend.");
        if ((copy.CreationOptions & GraphicsResourceCreationOptions.AllowAliasing) != 0)
            throw new GraphicsValidationException("OpenGL ES does not expose resource aliasing through this backend.");

        SizedInternalFormat format = GetInternalFormat(copy.Format);
        TextureTarget target = GetTextureTarget(copy);
        uint handle = gl.GenTexture();
        if (handle == 0)
            throw new GraphicsException("OpenGL ES failed to allocate a texture name.");

        try
        {
            gl.BindTexture(target, handle);
            if (copy.Dimension == GraphicsTextureDimension.Texture2D ||
                copy.Dimension == GraphicsTextureDimension.Cube)
            {
                gl.TexStorage2D(target, copy.MipLevels, format, copy.Width, copy.Height);
            }
            else
            {
                uint depth = copy.Dimension == GraphicsTextureDimension.Texture3D
                    ? copy.Depth
                    : copy.ArrayLayers;
                gl.TexStorage3D(target, copy.MipLevels, format, copy.Width, copy.Height, depth);
            }

            gl.TexParameteri(target, TextureParameterName.TextureBaseLevel, 0);
            gl.TexParameteri(target, TextureParameterName.TextureMaxLevel, checked((int)copy.MipLevels - 1));
            gl.BindTexture(target, 0);

            OpenGLESGraphicsTexture texture = new(device, copy, target, handle);
            device.RegisterUnderContext(texture);
            return texture;
        }
        catch
        {
            gl.BindTexture(target, 0);
            gl.DeleteTexture(handle);
            throw;
        }
    }

    public IGraphicsTextureView CreateView(in GraphicsTextureViewDescription description)
    {
        ThrowIfDisposed();
        GraphicsTextureViewDescription copy = description;
        copy.Validate(Description);

        if (copy.BaseMipLevel != 0 || copy.MipLevelCount != Description.MipLevels ||
            copy.BaseArrayLayer != 0 || copy.ArrayLayerCount != Description.ArrayLayers)
        {
            throw new GraphicsValidationException(
                "OpenGL ES texture views currently support the identity/full-resource view only; texture-view extensions are not assumed.");
        }

        GraphicsTextureViewType expected = Description.Dimension switch
        {
            GraphicsTextureDimension.Texture2D when Description.ArrayLayers > 1 => GraphicsTextureViewType.Texture2DArray,
            GraphicsTextureDimension.Texture2D => GraphicsTextureViewType.Texture2D,
            GraphicsTextureDimension.Texture3D => GraphicsTextureViewType.Texture3D,
            GraphicsTextureDimension.Cube => GraphicsTextureViewType.Cube,
            _ => throw new GraphicsValidationException("The source texture dimensionality has no GLES view mapping.")
        };
        if (copy.ViewType != expected ||
            (copy.Format != GraphicsFormat.Unknown && copy.Format != Description.Format))
        {
            throw new GraphicsValidationException(
                "OpenGL ES identity views cannot change texture dimensionality or reinterpret the resource format.");
        }

        return new OpenGLESGraphicsTextureView(this, copy);
    }

    protected override void DisposeCore() => ReleaseOwnerReference();

    public override void ReleaseNative(GL gl)
    {
        uint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
            gl.DeleteTexture(handle);
    }

    internal static SizedInternalFormat GetInternalFormat(GraphicsFormat format) => format switch
    {
        GraphicsFormat.R8UNorm => SizedInternalFormat.R8,
        GraphicsFormat.RG8UNorm => SizedInternalFormat.RG8,
        GraphicsFormat.RGBA8UNorm => SizedInternalFormat.Rgba8,
        GraphicsFormat.RGBA8UNormSrgb => SizedInternalFormat.Srgb8Alpha8,
        GraphicsFormat.D16UNorm => SizedInternalFormat.DepthComponent16,
        GraphicsFormat.D24UNormS8UInt => SizedInternalFormat.Depth24Stencil8,
        _ => throw new GraphicsValidationException($"OpenGL ES has no implemented storage mapping for format {format}.")
    };

    private static TextureTarget GetTextureTarget(in GraphicsTextureDescription description) =>
        description.Dimension switch
        {
            GraphicsTextureDimension.Texture2D => description.ArrayLayers > 1
                ? TextureTarget.Texture2DArray
                : TextureTarget.Texture2D,
            GraphicsTextureDimension.Texture3D => TextureTarget.Texture3D,
            GraphicsTextureDimension.Cube => TextureTarget.TextureCubeMap,
            _ => throw new GraphicsValidationException(
                $"OpenGL ES does not support the requested texture dimensionality: {description.Dimension}.")
        };
}

internal sealed class OpenGLESGraphicsTextureView : GraphicsDeviceObject, IGraphicsTextureView
{
    private int _parentReferenceHeld;

    internal OpenGLESGraphicsTextureView(OpenGLESGraphicsTexture texture, in GraphicsTextureViewDescription description)
        : base(texture.Device)
    {
        if (!texture.TryRetain())
            throw new ObjectDisposedException(nameof(OpenGLESGraphicsTexture));

        Texture = texture;
        Description = description;
        _parentReferenceHeld = 1;
    }

    public IGraphicsTexture Texture { get; }

    public GraphicsTextureViewDescription Description { get; }

    protected override void DisposeCore()
    {
        if (Interlocked.Exchange(ref _parentReferenceHeld, 0) != 0)
            ((IOpenGLESNativeResource)Texture).ReleaseReference();
    }
}
