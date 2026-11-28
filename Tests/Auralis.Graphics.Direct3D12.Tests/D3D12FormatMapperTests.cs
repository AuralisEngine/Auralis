using Auralis.Graphics.Resources;
using Auralis.Graphics.Direct3D12.Devices;
using TerraFX.Interop.DirectX;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12FormatMapperTests
{
    [Theory]
    [InlineData(GraphicsFormat.R8UNorm, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM)]
    [InlineData(GraphicsFormat.RGBA8UNormSrgb, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB)]
    [InlineData(GraphicsFormat.BGRA8UNorm, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM)]
    [InlineData(GraphicsFormat.RGBA16Float, DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT)]
    [InlineData(GraphicsFormat.RGB32Float, DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT)]
    [InlineData(GraphicsFormat.D32FloatS8UInt, DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT)]
    [InlineData(GraphicsFormat.BC7UNormSrgb, DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM_SRGB)]
    public void SupportedCommonFormatsMapToExactDxgiFormats(
        GraphicsFormat format,
        DXGI_FORMAT expected)
    {
        Assert.True(D3D12FormatMapper.TryGetDxgiFormat(format, out DXGI_FORMAT actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(GraphicsFormat.Unknown)]
    [InlineData(GraphicsFormat.RGB16Float)]
    [InlineData(GraphicsFormat.RGB16UNorm)]
    [InlineData(GraphicsFormat.S8UInt)]
    public void UnsupportedCommonFormatsAreRejectedInsteadOfAliased(
        GraphicsFormat format)
    {
        Assert.False(D3D12FormatMapper.TryGetDxgiFormat(format, out DXGI_FORMAT dxgi));
        Assert.Equal(DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, dxgi);
    }

    [Fact]
    public void SupportMappingRequiresBothTypedUavLoadAndStore()
    {
        GraphicsFormatSupport loadOnly = D3D12FormatMapper.MapSupport(
            GraphicsFormat.R32Float,
            D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_TYPED_UNORDERED_ACCESS_VIEW,
            D3D12_FORMAT_SUPPORT2.D3D12_FORMAT_SUPPORT2_UAV_TYPED_LOAD,
            supportsAccelerationStructureVertex: false);

        Assert.False(loadOnly.HasFlag(GraphicsFormatSupport.Storage));

        GraphicsFormatSupport loadAndStore = D3D12FormatMapper.MapSupport(
            GraphicsFormat.R32Float,
            D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_TYPED_UNORDERED_ACCESS_VIEW,
            D3D12_FORMAT_SUPPORT2.D3D12_FORMAT_SUPPORT2_UAV_TYPED_LOAD |
            D3D12_FORMAT_SUPPORT2.D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE,
            supportsAccelerationStructureVertex: false);

        Assert.True(loadAndStore.HasFlag(GraphicsFormatSupport.Storage));
    }

    [Fact]
    public void IntegerFormatDoesNotAdvertiseLinearFiltering()
    {
        GraphicsFormatSupport support = D3D12FormatMapper.MapSupport(
            GraphicsFormat.R32UInt,
            D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE,
            (D3D12_FORMAT_SUPPORT2)0,
            supportsAccelerationStructureVertex: false);

        Assert.True(support.HasFlag(GraphicsFormatSupport.Sampled));
        Assert.False(support.HasFlag(GraphicsFormatSupport.LinearFiltering));
    }

    [Fact]
    public void FloatPositionFormatCanAdvertiseAccelerationStructureVertexUsage()
    {
        GraphicsFormatSupport support = D3D12FormatMapper.MapSupport(
            GraphicsFormat.RGB32Float,
            D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_IA_VERTEX_BUFFER,
            (D3D12_FORMAT_SUPPORT2)0,
            supportsAccelerationStructureVertex: true);

        Assert.True(support.HasFlag(GraphicsFormatSupport.AccelerationStructureVertex));
    }

    [Fact]
    public void SampleCountBitsMapToCommonFlagsWithoutInventingCounts()
    {
        GraphicsSampleCountFlags flags = D3D12FormatMapper.MapSampleCounts(1 | 4 | 16);

        Assert.Equal(
            GraphicsSampleCountFlags.One |
            GraphicsSampleCountFlags.Four |
            GraphicsSampleCountFlags.Sixteen,
            flags);
    }
}
