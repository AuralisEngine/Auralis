using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Tests;

public sealed class OpenGLESCapabilityProfileTests
{
    [Theory]
    [InlineData("OpenGL ES 3.1 Mesa 24.0", 3, 1)]
    [InlineData("OpenGL ES 3.2 V@0502.0", 3, 2)]
    [InlineData("OpenGL ES-CM 1.1 Vendor", 1, 1)]
    [InlineData("OpenGL ES 3.10 Vendor", 3, 10)]
    public void TryParse_ParsesGlesVersionPrefix(string text, int major, int minor)
    {
        Assert.True(OpenGLESVersion.TryParse(text, out OpenGLESVersion parsed));
        Assert.Equal(new OpenGLESVersion(major, minor), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OpenGL 4.5 Core Profile")]
    [InlineData("OpenGL ES GLSL ES 3.20")]
    [InlineData("OpenGL ES version unknown")]
    public void TryParse_RejectsInvalidOrDesktopStrings(string? text)
    {
        Assert.False(OpenGLESVersion.TryParse(text, out _));
    }

    [Fact]
    public void Parse_ThrowsForDesktopOpenGlVersion()
    {
        Assert.Throws<FormatException>(() => OpenGLESVersion.Parse("4.5 Core Profile"));
    }

    [Fact]
    public void Es31_ExposesOnlyCore31Capabilities()
    {
        OpenGLESCapabilityProfile profile = OpenGLESCapabilityProfile.Create(
            new OpenGLESVersion(3, 1),
            ReadOnlySpan<string>.Empty,
            supportsPresentation: false);

        Assert.True(profile.MeetsRequiredBaseline);
        Assert.True(profile.Features.Supports(GraphicsFeature.ComputeShaders));
        Assert.True(profile.Features.Supports(GraphicsFeature.IndirectDrawing));
        Assert.False(profile.Features.Supports(GraphicsFeature.GeometryShaders));
        Assert.False(profile.Features.Supports(GraphicsFeature.TessellationShaders));
        Assert.False(profile.Features.Supports(GraphicsFeature.MeshShaders));
        Assert.False(profile.Features.Supports(GraphicsFeature.TaskShaders));
        Assert.False(profile.Features.Supports(GraphicsFeature.SpirVShaders));
        Assert.Equal(
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute | GraphicsQueueCapabilities.Copy,
            profile.QueueCapabilities);
    }

    [Fact]
    public void Es32_EnablesGeometryAndTessellationCoreFeatures()
    {
        OpenGLESCapabilityProfile profile = OpenGLESCapabilityProfile.Create(
            new OpenGLESVersion(3, 2),
            ReadOnlySpan<string>.Empty,
            supportsPresentation: true);

        Assert.True(profile.Features.Supports(GraphicsFeature.GeometryShaders));
        Assert.True(profile.Features.Supports(GraphicsFeature.TessellationShaders));
        Assert.True(profile.QueueCapabilities.HasFlag(GraphicsQueueCapabilities.Present));
    }

    [Fact]
    public void ExtensionFeatures_RequireExactExtensionNames()
    {
        string[] extensions =
        [
            "GL_EXT_texture_filter_anisotropic",
            "GL_EXT_draw_buffers_indexed",
            "GL_EXT_multi_draw_indirect"
        ];

        OpenGLESCapabilityProfile profile = OpenGLESCapabilityProfile.Create(
            new OpenGLESVersion(3, 1),
            extensions,
            supportsPresentation: false);

        Assert.True(profile.HasExtension("GL_EXT_texture_filter_anisotropic"));
        Assert.False(profile.HasExtension("GL_EXT_texture_filter"));
        Assert.True(profile.Features.Supports(GraphicsFeature.SamplerAnisotropy));
        Assert.False(profile.Features.Supports(GraphicsFeature.IndependentBlend));
        Assert.False(profile.Features.Supports(GraphicsFeature.MultiDrawIndirect));
        Assert.True(profile.Features.Supports(GraphicsFeature.TimelineSynchronization));
    }

    [Fact]
    public void ExtensionNames_AreValidatedAndDuplicatesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => OpenGLESCapabilityProfile.Create(
            new OpenGLESVersion(3, 1),
            new[] { "GL_EXT_one", "GL_EXT_one" },
            supportsPresentation: false));

        Assert.Throws<ArgumentException>(() => OpenGLESCapabilityProfile.Create(
            new OpenGLESVersion(3, 1),
            new[] { "GL_EXT_one two" },
            supportsPresentation: false));
    }

    [Fact]
    public void Es30_DoesNotMeetBaseline_AndComputeQueueIsNotAdvertised()
    {
        OpenGLESCapabilityProfile profile = OpenGLESCapabilityProfile.Create(
            new OpenGLESVersion(3, 0),
            ReadOnlySpan<string>.Empty,
            supportsPresentation: false);

        Assert.False(profile.MeetsRequiredBaseline);
        Assert.Throws<NotSupportedException>(profile.EnsureRequiredBaseline);
        Assert.False(profile.QueueCapabilities.HasFlag(GraphicsQueueCapabilities.Compute));
    }

    [Fact]
    public void ExistingDeviceOptions_AcceptOpenGlesBackendSelection()
    {
        GraphicsDeviceOptions options = new()
        {
            Backend = GraphicsBackend.OpenGLES,
            RequiredQueues = GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute
        };

        options.Validate();
        Assert.Equal(GraphicsBackend.OpenGLES, options.Backend);
    }

    [Fact]
    public void BackendIdentifier_IsPartOfExistingGraphicsBackendEnum()
    {
        Assert.True(Enum.IsDefined(GraphicsBackend.OpenGLES));
    }
}

public sealed class OpenGLESResourceReferenceStateTests
{
    [Fact]
    public void OwnerClose_PreventsNewRetains_ButAllowsExistingDependentsToRetain()
    {
        OpenGLESResourceReferenceState state = new();

        Assert.True(state.TryRetain()); // command or texture-view owner
        Assert.False(state.CloseOwner());
        Assert.True(state.IsClosed);
        Assert.False(state.TryRetain());
        Assert.True(state.TryRetainDependency());
        Assert.Equal(2, state.ReferenceCount);

        Assert.False(state.ReleaseReference());
        Assert.True(state.ReleaseReference());
        Assert.Equal(0, state.ReferenceCount);
    }

    [Fact]
    public void CloseOwner_IsIdempotent_AndReleasesOnlyTheOwnerReference()
    {
        OpenGLESResourceReferenceState state = new();

        Assert.True(state.CloseOwner());
        Assert.False(state.CloseOwner());
        Assert.Equal(0, state.ReferenceCount);
        Assert.False(state.TryRetainDependency());
        Assert.Throws<InvalidOperationException>(() => state.ReleaseReference());
    }

    [Fact]
    public void ConcurrentCommandRetains_ReleaseExactlyOneFinalReference()
    {
        OpenGLESResourceReferenceState state = new();
        const int workers = 16;
        const int iterations = 2_000;
        int finalReleases = 0;

        Parallel.For(0, workers, _ =>
        {
            for (int i = 0; i < iterations; i++)
            {
                Assert.True(state.TryRetain());
                if (state.ReleaseReference())
                    Interlocked.Increment(ref finalReleases);
            }
        });

        Assert.Equal(1, state.ReferenceCount);
        Assert.Equal(0, finalReleases);
        Assert.True(state.CloseOwner());
        Assert.Equal(0, state.ReferenceCount);
    }
}
