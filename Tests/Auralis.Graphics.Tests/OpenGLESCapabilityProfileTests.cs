using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Xunit;

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

public sealed class OpenGLESBackendProviderTests
{
    [Fact]
    public void ProviderRegistersInTheExistingBackendRegistry()
    {
        FakeOpenGLESContextProvider context = new();
        OpenGLESGraphicsBackendProvider provider = new(context);
        GraphicsBackendProviderRegistry registry = new();

        registry.Register(provider);

        Assert.Equal(1, registry.Count);
        Assert.Same(provider, registry.GetProvider(GraphicsBackend.OpenGLES));
    }

    [Fact]
    public async Task FactoryRejectsAnUnavailableGlesProvider()
    {
        FakeOpenGLESContextProvider context = new() { Available = false };
        GraphicsBackendProviderRegistry registry = new();
        registry.Register(new OpenGLESGraphicsBackendProvider(context));

        await Assert.ThrowsAsync<GraphicsBackendUnavailableException>(async () =>
            await GraphicsDeviceFactory.CreateAsync(
                registry,
                new GraphicsDeviceOptions { Backend = GraphicsBackend.OpenGLES }));
    }

    [Fact]
    public async Task OwnedContextProviderIsDisposedWhenDeviceInitializationFails()
    {
        FakeOpenGLESContextProvider context = new()
        {
            Ownership = OpenGLESContextOwnership.Owned,
            ThrowFromMakeCurrent = true
        };
        OpenGLESGraphicsBackendProvider provider = new(context);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.CreateDeviceAsync(new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.OpenGLES,
                RequiredQueues = GraphicsQueueCapabilities.Graphics
            }));

        Assert.True(context.IsDisposed);
        Assert.Equal(0, context.ReleaseCurrentCount);
    }

    private sealed class FakeOpenGLESContextProvider : IOpenGLESContextProvider
    {
        public object ContextIdentity => this;
        public OpenGLESContextOwnership Ownership { get; set; } = OpenGLESContextOwnership.Borrowed;
        public OpenGLESContextStateOwnership StateOwnership => OpenGLESContextStateOwnership.Exclusive;
        public bool Available { get; set; } = true;
        public bool IsAvailable => Available;
        public string? UnavailableReason => IsAvailable ? null : "Disabled in test.";
        public bool SupportsPresentation => false;
        public bool ThrowFromMakeCurrent { get; set; }
        public bool IsDisposed { get; private set; }
        public int ReleaseCurrentCount { get; private set; }

        public bool IsPresentationTargetCompatible(in GraphicsPresentationTargetSnapshot target) => false;
        public bool SupportsPresentMode(GraphicsPresentMode presentMode) => false;
        public void SetPresentMode(GraphicsPresentMode presentMode) => throw new NotSupportedException();
        public void MakeCurrent()
        {
            if (ThrowFromMakeCurrent)
                throw new InvalidOperationException("Simulated GLES context activation failure.");
        }
        public void ReleaseCurrent() => ReleaseCurrentCount++;
        public nint GetProcAddress(string name) => 0;
        public void SwapBuffers() => throw new NotSupportedException();
        public void Dispose() => IsDisposed = true;
    }
}

public sealed class OpenGLESShaderCompatibilityTests
{
    [Fact]
    public void Es31_AllowsVertexFragmentAndComputeStages()
    {
        OpenGLESVersion version = new(3, 1);

        Assert.Equal(Silk.NET.OpenGLES.ShaderType.VertexShader,
            OpenGLESShaderCompatibility.GetNativeStage(version, GraphicsShaderStage.Vertex));
        Assert.Equal(Silk.NET.OpenGLES.ShaderType.FragmentShader,
            OpenGLESShaderCompatibility.GetNativeStage(version, GraphicsShaderStage.Fragment));
        Assert.Equal(Silk.NET.OpenGLES.ShaderType.ComputeShader,
            OpenGLESShaderCompatibility.GetNativeStage(version, GraphicsShaderStage.Compute));
        Assert.Throws<GraphicsFeatureNotSupportedException>(() =>
            OpenGLESShaderCompatibility.GetNativeStage(version, GraphicsShaderStage.Geometry));
        Assert.Throws<GraphicsFeatureNotSupportedException>(() =>
            OpenGLESShaderCompatibility.GetNativeStage(version, GraphicsShaderStage.TessellationControl));
    }

    [Fact]
    public void Es32_AddsGeometryAndTessellationShaderStages()
    {
        OpenGLESVersion version = new(3, 2);

        Assert.Equal(Silk.NET.OpenGLES.ShaderType.GeometryShader,
            OpenGLESShaderCompatibility.GetNativeStage(version, GraphicsShaderStage.Geometry));
        Assert.Equal(Silk.NET.OpenGLES.ShaderType.TessControlShader,
            OpenGLESShaderCompatibility.GetNativeStage(version, GraphicsShaderStage.TessellationControl));
        Assert.Equal(Silk.NET.OpenGLES.ShaderType.TessEvaluationShader,
            OpenGLESShaderCompatibility.GetNativeStage(version, GraphicsShaderStage.TessellationEvaluation));
    }

    [Theory]
    [InlineData(GraphicsShaderStage.Task)]
    [InlineData(GraphicsShaderStage.Mesh)]
    public void MeshAndTaskStagesAreNeverClaimedAsGlesCore(GraphicsShaderStage stage)
    {
        Assert.Throws<GraphicsFeatureNotSupportedException>(() =>
            OpenGLESShaderCompatibility.GetNativeStage(new OpenGLESVersion(3, 2), stage));
    }

    [Fact]
    public void ValidatesGlslEsLanguageAndContextCompatibility()
    {
        OpenGLESShaderCompatibility.ValidateSourceVersion(
            new OpenGLESVersion(3, 1),
            GraphicsShaderStage.Vertex,
            "#version 300 es\nvoid main(){}");
        OpenGLESShaderCompatibility.ValidateSourceVersion(
            new OpenGLESVersion(3, 1),
            GraphicsShaderStage.Compute,
            "#version 310 es\nlayout(local_size_x=1) in; void main(){}");
        OpenGLESShaderCompatibility.ValidateSourceVersion(
            new OpenGLESVersion(3, 2),
            GraphicsShaderStage.Geometry,
            "#version 320 es\nlayout(points) in; layout(points, max_vertices=1) out; void main(){}");
    }

    [Theory]
    [InlineData("#version 450 core\nvoid main(){}")]
    [InlineData("#version 100 es\nvoid main(){}")]
    [InlineData("void main(){}")]
    public void RejectsDesktopLegacyOrMissingVersionDirectives(string source)
    {
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESShaderCompatibility.ValidateSourceVersion(
                new OpenGLESVersion(3, 2), GraphicsShaderStage.Vertex, source));
    }

    [Fact]
    public void RejectsComputeAndGeometryStagesWithOlderGlslEsVersions()
    {
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESShaderCompatibility.ValidateSourceVersion(
                new OpenGLESVersion(3, 1), GraphicsShaderStage.Compute, "#version 300 es\nvoid main(){}"));
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESShaderCompatibility.ValidateSourceVersion(
                new OpenGLESVersion(3, 2), GraphicsShaderStage.Geometry, "#version 310 es\nvoid main(){}"));
    }

    [Fact]
    public void RejectsGlslEsVersionHigherThanContext()
    {
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESShaderCompatibility.ValidateSourceVersion(
                new OpenGLESVersion(3, 1), GraphicsShaderStage.Vertex, "#version 320 es\nvoid main(){}"));
    }
}

public sealed class OpenGLESTextureCompatibilityTests
{
    [Theory]
    [InlineData(GraphicsFormat.R8UNorm, Silk.NET.OpenGLES.SizedInternalFormat.R8)]
    [InlineData(GraphicsFormat.RG8UNorm, Silk.NET.OpenGLES.SizedInternalFormat.RG8)]
    [InlineData(GraphicsFormat.RGBA8UNorm, Silk.NET.OpenGLES.SizedInternalFormat.Rgba8)]
    [InlineData(GraphicsFormat.RGBA8UNormSrgb, Silk.NET.OpenGLES.SizedInternalFormat.Srgb8Alpha8)]
    public void SupportedFormatsHaveAnExplicitGlesInternalFormat(
        GraphicsFormat format,
        Silk.NET.OpenGLES.SizedInternalFormat expected)
    {
        Assert.Equal(expected, OpenGLESGraphicsTexture.GetInternalFormat(format));
    }

    [Fact]
    public void UnsupportedTextureFormatIsRejected()
    {
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESGraphicsTexture.GetInternalFormat(GraphicsFormat.Unknown));
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESGraphicsTexture.GetInternalFormat(GraphicsFormat.BGRA8UNorm));
    }

    [Fact]
    public void ValidGlesTextureDescriptionMapsToExpectedTarget()
    {
        GraphicsTextureDescription description = new(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            128, 64, 1, 8, 1, 1,
            GraphicsTextureUsage.Sampled | GraphicsTextureUsage.Storage);

        Assert.Equal(
            Silk.NET.OpenGLES.TextureTarget.Texture2D,
            OpenGLESTextureCompatibility.ValidateAndGetTarget(description));
    }

    [Fact]
    public void TwoDimensionalArrayUsesGlesArrayTarget()
    {
        GraphicsTextureDescription description = new(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            128, 64, 1, 8, 4, 1,
            GraphicsTextureUsage.Sampled);

        Assert.Equal(
            Silk.NET.OpenGLES.TextureTarget.Texture2DArray,
            OpenGLESTextureCompatibility.ValidateAndGetTarget(description));
    }

    [Fact]
    public void RejectsUnsupportedGlesTextureKindsWithoutAContext()
    {
        GraphicsTextureDescription oneDimensional = new(
            GraphicsTextureDimension.Texture1D,
            GraphicsFormat.RGBA8UNorm,
            128, 1, 1, 8, 1, 1,
            GraphicsTextureUsage.Sampled);
        GraphicsTextureDescription multisampled = new(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            128, 64, 1, 1, 1, 4,
            GraphicsTextureUsage.Sampled);
        GraphicsTextureDescription cubeArray = new(
            GraphicsTextureDimension.Cube,
            GraphicsFormat.RGBA8UNorm,
            128, 128, 1, 8, 12, 1,
            GraphicsTextureUsage.Sampled);

        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESTextureCompatibility.ValidateAndGetTarget(oneDimensional));
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESTextureCompatibility.ValidateAndGetTarget(multisampled));
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESTextureCompatibility.ValidateAndGetTarget(cubeArray));
    }
}
