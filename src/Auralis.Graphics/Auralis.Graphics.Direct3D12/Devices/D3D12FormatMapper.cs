using Auralis.Graphics.Resources;
using TerraFX.Interop.DirectX;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>Exact common-format to DXGI-format mapping and capability conversion.</summary>
internal static class D3D12FormatMapper
{
    public static bool TryGetDxgiFormat(GraphicsFormat format, out DXGI_FORMAT dxgiFormat)
    {
        dxgiFormat = format switch
        {
            GraphicsFormat.R8UNorm => DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
            GraphicsFormat.R8SNorm => DXGI_FORMAT.DXGI_FORMAT_R8_SNORM,
            GraphicsFormat.R8UInt => DXGI_FORMAT.DXGI_FORMAT_R8_UINT,
            GraphicsFormat.R8SInt => DXGI_FORMAT.DXGI_FORMAT_R8_SINT,
            GraphicsFormat.RG8UNorm => DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM,
            GraphicsFormat.RG8SNorm => DXGI_FORMAT.DXGI_FORMAT_R8G8_SNORM,
            GraphicsFormat.RG8UInt => DXGI_FORMAT.DXGI_FORMAT_R8G8_UINT,
            GraphicsFormat.RG8SInt => DXGI_FORMAT.DXGI_FORMAT_R8G8_SINT,
            GraphicsFormat.RGBA8UNorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            GraphicsFormat.RGBA8UNormSrgb => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
            GraphicsFormat.RGBA8SNorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_SNORM,
            GraphicsFormat.RGBA8UInt => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UINT,
            GraphicsFormat.RGBA8SInt => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_SINT,
            GraphicsFormat.R10G10B10A2UNorm => DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM,
            GraphicsFormat.R10G10B10A2UInt => DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UINT,
            GraphicsFormat.R11G11B10Float => DXGI_FORMAT.DXGI_FORMAT_R11G11B10_FLOAT,
            GraphicsFormat.BGRA8UNorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            GraphicsFormat.BGRA8UNormSrgb => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
            GraphicsFormat.R16Float => DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT,
            GraphicsFormat.R16UNorm => DXGI_FORMAT.DXGI_FORMAT_R16_UNORM,
            GraphicsFormat.R16SNorm => DXGI_FORMAT.DXGI_FORMAT_R16_SNORM,
            GraphicsFormat.R16UInt => DXGI_FORMAT.DXGI_FORMAT_R16_UINT,
            GraphicsFormat.R16SInt => DXGI_FORMAT.DXGI_FORMAT_R16_SINT,
            GraphicsFormat.RG16Float => DXGI_FORMAT.DXGI_FORMAT_R16G16_FLOAT,
            GraphicsFormat.RG16UNorm => DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM,
            GraphicsFormat.RG16SNorm => DXGI_FORMAT.DXGI_FORMAT_R16G16_SNORM,
            GraphicsFormat.RG16UInt => DXGI_FORMAT.DXGI_FORMAT_R16G16_UINT,
            GraphicsFormat.RG16SInt => DXGI_FORMAT.DXGI_FORMAT_R16G16_SINT,
            GraphicsFormat.RGBA16Float => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
            GraphicsFormat.RGBA16UNorm => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM,
            GraphicsFormat.RGBA16SNorm => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_SNORM,
            GraphicsFormat.RGBA16UInt => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UINT,
            GraphicsFormat.RGBA16SInt => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_SINT,
            GraphicsFormat.R32Float => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
            GraphicsFormat.R32UInt => DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
            GraphicsFormat.R32SInt => DXGI_FORMAT.DXGI_FORMAT_R32_SINT,
            GraphicsFormat.RG32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
            GraphicsFormat.RG32UInt => DXGI_FORMAT.DXGI_FORMAT_R32G32_UINT,
            GraphicsFormat.RG32SInt => DXGI_FORMAT.DXGI_FORMAT_R32G32_SINT,
            GraphicsFormat.RGB32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
            GraphicsFormat.RGBA32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
            GraphicsFormat.RGBA32UInt => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_UINT,
            GraphicsFormat.RGBA32SInt => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_SINT,
            GraphicsFormat.D16UNorm => DXGI_FORMAT.DXGI_FORMAT_D16_UNORM,
            GraphicsFormat.D24UNormS8UInt => DXGI_FORMAT.DXGI_FORMAT_D24_UNORM_S8_UINT,
            GraphicsFormat.D32Float => DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT,
            GraphicsFormat.D32FloatS8UInt => DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT,
            GraphicsFormat.BC1UNorm => DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM,
            GraphicsFormat.BC1UNormSrgb => DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM_SRGB,
            GraphicsFormat.BC2UNorm => DXGI_FORMAT.DXGI_FORMAT_BC2_UNORM,
            GraphicsFormat.BC2UNormSrgb => DXGI_FORMAT.DXGI_FORMAT_BC2_UNORM_SRGB,
            GraphicsFormat.BC3UNorm => DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM,
            GraphicsFormat.BC3UNormSrgb => DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM_SRGB,
            GraphicsFormat.BC4UNorm => DXGI_FORMAT.DXGI_FORMAT_BC4_UNORM,
            GraphicsFormat.BC4SNorm => DXGI_FORMAT.DXGI_FORMAT_BC4_SNORM,
            GraphicsFormat.BC5UNorm => DXGI_FORMAT.DXGI_FORMAT_BC5_UNORM,
            GraphicsFormat.BC5SNorm => DXGI_FORMAT.DXGI_FORMAT_BC5_SNORM,
            GraphicsFormat.BC6HUFloat => DXGI_FORMAT.DXGI_FORMAT_BC6H_UF16,
            GraphicsFormat.BC6HSFloat => DXGI_FORMAT.DXGI_FORMAT_BC6H_SF16,
            GraphicsFormat.BC7UNorm => DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM,
            GraphicsFormat.BC7UNormSrgb => DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM_SRGB,
            _ => DXGI_FORMAT.DXGI_FORMAT_UNKNOWN
        };

        return dxgiFormat != DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
    }

    public static GraphicsFormatSupport MapSupport(
        GraphicsFormat format,
        D3D12_FORMAT_SUPPORT1 support1,
        D3D12_FORMAT_SUPPORT2 support2,
        bool supportsAccelerationStructureVertex)
    {
        GraphicsFormatSupport support = GraphicsFormatSupport.None;
        bool shaderResource =
            (support1 & (D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE |
                         D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_LOAD)) != 0;

        if (shaderResource)
            support |= GraphicsFormatSupport.Sampled;

        if ((support1 & D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_TYPED_UNORDERED_ACCESS_VIEW) != 0 &&
            (support2 & (D3D12_FORMAT_SUPPORT2.D3D12_FORMAT_SUPPORT2_UAV_TYPED_LOAD |
                         D3D12_FORMAT_SUPPORT2.D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE)) ==
                       (D3D12_FORMAT_SUPPORT2.D3D12_FORMAT_SUPPORT2_UAV_TYPED_LOAD |
                        D3D12_FORMAT_SUPPORT2.D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE))
        {
            support |= GraphicsFormatSupport.Storage;
        }

        if ((support1 & D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_RENDER_TARGET) != 0)
            support |= GraphicsFormatSupport.ColorAttachment;

        if ((support1 & D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_DEPTH_STENCIL) != 0)
            support |= GraphicsFormatSupport.DepthStencilAttachment;

        if ((support1 & D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_BLENDABLE) != 0)
            support |= GraphicsFormatSupport.Blend;

        if ((support1 & D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_IA_VERTEX_BUFFER) != 0)
            support |= GraphicsFormatSupport.VertexBuffer;

        if ((support1 & (D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_BUFFER |
                         D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_TEXTURE1D |
                         D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_TEXTURE2D |
                         D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_TEXTURE3D)) != 0)
        {
            support |= GraphicsFormatSupport.CopySource | GraphicsFormatSupport.CopyDestination;
        }

        if (!IsIntegerOrDepthFormat(format) &&
            (support1 & D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE) != 0)
        {
            support |= GraphicsFormatSupport.LinearFiltering;
        }

        if (supportsAccelerationStructureVertex)
            support |= GraphicsFormatSupport.AccelerationStructureVertex;

        return support;
    }

    public static GraphicsSampleCountFlags MapSampleCounts(uint supportedSampleCounts)
    {
        GraphicsSampleCountFlags result = GraphicsSampleCountFlags.None;
        if ((supportedSampleCounts & 1) != 0) result |= GraphicsSampleCountFlags.One;
        if ((supportedSampleCounts & 2) != 0) result |= GraphicsSampleCountFlags.Two;
        if ((supportedSampleCounts & 4) != 0) result |= GraphicsSampleCountFlags.Four;
        if ((supportedSampleCounts & 8) != 0) result |= GraphicsSampleCountFlags.Eight;
        if ((supportedSampleCounts & 16) != 0) result |= GraphicsSampleCountFlags.Sixteen;
        if ((supportedSampleCounts & 32) != 0) result |= GraphicsSampleCountFlags.ThirtyTwo;
        if ((supportedSampleCounts & 64) != 0) result |= GraphicsSampleCountFlags.SixtyFour;
        return result;
    }

    private static bool IsIntegerOrDepthFormat(GraphicsFormat format) => format switch
    {
        GraphicsFormat.R8UInt or GraphicsFormat.R8SInt or
        GraphicsFormat.RG8UInt or GraphicsFormat.RG8SInt or
        GraphicsFormat.RGBA8UInt or GraphicsFormat.RGBA8SInt or
        GraphicsFormat.R10G10B10A2UInt or
        GraphicsFormat.R16UInt or GraphicsFormat.R16SInt or
        GraphicsFormat.RG16UInt or GraphicsFormat.RG16SInt or
        GraphicsFormat.RGBA16UInt or GraphicsFormat.RGBA16SInt or
        GraphicsFormat.R32UInt or GraphicsFormat.R32SInt or
        GraphicsFormat.RG32UInt or GraphicsFormat.RG32SInt or
        GraphicsFormat.RGBA32UInt or GraphicsFormat.RGBA32SInt or
        GraphicsFormat.D16UNorm or GraphicsFormat.D24UNormS8UInt or
        GraphicsFormat.D32Float or GraphicsFormat.D32FloatS8UInt => true,
        _ => false
    };
}
