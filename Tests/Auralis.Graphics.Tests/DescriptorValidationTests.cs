using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class DescriptorValidationTests
{
    [Fact]
    public void BufferDescriptionAcceptsValidGpuBuffer()
    {
        var description = new GraphicsBufferDescription(
            4096,
            GraphicsBufferUsage.Vertex | GraphicsBufferUsage.CopyDestination,
            GraphicsMemoryAccess.GpuOnly,
            GraphicsMemoryPreference.DeviceLocal,
            256);

        description.Validate();
    }

    [Fact]
    public void BufferDescriptionRejectsZeroSizeAndEmptyUsage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GraphicsBufferDescription(0, GraphicsBufferUsage.Vertex).Validate());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GraphicsBufferDescription(128, GraphicsBufferUsage.None).Validate());
    }

    [Fact]
    public void TextureDescriptionAcceptsValidMipChain()
    {
        var description = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            1024,
            512,
            1,
            11,
            1,
            1,
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.CopyDestination);

        description.Validate();
    }

    [Fact]
    public void TextureDescriptionRejectsInvalidDimensionsAndMipCount()
    {
        var invalidDimension = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture1D,
            GraphicsFormat.R8UNorm,
            64,
            2,
            1,
            1,
            1,
            1,
            GraphicsTextureUsage.Sampled);

        var invalidMipCount = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            8,
            8,
            1,
            5,
            1,
            1,
            GraphicsTextureUsage.Sampled);

        Assert.Throws<ArgumentException>(invalidDimension.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(invalidMipCount.Validate);
    }

    [Fact]
    public void TextureViewRejectsOutOfRangeSubresources()
    {
        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            256,
            256,
            1,
            8,
            4,
            1,
            GraphicsTextureUsage.Sampled);

        var view = new GraphicsTextureViewDescription(
            GraphicsFormat.Unknown,
            GraphicsTextureViewType.Texture2DArray,
            6,
            3,
            0,
            4);

        Assert.Throws<ArgumentOutOfRangeException>(() => view.Validate(texture));
    }

    [Fact]
    public void SamplerRejectsNonFiniteValuesAndInvalidLodRange()
    {
        var nonFinite = new GraphicsSamplerDescription(
            GraphicsFilter.Linear, GraphicsFilter.Linear, GraphicsFilter.Linear,
            GraphicsAddressMode.Repeat, GraphicsAddressMode.Repeat, GraphicsAddressMode.Repeat,
            0, 10, LodBias: float.NaN);

        var reversedRange = new GraphicsSamplerDescription(
            GraphicsFilter.Linear, GraphicsFilter.Linear, GraphicsFilter.Linear,
            GraphicsAddressMode.Repeat, GraphicsAddressMode.Repeat, GraphicsAddressMode.Repeat,
            8, 2);

        Assert.Throws<ArgumentException>(nonFinite.Validate);
        Assert.Throws<ArgumentException>(reversedRange.Validate);
    }

    [Fact]
    public void ShaderDescriptionRequiresArtifactAndEntryPoint()
    {
        var valid = new GraphicsShaderDescription
        {
            Stage = GraphicsShaderStage.Vertex,
            Format = GraphicsShaderFormat.SpirV,
            Code = new byte[] { 1, 2, 3 }
        };

        valid.Validate();

        var invalid = valid with { Code = ReadOnlyMemory<byte>.Empty };
        Assert.Throws<ArgumentException>(invalid.Validate);
    }
}
