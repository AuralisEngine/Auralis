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
    public async Task ProviderExposesItsHostSelectedAdapterToTheFactory()
    {
        FakeOpenGLESContextProvider context = new();
        OpenGLESGraphicsBackendProvider provider = new(context);

        ReadOnlyMemory<GraphicsAdapterInfo> adapters = await provider.EnumerateAdaptersAsync();

        Assert.Single(adapters.ToArray());
        Assert.True(adapters.Span[0].Id.IsDefault);
        Assert.Equal("OpenGL ES host context", adapters.Span[0].Name);
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


public sealed class OpenGLESComputeWorkGroupValidationTests
{
    private static readonly GraphicsDeviceLimits s_limits = new(
        MaxBufferSizeInBytes: 0,
        MaxTextureDimension1D: 0,
        MaxTextureDimension2D: 0,
        MaxTextureDimension3D: 0,
        MaxTextureArrayLayers: 0,
        MaxVertexInputAttributes: 0,
        MaxVertexInputBindings: 0,
        MaxBoundDescriptorSets: 0,
        MaxComputeWorkGroupInvocations: 256,
        MaxComputeWorkGroupSizeX: 128,
        MaxComputeWorkGroupSizeY: 128,
        MaxComputeWorkGroupSizeZ: 64,
        MaxSamplerAnisotropy: 1);

    [Fact]
    public void Dispatch_AllowsCountsImmediatelyBelowEachAxisLimit() =>
        OpenGLESComputeWorkGroupValidation.ValidateDispatch(65_534, 65_534, 65_534, 65_535, 65_535, 65_535);

    [Theory]
    [InlineData(0u, 1u, 1u)]
    [InlineData(1u, 0u, 1u)]
    [InlineData(1u, 1u, 0u)]
    public void Dispatch_RejectsZeroCountOnEveryAxis(uint x, uint y, uint z) =>
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESComputeWorkGroupValidation.ValidateDispatch(x, y, z, 65_535, 65_535, 65_535));

    [Theory]
    [InlineData(65_536u, 1u, 1u)]
    [InlineData(1u, 65_536u, 1u)]
    [InlineData(1u, 1u, 65_536u)]
    public void Dispatch_RejectsEachAxisIndependentlyWhenItExceedsTheLimit(uint x, uint y, uint z) =>
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESComputeWorkGroupValidation.ValidateDispatch(x, y, z, 65_535, 65_535, 65_535));


    [Theory]
    [InlineData(65_535u, 1u, 1u)]
    [InlineData(1u, 65_535u, 1u)]
    [InlineData(1u, 1u, 65_535u)]
    public void Dispatch_RejectsCountsEqualToEachAxisLimit(uint x, uint y, uint z) =>
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESComputeWorkGroupValidation.ValidateDispatch(x, y, z, 65_535, 65_535, 65_535));

    [Fact]
    public void Dispatch_RejectsMissingNativeLimits() =>
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESComputeWorkGroupValidation.ValidateDispatch(1, 1, 1, 65_535, 0, 65_535));

    [Fact]
    public void LocalSize_AllowsAxisAndInvocationValuesAtLimits() =>
        OpenGLESComputeWorkGroupValidation.ValidateLocalSize(16, 16, 1, s_limits);

    [Theory]
    [InlineData(129, 1, 1)]
    [InlineData(1, 129, 1)]
    [InlineData(1, 1, 65)]
    public void LocalSize_RejectsPerAxisLimitViolations(int x, int y, int z) =>
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESComputeWorkGroupValidation.ValidateLocalSize(x, y, z, s_limits));

    [Fact]
    public void LocalSize_RejectsProductAboveInvocationLimit() =>
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESComputeWorkGroupValidation.ValidateLocalSize(17, 16, 1, s_limits));

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, 0)]
    public void LocalSize_RejectsNonPositiveDimensions(int x, int y, int z) =>
        Assert.Throws<GraphicsValidationException>(() =>
            OpenGLESComputeWorkGroupValidation.ValidateLocalSize(x, y, z, s_limits));
}


using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Devices;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class OpenGLESFormatCapabilityTests
{
    private static GraphicsDeviceCapabilities CreateCapabilities(int major = 3, int minor = 1)
    {
        OpenGLESCapabilityProfile profile = OpenGLESCapabilityProfile.Create(
            new OpenGLESVersion(major, minor),
            ReadOnlySpan<string>.Empty,
            supportsPresentation: false);

        OpenGLESGraphicsDevice.OpenGLESQueriedLimits limits = new(
            MaxTextureSize: 4096,
            MaxTexture3DSize: 256,
            MaxArrayLayers: 256,
            MaxVertexAttributes: 16,
            MaxUniformBufferBindings: 24,
            MaxStorageBufferBindings: 8,
            MaxTextureUnits: 16,
            MaxImageUnits: major > 3 || (major == 3 && minor >= 1) ? 8u : 0u,
            UniformBufferOffsetAlignment: 256,
            StorageBufferOffsetAlignment: 16,
            MaxComputeInvocations: major > 3 || (major == 3 && minor >= 1) ? 256u : 0u,
            MaxComputeWorkGroupSizeX: major > 3 || (major == 3 && minor >= 1) ? 128u : 0u,
            MaxComputeWorkGroupSizeY: major > 3 || (major == 3 && minor >= 1) ? 128u : 0u,
            MaxComputeWorkGroupSizeZ: major > 3 || (major == 3 && minor >= 1) ? 64u : 0u,
            MaxComputeWorkGroupCountX: major > 3 || (major == 3 && minor >= 1) ? 65_535u : 0u,
            MaxComputeWorkGroupCountY: major > 3 || (major == 3 && minor >= 1) ? 65_535u : 0u,
            MaxComputeWorkGroupCountZ: major > 3 || (major == 3 && minor >= 1) ? 65_535u : 0u,
            MaxTessellationPatchSize: minor >= 2 ? 32u : 0u,
            MaxAnisotropy: 1);

        return OpenGLESGraphicsDevice.QueryCapabilities(profile, limits);
    }

    [Theory]
    [InlineData(GraphicsFormat.R8UNorm)]
    [InlineData(GraphicsFormat.RG8UNorm)]
    [InlineData(GraphicsFormat.D16UNorm)]
    [InlineData(GraphicsFormat.D24UNormS8UInt)]
    public void FormatsOutsideImplementedColorBlitPath_DoNotAdvertiseTextureCopy(GraphicsFormat format)
    {
        GraphicsDeviceCapabilities capabilities = CreateCapabilities();

        Assert.True(capabilities.TryGetFormatCapabilities(format, out GraphicsFormatCapabilities support));
        Assert.False(support.Supports(GraphicsFormatSupport.CopySource));
        Assert.False(support.Supports(GraphicsFormatSupport.CopyDestination));
    }

    [Theory]
    [InlineData(GraphicsFormat.RGBA8UNorm)]
    [InlineData(GraphicsFormat.RGBA8UNormSrgb)]
    public void ImplementedRgbaColorBlitFormats_AdvertiseTextureCopy(GraphicsFormat format)
    {
        GraphicsDeviceCapabilities capabilities = CreateCapabilities();

        Assert.True(capabilities.TryGetFormatCapabilities(format, out GraphicsFormatCapabilities support));
        Assert.True(support.Supports(GraphicsFormatSupport.CopySource));
        Assert.True(support.Supports(GraphicsFormatSupport.CopyDestination));
        Assert.True(support.Supports(GraphicsFormatSupport.ColorAttachment));
    }

    [Fact]
    public void Rgba8StorageSupport_IsReportedOnlyForGles31AndNewer()
    {
        GraphicsDeviceCapabilities es30 = CreateCapabilities(3, 0);
        GraphicsDeviceCapabilities es31 = CreateCapabilities(3, 1);

        Assert.True(es30.TryGetFormatCapabilities(GraphicsFormat.RGBA8UNorm, out GraphicsFormatCapabilities es30Support));
        Assert.True(es31.TryGetFormatCapabilities(GraphicsFormat.RGBA8UNorm, out GraphicsFormatCapabilities es31Support));
        Assert.False(es30Support.Supports(GraphicsFormatSupport.Storage));
        Assert.True(es31Support.Supports(GraphicsFormatSupport.Storage));
    }

    [Fact]
    public void NormalizedAndDepthFormats_RetainTheirImplementedUsages()
    {
        GraphicsDeviceCapabilities capabilities = CreateCapabilities();

        Assert.True(capabilities.SupportsFormat(
            GraphicsFormat.R8UNorm,
            GraphicsFormatSupport.Sampled | GraphicsFormatSupport.VertexBuffer | GraphicsFormatSupport.LinearFiltering));
        Assert.True(capabilities.SupportsFormat(
            GraphicsFormat.RG8UNorm,
            GraphicsFormatSupport.Sampled | GraphicsFormatSupport.VertexBuffer | GraphicsFormatSupport.LinearFiltering));
        Assert.True(capabilities.SupportsFormat(
            GraphicsFormat.D16UNorm,
            GraphicsFormatSupport.Sampled | GraphicsFormatSupport.DepthStencilAttachment));
        Assert.True(capabilities.SupportsFormat(
            GraphicsFormat.D24UNormS8UInt,
            GraphicsFormatSupport.Sampled | GraphicsFormatSupport.DepthStencilAttachment));
    }
}
