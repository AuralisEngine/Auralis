using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// GLES immutable-storage texture. Supports GLES 3.1 2D/array/3D/cubemap storage and
/// single-mip 2D multisample storage where the device reports the requested format/sample count.
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
        TextureTarget target = OpenGLESTextureCompatibility.ValidateAndGetTarget(
            copy, device.Version, device.SupportsCubeMapArray);
        SizedInternalFormat format = GetInternalFormat(copy.Format);
        uint handle = gl.GenTexture();
        if (handle == 0)
            throw new GraphicsException("OpenGL ES failed to allocate a texture name.");

        try
        {
            gl.BindTexture(target, handle);
            if (target == TextureTarget.Texture2DMultisample)
            {
                gl.TexStorage2DMultisample(
                    target,
                    copy.SampleCount,
                    format,
                    copy.Width,
                    copy.Height,
                    fixedsamplelocations: true);
            }
            else
            {
                if ((copy.Dimension == GraphicsTextureDimension.Texture2D && copy.ArrayLayers == 1) ||
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

                gl.TexParameter(target, TextureParameterName.TextureBaseLevel, 0);
                gl.TexParameter(target, TextureParameterName.TextureMaxLevel, checked((int)copy.MipLevels - 1));
            }
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

    /// <summary>Creates a native texture view or a restricted framebuffer-only alias for a supported subresource.</summary>
    /// <param name="description">View target, format, and parent mip/layer range.</param>
    /// <returns>A view that retains its parent native texture until the view itself is released.</returns>
    /// <exception cref="ObjectDisposedException">The parent texture or device has been disposed.</exception>
    /// <exception cref="GraphicsValidationException">The requested view is unsupported by native texture-view entry points and the framebuffer-only alias rules.</exception>
    public IGraphicsTextureView CreateView(in GraphicsTextureViewDescription description)
    {
        ThrowIfDisposed();
        GraphicsTextureViewDescription copy = description;
        copy.Validate(Description);

        OpenGLESGraphicsDevice device = (OpenGLESGraphicsDevice)Device;
        using OpenGLESContextScope context = device.AcquireContext();
        return OpenGLESGraphicsTextureView.CreateUnderContext(
            device, context.Gl, device.TextureViewExt, this, copy);
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
        GraphicsFormat.R8UInt => SizedInternalFormat.R8ui,
        GraphicsFormat.R8SInt => SizedInternalFormat.R8i,
        GraphicsFormat.RG8UInt => SizedInternalFormat.RG8ui,
        GraphicsFormat.RG8SInt => SizedInternalFormat.RG8i,
        GraphicsFormat.RGBA8UInt => SizedInternalFormat.Rgba8ui,
        GraphicsFormat.RGBA8SInt => SizedInternalFormat.Rgba8i,
        GraphicsFormat.R8SNorm => SizedInternalFormat.R8SNorm,
        GraphicsFormat.RG8SNorm => SizedInternalFormat.RG8SNorm,
        GraphicsFormat.RGBA8SNorm => SizedInternalFormat.Rgba8SNorm,
        GraphicsFormat.R16Float => SizedInternalFormat.R16f,
        GraphicsFormat.RG16Float => SizedInternalFormat.RG16f,
        GraphicsFormat.RGBA16Float => SizedInternalFormat.Rgba16f,
        GraphicsFormat.R32Float => SizedInternalFormat.R32f,
        GraphicsFormat.RG32Float => SizedInternalFormat.RG32f,
        GraphicsFormat.RGB32Float => SizedInternalFormat.Rgb32f,
        GraphicsFormat.RGBA32Float => SizedInternalFormat.Rgba32f,
        GraphicsFormat.R16UInt => SizedInternalFormat.R16ui,
        GraphicsFormat.R16SInt => SizedInternalFormat.R16i,
        GraphicsFormat.RG16UInt => SizedInternalFormat.RG16ui,
        GraphicsFormat.RG16SInt => SizedInternalFormat.RG16i,
        GraphicsFormat.RGBA16UInt => SizedInternalFormat.Rgba16ui,
        GraphicsFormat.RGBA16SInt => SizedInternalFormat.Rgba16i,
        GraphicsFormat.R32UInt => SizedInternalFormat.R32ui,
        GraphicsFormat.R32SInt => SizedInternalFormat.R32i,
        GraphicsFormat.RG32UInt => SizedInternalFormat.RG32ui,
        GraphicsFormat.RG32SInt => SizedInternalFormat.RG32i,
        GraphicsFormat.RGBA32UInt => SizedInternalFormat.Rgba32ui,
        GraphicsFormat.RGBA32SInt => SizedInternalFormat.Rgba32i,
        GraphicsFormat.D16UNorm => SizedInternalFormat.DepthComponent16,
        GraphicsFormat.D24UNormS8UInt => SizedInternalFormat.Depth24Stencil8,
        GraphicsFormat.D32Float => SizedInternalFormat.DepthComponent32f,
        GraphicsFormat.D32FloatS8UInt => SizedInternalFormat.Depth32fStencil8,
        // Compressed internal-format tokens are extension-defined and are advertised only
        // when the matching extension is present in the device capability snapshot.
        GraphicsFormat.BC1UNorm => (SizedInternalFormat)0x83F1, // COMPRESSED_RGBA_S3TC_DXT1_EXT
        GraphicsFormat.BC1UNormSrgb => (SizedInternalFormat)0x8C4D, // COMPRESSED_SRGB_ALPHA_S3TC_DXT1_EXT
        GraphicsFormat.BC3UNorm => (SizedInternalFormat)0x83F3, // COMPRESSED_RGBA_S3TC_DXT5_EXT
        GraphicsFormat.BC3UNormSrgb => (SizedInternalFormat)0x8C4F, // COMPRESSED_SRGB_ALPHA_S3TC_DXT5_EXT
        GraphicsFormat.BC5UNorm => (SizedInternalFormat)0x8DBD, // COMPRESSED_RG_RGTC2
        GraphicsFormat.BC7UNorm => (SizedInternalFormat)0x8E8C, // COMPRESSED_RGBA_BPTC_UNORM
        GraphicsFormat.BC7UNormSrgb => (SizedInternalFormat)0x8E8D, // COMPRESSED_SRGB_ALPHA_BPTC_UNORM
        GraphicsFormat.ASTC4x4UNorm => (SizedInternalFormat)0x93B0, // COMPRESSED_RGBA_ASTC_4x4_KHR
        GraphicsFormat.ASTC4x4UNormSrgb => (SizedInternalFormat)0x93D0, // COMPRESSED_SRGB8_ALPHA8_ASTC_4x4_KHR
        _ => throw new GraphicsValidationException($"OpenGL ES has no implemented storage mapping for format {format}.")
    };

    internal static TextureTarget GetTextureTarget(in GraphicsTextureDescription description) =>
        description.Dimension switch
        {
            GraphicsTextureDimension.Texture2D => description.ArrayLayers > 1
                ? TextureTarget.Texture2DArray
                : TextureTarget.Texture2D,
            GraphicsTextureDimension.Texture3D => TextureTarget.Texture3D,
            GraphicsTextureDimension.Cube => description.ArrayLayers > 6
            ? TextureTarget.TextureCubeMapArray
            : TextureTarget.TextureCubeMap,
            _ => throw new GraphicsValidationException(
                $"OpenGL ES does not support the requested texture dimensionality: {description.Dimension}.")
        };
}

/// <summary>Represents an OpenGL ES texture view while retaining the parent texture's native storage.</summary>
/// <remarks>
/// Identity views reuse the parent's texture name and never delete it. When native EXT/OES texture
/// views are available, a subresource view owns a separate texture name. Without those extensions,
/// only a narrowly validated same-format 2D or 2D-array subresource may be represented as a
/// framebuffer-only alias; that alias can be attached to an FBO but is not sampleable or image-bindable.
/// Every view retains the parent object until release.
/// </remarks>
internal sealed class OpenGLESGraphicsTextureView : OpenGLESNativeDeviceObject, IGraphicsTextureView
{
    private readonly OpenGLESGraphicsTexture _texture;
    private readonly bool _ownsNativeHandle;
    private readonly bool _isFramebufferSubresourceAlias;
    private uint _handle;

    private OpenGLESGraphicsTextureView(
        OpenGLESGraphicsDevice device,
        OpenGLESGraphicsTexture texture,
        in GraphicsTextureViewDescription description,
        TextureTarget target,
        uint handle,
        bool ownsNativeHandle,
        bool isFramebufferSubresourceAlias) : base(device)
    {
        _texture = texture;
        Description = description;
        Target = target;
        _handle = handle;
        _ownsNativeHandle = ownsNativeHandle;
        _isFramebufferSubresourceAlias = isFramebufferSubresourceAlias;
    }

    public IGraphicsTexture Texture => _texture;

    public GraphicsTextureViewDescription Description { get; }

    public string? DebugName => null;

    internal TextureTarget Target { get; }

    /// <summary>
    /// True when this view aliases one 2D mip or one 2D-array layer of its parent and is
    /// implemented only through framebuffer attachment selection because native GLES
    /// texture-view support is unavailable. Such a view cannot be sampled or bound as an image.
    /// </summary>
    internal bool IsFramebufferSubresourceAlias => _isFramebufferSubresourceAlias;

    /// <summary>Attaches this view to a framebuffer, selecting the parent mip/layer when it is a fallback alias.</summary>
    /// <param name="gl">Current GLES dispatch object.</param>
    /// <param name="framebufferTarget">Read, draw, or combined framebuffer target.</param>
    /// <param name="attachment">Color, depth, or stencil attachment point.</param>
    /// <exception cref="GraphicsValidationException">A framebuffer-only alias has an unsupported parent texture target.</exception>
    internal void AttachToFramebuffer(GL gl, FramebufferTarget framebufferTarget, GLEnum attachment)
    {
        ArgumentNullException.ThrowIfNull(gl);
        uint handle = Handle;
        if (!_isFramebufferSubresourceAlias)
        {
            gl.FramebufferTexture2D(framebufferTarget, attachment, Target, handle, 0);
            return;
        }

        // GLES 3.x can select a mip of a 2D texture or a single layer of a 2D array
        // directly when attaching the parent object to an FBO. This preserves true
        // storage aliasing without pretending to create a sampleable GL texture view.
        if (_texture.Target == TextureTarget.Texture2D)
        {
            gl.FramebufferTexture2D(
                framebufferTarget,
                attachment,
                TextureTarget.Texture2D,
                handle,
                checked((int)Description.BaseMipLevel));
        }
        else if (_texture.Target == TextureTarget.Texture2DArray)
        {
            gl.FramebufferTextureLayer(
                framebufferTarget,
                attachment,
                handle,
                checked((int)Description.BaseMipLevel),
                checked((int)Description.BaseArrayLayer));
        }
        else
        {
            throw new GraphicsValidationException(
                $"Framebuffer-only GLES subresource aliases do not support parent target {_texture.Target}.");
        }
    }


    internal uint Handle
    {
        get
        {
            uint handle = Volatile.Read(ref _handle);
            return handle != 0 ? handle : throw new ObjectDisposedException(nameof(OpenGLESGraphicsTextureView));
        }
    }

    internal static OpenGLESGraphicsTextureView CreateUnderContext(
        OpenGLESGraphicsDevice device,
        GL gl,
        OpenGLESGraphicsDevice.TextureViewExtDelegate? textureViewApi,
        OpenGLESGraphicsTexture texture,
        in GraphicsTextureViewDescription description)
    {
        GraphicsTextureViewDescription copy = description;
        GraphicsFormat viewFormat = copy.Format == GraphicsFormat.Unknown
            ? texture.Description.Format
            : copy.Format;
        SizedInternalFormat nativeFormat = OpenGLESGraphicsTexture.GetInternalFormat(viewFormat);
        TextureTarget target = GetViewTarget(copy.ViewType);

        bool isIdentity = OpenGLESTextureViewValidation.IsIdentity(
            texture.Description,
            texture.Target,
            copy,
            target,
            viewFormat);
        bool isFramebufferSubresourceAlias = !isIdentity &&
            textureViewApi is null &&
            OpenGLESTextureViewValidation.CanUseFramebufferOnlyAlias(
                texture.Description,
                texture.Target,
                copy,
                target,
                viewFormat);
        if (!isIdentity && textureViewApi is null && !isFramebufferSubresourceAlias)
        {
            bool hasExtTextureView = device.Profile.HasExtension("GL_EXT_texture_view");
            bool hasOesTextureView = device.Profile.HasExtension("GL_OES_texture_view");
            if (!hasExtTextureView && !hasOesTextureView)
            {
                throw new GraphicsValidationException(
                    "The driver advertises neither GL_EXT_texture_view nor GL_OES_texture_view; subresource texture views are unsupported.");
            }

            throw new GraphicsValidationException(
                "The driver advertises a texture-view extension, but its corresponding native entry point is unavailable; " +
                "this subresource/format view is outside the framebuffer-only fallback.");
        }

        // Retain the parent's native object for the full view lifetime. Identity views and
        // framebuffer-only subresource aliases share the parent name; native extension views
        // own a separate texture name but retain the same underlying storage dependency.
        if (!texture.TryRetain())
            throw new ObjectDisposedException(nameof(OpenGLESGraphicsTexture));

        uint handle = texture.Handle;
        bool ownsHandle = false;
        try
        {
            if (!isIdentity && !isFramebufferSubresourceAlias)
            {
                handle = gl.GenTexture();
                if (handle == 0)
                    throw new GraphicsException("OpenGL ES failed to allocate a texture-view name.");

                ownsHandle = true;
                textureViewApi!(
                    handle,
                    target,
                    texture.Handle,
                    nativeFormat,
                    copy.BaseMipLevel,
                    copy.MipLevelCount,
                    copy.BaseArrayLayer,
                    copy.ArrayLayerCount);
            }

            OpenGLESGraphicsTextureView view = new(
                device,
                texture,
                copy,
                target,
                handle,
                ownsHandle,
                isFramebufferSubresourceAlias);
            device.RegisterUnderContext(view);
            return view;
        }
        catch
        {
            if (ownsHandle && handle != 0)
                gl.DeleteTexture(handle);
            ((IOpenGLESNativeResource)texture).ReleaseReference();
            throw;
        }
    }

    private static TextureTarget GetViewTarget(GraphicsTextureViewType viewType) =>
        viewType switch
        {
            GraphicsTextureViewType.Texture2D => TextureTarget.Texture2D,
            GraphicsTextureViewType.Texture2DArray => TextureTarget.Texture2DArray,
            GraphicsTextureViewType.Texture2DMultisample => TextureTarget.Texture2DMultisample,
            GraphicsTextureViewType.Texture2DMultisampleArray => TextureTarget.Texture2DMultisampleArray,
            GraphicsTextureViewType.Texture3D => TextureTarget.Texture3D,
            GraphicsTextureViewType.Cube => TextureTarget.TextureCubeMap,
            GraphicsTextureViewType.CubeArray => TextureTarget.TextureCubeMapArray,
            _ => throw new GraphicsValidationException(
                $"OpenGL ES does not support the requested texture-view type {viewType}.")
        };

    protected override void DisposeCore() => ReleaseOwnerReference();

    public override void ReleaseNative(GL gl)
    {
        uint handle = Interlocked.Exchange(ref _handle, 0);
        // Delete a separately-created subresource view before releasing its parent dependency.
        // Identity views alias the parent handle and must never delete it themselves.
        if (_ownsNativeHandle && handle != 0)
            gl.DeleteTexture(handle);

        ((IOpenGLESNativeResource)_texture).ReleaseReference();
    }
}


/// <summary>
/// Pure texture-view classification and support validation shared by native creation and
/// hardware-independent tests. Identity views alias the parent texture name and need no extension.
/// </summary>
internal static class OpenGLESTextureViewValidation
{
    /// <summary>Determines whether a view selects the complete parent storage with the same target and format.</summary>
    internal static bool IsIdentity(
        in GraphicsTextureDescription textureDescription,
        TextureTarget textureTarget,
        in GraphicsTextureViewDescription viewDescription,
        TextureTarget viewTarget,
        GraphicsFormat viewFormat) =>
        viewDescription.BaseMipLevel == 0 &&
        viewDescription.MipLevelCount == textureDescription.MipLevels &&
        viewDescription.BaseArrayLayer == 0 &&
        viewDescription.ArrayLayerCount == textureDescription.ArrayLayers &&
        viewTarget == textureTarget &&
        viewFormat == textureDescription.Format;

    /// <summary>
    /// Whether a subresource can be exposed as a 2D framebuffer attachment without
    /// glTextureView: one mip of a 2D texture, or one layer/mip of a 2D-array texture.
    /// It must preserve the parent's format and storage; this is not a sampleable view.
    /// </summary>
    /// <summary>Determines whether a subresource can be attached directly from its parent without creating a native texture-view object.</summary>
    /// <remarks>The result is a framebuffer-only compatibility path, not a claim of general GLES texture-view support.</remarks>
    internal static bool CanUseFramebufferOnlyAlias(
        in GraphicsTextureDescription textureDescription,
        TextureTarget textureTarget,
        in GraphicsTextureViewDescription viewDescription,
        TextureTarget viewTarget,
        GraphicsFormat viewFormat)
    {
        if (viewTarget != TextureTarget.Texture2D ||
            viewFormat != textureDescription.Format ||
            viewDescription.MipLevelCount != 1 ||
            viewDescription.ArrayLayerCount != 1)
        {
            return false;
        }

        return textureTarget switch
        {
            TextureTarget.Texture2D =>
                textureDescription.ArrayLayers == 1 &&
                viewDescription.BaseArrayLayer == 0,
            TextureTarget.Texture2DArray => true,
            _ => false
        };
    }

}
