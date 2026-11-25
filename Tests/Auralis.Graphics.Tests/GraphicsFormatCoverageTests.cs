using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class GraphicsFormatCoverageTests
{
    [Theory]
    [InlineData(GraphicsFormat.BC2UNorm)]
    [InlineData(GraphicsFormat.BC2UNormSrgb)]
    [InlineData(GraphicsFormat.BC4SNorm)]
    [InlineData(GraphicsFormat.BC5SNorm)]
    [InlineData(GraphicsFormat.ETC2RGB8UNorm)]
    [InlineData(GraphicsFormat.ETC2RGB8UNormSrgb)]
    [InlineData(GraphicsFormat.ETC2RGBA8UNorm)]
    [InlineData(GraphicsFormat.ETC2RGBA8UNormSrgb)]
    [InlineData(GraphicsFormat.ASTC6x6UNorm)]
    [InlineData(GraphicsFormat.ASTC6x6UNormSrgb)]
    [InlineData(GraphicsFormat.ASTC8x8UNorm)]
    [InlineData(GraphicsFormat.ASTC8x8UNormSrgb)]
    public void AdditionalCompressedFormatsCanBeAdvertisedByDeviceCapabilities(GraphicsFormat format)
    {
        var capabilities = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.None),
            GraphicsQueueCapabilities.None,
            default,
            [new GraphicsFormatCapabilities(
                format,
                GraphicsFormatSupport.Sampled,
                GraphicsSampleCountFlags.One)]);

        Assert.True(capabilities.SupportsFormat(format, GraphicsFormatSupport.Sampled));
        Assert.False(capabilities.SupportsFormat(format, GraphicsFormatSupport.Storage));
        Assert.True(capabilities.SupportsSampleCount(format, 1));
    }

    [Theory]
    [InlineData(0u, 1u, 1u, "Width")]
    [InlineData(1u, 0u, 1u, "Height")]
    [InlineData(1u, 1u, 0u, "Depth")]
    public void RayDispatchReportsTheInvalidDimensionParameter(
        uint width, uint height, uint depth, string expectedParameter)
    {
        var description = new GraphicsRayDispatchDescription(
            default, default, default, default, width, height, depth);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(description.Validate);
        Assert.Equal(expectedParameter, exception.ParamName);
    }
}
