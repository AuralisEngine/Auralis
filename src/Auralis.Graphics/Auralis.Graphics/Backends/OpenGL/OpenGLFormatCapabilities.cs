using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Maps common formats to desktop OpenGL sized internal formats and queries their actual support.</summary>
internal static class OpenGLFormatCapabilities
{
    internal static GraphicsFormatCapabilities[] Query(
        GL gl,
        ArbInternalformatQuery? internalFormatQuery)
    {
        if (internalFormatQuery is null)
            return [];

        List<GraphicsFormatCapabilities> formats = new(35);
        foreach (GraphicsFormat format in Enum.GetValues<GraphicsFormat>())
        {
            if (format == GraphicsFormat.Unknown ||
                !TryGetInternalFormat(format, out SizedInternalFormat internalFormat, out string? requiredExtension))
                continue;

            if (requiredExtension is not null && !gl.IsExtensionPresent(requiredExtension))
                continue;

            int supported = internalFormatQuery.GetInternalformat(
                TextureTarget.Texture2D,
                internalFormat,
                InternalFormatPName.InternalformatSupported);

            if (supported == 0)
                continue;

            GraphicsFormatSupport support = GraphicsFormatSupport.Sampled |
                                            GraphicsFormatSupport.CopySource |
                                            GraphicsFormatSupport.CopyDestination;

            if (internalFormatQuery.GetInternalformat(
                    TextureTarget.Texture2D,
                    internalFormat,
                    InternalFormatPName.Filter) != 0)
            {
                support |= GraphicsFormatSupport.LinearFiltering;
            }

            bool isDepthStencil = IsDepthStencil(format);
            if (isDepthStencil)
            {
                if (internalFormatQuery.GetInternalformat(
                        TextureTarget.Texture2D,
                        internalFormat,
                        InternalFormatPName.DepthRenderable) != 0 ||
                    internalFormatQuery.GetInternalformat(
                        TextureTarget.Texture2D,
                        internalFormat,
                        InternalFormatPName.StencilRenderable) != 0)
                {
                    support |= GraphicsFormatSupport.DepthStencilAttachment;
                }
            }
            else
            {
                if (internalFormatQuery.GetInternalformat(
                        TextureTarget.Texture2D,
                        internalFormat,
                        InternalFormatPName.ColorRenderable) != 0)
                {
                    support |= GraphicsFormatSupport.ColorAttachment;
                }

                if (internalFormatQuery.GetInternalformat(
                        TextureTarget.Texture2D,
                        internalFormat,
                        InternalFormatPName.FramebufferBlend) != 0)
                {
                    support |= GraphicsFormatSupport.Blend;
                }
            }

            if (internalFormatQuery.GetInternalformat(
                    TextureTarget.Texture2D,
                    internalFormat,
                    InternalFormatPName.ShaderImageLoad) != 0 &&
                internalFormatQuery.GetInternalformat(
                    TextureTarget.Texture2D,
                    internalFormat,
                    InternalFormatPName.ShaderImageStore) != 0)
            {
                support |= GraphicsFormatSupport.Storage;
            }

            if (IsVertexFormat(format))
                support |= GraphicsFormatSupport.VertexBuffer;

            GraphicsSampleCountFlags sampleCounts = GraphicsSampleCountFlags.One;
            int count = internalFormatQuery.GetInternalformat(
                TextureTarget.Texture2DMultisample,
                internalFormat,
                InternalFormatPName.NumSampleCounts);

            if (count > 0 && count <= 64)
            {
                Span<int> queriedCounts = stackalloc int[count];
                internalFormatQuery.GetInternalformat(
                    TextureTarget.Texture2DMultisample,
                    internalFormat,
                    queriedCounts);
                sampleCounts = GraphicsSampleCountFlags.None;
                for (int i = 0; i < queriedCounts.Length; i++)
                {
                    sampleCounts |= queriedCounts[i] switch
                    {
                        1 => GraphicsSampleCountFlags.One,
                        2 => GraphicsSampleCountFlags.Two,
                        4 => GraphicsSampleCountFlags.Four,
                        8 => GraphicsSampleCountFlags.Eight,
                        16 => GraphicsSampleCountFlags.Sixteen,
                        32 => GraphicsSampleCountFlags.ThirtyTwo,
                        64 => GraphicsSampleCountFlags.SixtyFour,
                        _ => GraphicsSampleCountFlags.None
                    };
                }

                if (sampleCounts == GraphicsSampleCountFlags.None)
                    sampleCounts = GraphicsSampleCountFlags.One;
            }

            formats.Add(new GraphicsFormatCapabilities(format, support, sampleCounts));
        }

        return formats.ToArray();
    }

    internal static bool TryGetInternalFormat(
        GraphicsFormat format,
        out SizedInternalFormat internalFormat,
        out string? requiredExtension)
    {
        requiredExtension = null;
        internalFormat = format switch
        {
            GraphicsFormat.R8UNorm => SizedInternalFormat.R8,
            GraphicsFormat.R8SNorm => SizedInternalFormat.R8SNorm,
            GraphicsFormat.R8UInt => SizedInternalFormat.R8ui,
            GraphicsFormat.R8SInt => SizedInternalFormat.R8i,
            GraphicsFormat.RG8UNorm => SizedInternalFormat.RG8,
            GraphicsFormat.RG8SNorm => SizedInternalFormat.RG8SNorm,
            GraphicsFormat.RG8UInt => SizedInternalFormat.RG8ui,
            GraphicsFormat.RG8SInt => SizedInternalFormat.RG8i,
            GraphicsFormat.RGBA8UNorm => SizedInternalFormat.Rgba8,
            GraphicsFormat.RGBA8UNormSrgb => SizedInternalFormat.Srgb8Alpha8,
            GraphicsFormat.RGBA8SNorm => SizedInternalFormat.Rgba8SNorm,
            GraphicsFormat.RGBA8UInt => SizedInternalFormat.Rgba8ui,
            GraphicsFormat.RGBA8SInt => SizedInternalFormat.Rgba8i,
            GraphicsFormat.R16Float => SizedInternalFormat.R16f,
            GraphicsFormat.R16UInt => SizedInternalFormat.R16ui,
            GraphicsFormat.R16SInt => SizedInternalFormat.R16i,
            GraphicsFormat.RG16Float => SizedInternalFormat.RG16f,
            GraphicsFormat.RG16UInt => SizedInternalFormat.RG16ui,
            GraphicsFormat.RG16SInt => SizedInternalFormat.RG16i,
            GraphicsFormat.RGBA16Float => SizedInternalFormat.Rgba16f,
            GraphicsFormat.RGBA16UInt => SizedInternalFormat.Rgba16ui,
            GraphicsFormat.RGBA16SInt => SizedInternalFormat.Rgba16i,
            GraphicsFormat.R32Float => SizedInternalFormat.R32f,
            GraphicsFormat.R32UInt => SizedInternalFormat.R32ui,
            GraphicsFormat.R32SInt => SizedInternalFormat.R32i,
            GraphicsFormat.RG32Float => SizedInternalFormat.RG32f,
            GraphicsFormat.RG32UInt => SizedInternalFormat.RG32ui,
            GraphicsFormat.RG32SInt => SizedInternalFormat.RG32i,
            GraphicsFormat.RGB32Float => SizedInternalFormat.Rgb32f,
            GraphicsFormat.RGBA32Float => SizedInternalFormat.Rgba32f,
            GraphicsFormat.RGBA32UInt => SizedInternalFormat.Rgba32ui,
            GraphicsFormat.RGBA32SInt => SizedInternalFormat.Rgba32i,
            GraphicsFormat.D16UNorm => SizedInternalFormat.DepthComponent16,
            GraphicsFormat.D24UNormS8UInt => SizedInternalFormat.Depth24Stencil8,
            GraphicsFormat.D32Float => SizedInternalFormat.DepthComponent32f,
            GraphicsFormat.D32FloatS8UInt => SizedInternalFormat.Depth32fStencil8,
            GraphicsFormat.BC1UNorm => Compressed(
                SizedInternalFormat.CompressedRgbaS3TCDxt1Ext, "GL_EXT_texture_compression_s3tc"),
            GraphicsFormat.BC1UNormSrgb => Compressed(
                SizedInternalFormat.CompressedSrgbAlphaS3TCDxt1Ext, "GL_EXT_texture_compression_s3tc"),
            GraphicsFormat.BC3UNorm => Compressed(
                SizedInternalFormat.CompressedRgbaS3TCDxt5Ext, "GL_EXT_texture_compression_s3tc"),
            GraphicsFormat.BC3UNormSrgb => Compressed(
                SizedInternalFormat.CompressedSrgbAlphaS3TCDxt5Ext, "GL_EXT_texture_compression_s3tc"),
            GraphicsFormat.BC5UNorm => Compressed(
                SizedInternalFormat.CompressedRGRgtc2, "GL_ARB_texture_compression_rgtc"),
            GraphicsFormat.BC7UNorm => Compressed(
                SizedInternalFormat.CompressedRgbaBptcUnorm, "GL_ARB_texture_compression_bptc"),
            GraphicsFormat.BC7UNormSrgb => Compressed(
                SizedInternalFormat.CompressedSrgbAlphaBptcUnorm, "GL_ARB_texture_compression_bptc"),
            GraphicsFormat.ASTC4x4UNorm => Compressed(
                SizedInternalFormat.CompressedRgbaAstc4x4, "GL_KHR_texture_compression_astc_ldr"),
            GraphicsFormat.ASTC4x4UNormSrgb => Compressed(
                SizedInternalFormat.CompressedSrgb8Alpha8Astc4x4, "GL_KHR_texture_compression_astc_ldr"),
            _ => (SizedInternalFormat)0
        };

        if ((int)internalFormat == 0)
            return false;

        if (format is GraphicsFormat.BC1UNorm or GraphicsFormat.BC1UNormSrgb or
            GraphicsFormat.BC3UNorm or GraphicsFormat.BC3UNormSrgb)
            requiredExtension = "GL_EXT_texture_compression_s3tc";
        else if (format == GraphicsFormat.BC5UNorm)
            requiredExtension = "GL_ARB_texture_compression_rgtc";
        else if (format is GraphicsFormat.BC7UNorm or GraphicsFormat.BC7UNormSrgb)
            requiredExtension = "GL_ARB_texture_compression_bptc";
        else if (format is GraphicsFormat.ASTC4x4UNorm or GraphicsFormat.ASTC4x4UNormSrgb)
            requiredExtension = "GL_KHR_texture_compression_astc_ldr";

        return true;
    }

    private static SizedInternalFormat Compressed(
        SizedInternalFormat format,
        string requiredExtension)
    {
        return format;
    }

    private static bool IsDepthStencil(GraphicsFormat format) =>
        format is GraphicsFormat.D16UNorm or GraphicsFormat.D24UNormS8UInt or
            GraphicsFormat.D32Float or GraphicsFormat.D32FloatS8UInt;

    private static bool IsVertexFormat(GraphicsFormat format) =>
        !IsDepthStencil(format) &&
        format is not (GraphicsFormat.BC1UNorm or GraphicsFormat.BC1UNormSrgb or
            GraphicsFormat.BC3UNorm or GraphicsFormat.BC3UNormSrgb or GraphicsFormat.BC5UNorm or
            GraphicsFormat.BC7UNorm or GraphicsFormat.BC7UNormSrgb or
            GraphicsFormat.ASTC4x4UNorm or GraphicsFormat.ASTC4x4UNormSrgb);
}
