using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Graphics device over one externally supplied OpenGL ES context. All driver work is serialized
/// because the host context is made current on the calling thread for each operation.
/// </summary>
internal sealed class OpenGLESGraphicsDevice : IGraphicsDevice
{
    private readonly IOpenGLESContextProvider _contextProvider;
    private readonly GL _gl;
    private readonly object _contextGate = new();
    private readonly object _resourceGate = new();
    private readonly HashSet<IOpenGLESNativeResource> _resources = new(ReferenceEqualityComparer.Instance);
    private readonly object _queueGate = new();
    private readonly OpenGLESGraphicsCommandQueue?[] _queues = new OpenGLESGraphicsCommandQueue?[3];
    private readonly bool _ownsContextProvider;
    private int _disposed;

    private OpenGLESGraphicsDevice(
        IOpenGLESContextProvider contextProvider,
        GL gl,
        OpenGLESVersion version,
        string versionString,
        string vendor,
        string renderer,
        OpenGLESCapabilityProfile profile,
        GraphicsDeviceCapabilities capabilities)
    {
        _contextProvider = contextProvider;
        _gl = gl;
        Version = version;
        VersionString = versionString;
        Vendor = vendor;
        Renderer = renderer;
        Profile = profile;
        Capabilities = capabilities;
        _ownsContextProvider = contextProvider.Ownership == OpenGLESContextOwnership.Owned;

        Backend = GraphicsBackend.OpenGLES;
        Adapter = new GraphicsAdapterInfo(
            default,
            renderer,
            GraphicsAdapterType.Unknown,
            0,
            0,
            0);
        Info = new GraphicsDeviceInfo(
            renderer,
            vendor,
            renderer,
            versionString,
            0,
            0,
            renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("swiftshader", StringComparison.OrdinalIgnoreCase));
    }

    public GraphicsBackend Backend { get; }

    public GraphicsAdapterInfo Adapter { get; }

    public GraphicsDeviceInfo Info { get; }

    public GraphicsDeviceCapabilities Capabilities { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public OpenGLESVersion Version { get; }

    public string VersionString { get; }

    public string Vendor { get; }

    public string Renderer { get; }

    public OpenGLESCapabilityProfile Profile { get; }

    internal GL Api => _gl;

    internal static OpenGLESGraphicsDevice Create(
        IOpenGLESContextProvider contextProvider,
        GraphicsDeviceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextProvider);
        cancellationToken.ThrowIfCancellationRequested();

        GL? gl = null;
        contextProvider.MakeCurrent();
        try
        {
            gl = GL.GetApi(contextProvider.GetProcAddress);
            string versionString = gl.GetStringS(StringName.Version);
            if (!OpenGLESVersion.TryParse(versionString, out OpenGLESVersion version))
                throw new GraphicsBackendUnavailableException(
                    GraphicsBackend.OpenGLES,
                    $"The supplied context did not report an OpenGL ES version (GL_VERSION='{versionString}').");

            OpenGLESCapabilityProfile profile = QueryProfile(gl, version, contextProvider.SupportsPresentation);
            profile.EnsureRequiredBaseline();

            string vendor = gl.GetStringS(StringName.Vendor);
            string renderer = gl.GetStringS(StringName.Renderer);
            GraphicsDeviceCapabilities capabilities = QueryCapabilities(gl, profile);

            cancellationToken.ThrowIfCancellationRequested();

            OpenGLESGraphicsDevice device = new(
                contextProvider, gl, version, versionString, vendor, renderer, profile, capabilities);
            gl = null;
            return device;
        }
        catch
        {
            gl?.Dispose();
            throw;
        }
        finally
        {
            contextProvider.ReleaseCurrent();
        }
    }

    private static OpenGLESCapabilityProfile QueryProfile(
        GL gl,
        OpenGLESVersion version,
        bool supportsPresentation)
    {
        int extensionCount = Math.Max(0, gl.GetInteger(GLEnum.NumExtensions));
        string[] extensions = new string[extensionCount];
        for (int i = 0; i < extensionCount; i++)
            extensions[i] = gl.GetStringS(StringName.Extensions, (uint)i);

        return OpenGLESCapabilityProfile.Create(version, extensions, supportsPresentation);
    }

    private static GraphicsDeviceCapabilities QueryCapabilities(GL gl, OpenGLESCapabilityProfile profile)
    {
        int maxTextureSize = Math.Max(0, gl.GetInteger(GLEnum.MaxTextureSize));
        int max3DTextureSize = Math.Max(0, gl.GetInteger(GLEnum.Max3DTextureSize));
        int maxArrayLayers = Math.Max(0, gl.GetInteger(GLEnum.MaxArrayTextureLayers));
        int maxVertexAttributes = Math.Max(0, gl.GetInteger(GLEnum.MaxVertexAttribs));
        int maxCombinedTextureUnits = Math.Max(0, gl.GetInteger(GLEnum.MaxCombinedTextureImageUnits));
        int maxUniformBindings = Math.Max(0, gl.GetInteger(GLEnum.MaxUniformBufferBindings));
        int maxStorageBindings = profile.Version.IsAtLeast(3, 1)
            ? Math.Max(0, gl.GetInteger(GLEnum.MaxShaderStorageBufferBindings))
            : 0;
        int maxComputeInvocations = profile.Version.IsAtLeast(3, 1)
            ? Math.Max(0, gl.GetInteger(GLEnum.MaxComputeWorkGroupInvocations))
            : 0;
        int maxComputeX = profile.Version.IsAtLeast(3, 1)
            ? Math.Max(0, gl.GetInteger(GLEnum.MaxComputeWorkGroupSize))
            : 0;

        GraphicsDeviceLimits limits = new(
            MaxBufferSizeInBytes: 0,
            MaxTextureDimension1D: 0,
            MaxTextureDimension2D: (uint)maxTextureSize,
            MaxTextureDimension3D: (uint)max3DTextureSize,
            MaxTextureArrayLayers: (uint)maxArrayLayers,
            MaxVertexInputAttributes: (uint)maxVertexAttributes,
            MaxVertexInputBindings: (uint)maxVertexAttributes,
            MaxBoundDescriptorSets: (uint)Math.Min(maxUniformBindings, maxStorageBindings),
            MaxComputeWorkGroupInvocations: (uint)maxComputeInvocations,
            MaxComputeWorkGroupSizeX: (uint)maxComputeX,
            MaxComputeWorkGroupSizeY: 0,
            MaxComputeWorkGroupSizeZ: 0,
            MaxSamplerAnisotropy: profile.Features.Supports(GraphicsFeature.SamplerAnisotropy)
                ? Math.Max(1, gl.GetFloat(GLEnum.MaxTextureMaxAnisotropy))
                : 1);

        GraphicsFormatSupport colorSupport =
            GraphicsFormatSupport.Sampled |
            GraphicsFormatSupport.ColorAttachment |
            GraphicsFormatSupport.Blend |
            GraphicsFormatSupport.VertexBuffer |
            GraphicsFormatSupport.CopySource |
            GraphicsFormatSupport.CopyDestination |
            GraphicsFormatSupport.LinearFiltering;

        GraphicsFormatSupport normalizedSingleSupport =
            GraphicsFormatSupport.Sampled |
            GraphicsFormatSupport.VertexBuffer |
            GraphicsFormatSupport.CopySource |
            GraphicsFormatSupport.CopyDestination |
            GraphicsFormatSupport.LinearFiltering;

        GraphicsFormatSupport depthSupport =
            GraphicsFormatSupport.Sampled |
            GraphicsFormatSupport.DepthStencilAttachment |
            GraphicsFormatSupport.CopySource |
            GraphicsFormatSupport.CopyDestination;

        GraphicsFormatCapabilities[] formats =
        [
            new(GraphicsFormat.R8UNorm, normalizedSingleSupport, GraphicsSampleCountFlags.One),
            new(GraphicsFormat.RG8UNorm, normalizedSingleSupport, GraphicsSampleCountFlags.One),
            new(GraphicsFormat.RGBA8UNorm, colorSupport | GraphicsFormatSupport.Storage, GraphicsSampleCountFlags.One),
            new(GraphicsFormat.RGBA8UNormSrgb,
                GraphicsFormatSupport.Sampled | GraphicsFormatSupport.ColorAttachment |
                GraphicsFormatSupport.Blend | GraphicsFormatSupport.CopySource |
                GraphicsFormatSupport.CopyDestination | GraphicsFormatSupport.LinearFiltering,
                GraphicsSampleCountFlags.One),
            new(GraphicsFormat.D16UNorm, depthSupport, GraphicsSampleCountFlags.One),
            new(GraphicsFormat.D24UNormS8UInt, depthSupport, GraphicsSampleCountFlags.One)
        ];

        // GLES has one ordered command stream. Logical roles share that stream rather than claiming
        // independent hardware queues.
        return new GraphicsDeviceCapabilities(
            profile.Features,
            profile.QueueCapabilities,
            limits,
            formats);
    }

    public IGraphicsCommandQueue GetQueue(GraphicsQueueType queueType, uint index = 0)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!Enum.IsDefined(queueType))
            throw new ArgumentOutOfRangeException(nameof(queueType));
        if (index != 0)
            throw new GraphicsValidationException("The OpenGL ES backend exposes one logical queue per role.");
        if (queueType == GraphicsQueueType.Compute &&
            !Capabilities.QueueCapabilities.HasFlag(GraphicsQueueCapabilities.Compute))
            throw new GraphicsFeatureNotSupportedException(
                GraphicsFeature.ComputeShaders, GraphicsFeature.ComputeShaders);

        lock (_queueGate)
            return _queues[(int)queueType] ??= new OpenGLESGraphicsCommandQueue(this, queueType);
    }

    public IGraphicsQueue GetPresentationQueue(uint index = 0)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (index != 0)
            throw new GraphicsValidationException("The OpenGL ES backend exposes one presentation queue candidate.");
        if (!_contextProvider.SupportsPresentation)
            throw new GraphicsBackendUnavailableException(
                Backend, "The host-provided GLES context is not associated with a presentation drawable.");
        return GetQueue(GraphicsQueueType.Graphics);
    }

    public IGraphicsBuffer CreateBuffer(in GraphicsBufferDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        GraphicsBufferDescription copy = description;
        Capabilities.ValidateBufferDescription(copy);
        return WithContext(gl => OpenGLESGraphicsBuffer.CreateUnderContext(this, gl, copy));
    }

    public IGraphicsTexture CreateTexture(in GraphicsTextureDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        GraphicsTextureDescription copy = description;
        Capabilities.ValidateTextureDescription(copy);
        return WithContext(gl => OpenGLESGraphicsTexture.CreateUnderContext(this, gl, copy));
    }

    public IGraphicsSampler CreateSampler(in GraphicsSamplerDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        GraphicsSamplerDescription copy = description;
        Capabilities.ValidateSamplerDescription(copy);
        return WithContext(gl => OpenGLESGraphicsSampler.CreateUnderContext(this, gl, copy));
    }

    public IGraphicsShader CreateShader(in GraphicsShaderDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        GraphicsShaderDescription copy = description;
        return WithContext(gl => OpenGLESGraphicsShader.CreateUnderContext(this, gl, copy));
    }

    public IGraphicsBindingLayout CreateBindingLayout(GraphicsBindingLayoutDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return new OpenGLESGraphicsBindingLayout(this, description);
    }

    public IGraphicsPipelineLayout CreatePipelineLayout(ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return new OpenGLESGraphicsPipelineLayout(this, bindingLayouts);
    }

    public IGraphicsBindingSet CreateBindingSet(IGraphicsBindingLayout bindingLayout)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(bindingLayout);
        if (bindingLayout is not OpenGLESGraphicsBindingLayout glesLayout ||
            !ReferenceEquals(glesLayout.Device, this) || glesLayout.IsDisposed)
            throw new GraphicsValidationException("The binding layout must belong to this OpenGL ES device.");
        return new OpenGLESGraphicsBindingSet(glesLayout);
    }

    public IGraphicsRenderPipeline CreateRenderPipeline(GraphicsRenderPipelineDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return OpenGLESGraphicsRenderPipeline.Create(this, description);
    }

    public IGraphicsComputePipeline CreateComputePipeline(GraphicsComputePipelineDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return OpenGLESGraphicsComputePipeline.Create(this, description);
    }

    public IGraphicsFence CreateFence(ulong initialValue = 0)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return WithContext(gl =>
        {
            OpenGLESGraphicsFence fence = new(this, initialValue);
            RegisterUnderContext(fence);
            return (IGraphicsFence)fence;
        });
    }

    public IGraphicsPresentationSurface CreatePresentationSurface(IGraphicsPresentationTarget target)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(target);
        if (!_contextProvider.SupportsPresentation)
            throw new GraphicsBackendUnavailableException(
                Backend, "The host-provided GLES context is not associated with a presentation drawable.");

        GraphicsPresentationTargetSnapshot snapshot = target.GetSnapshot();
        snapshot.Validate();
        if (!_contextProvider.IsPresentationTargetCompatible(snapshot))
            throw new GraphicsValidationException(
                "The supplied presentation target is not compatible with the host-provided GLES drawable.");
        return new OpenGLESGraphicsPresentationSurface(this, target, snapshot);
    }

    internal T WithContext<T>(Func<GL, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_contextGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _contextProvider.MakeCurrent();
            try
            {
                return action(_gl);
            }
            finally
            {
                _contextProvider.ReleaseCurrent();
            }
        }
    }

    internal void WithContext(Action<GL> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_contextGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _contextProvider.MakeCurrent();
            try
            {
                action(_gl);
            }
            finally
            {
                _contextProvider.ReleaseCurrent();
            }
        }
    }

    internal void RegisterUnderContext(IOpenGLESNativeResource resource)
    {
        lock (_resourceGate)
        {
            if (!_resources.Add(resource))
                throw new InvalidOperationException("The GLES native resource was registered more than once.");
        }
    }

    internal void ReleaseResource(IOpenGLESNativeResource resource)
    {
        lock (_contextGate)
        {
            if (IsDisposed)
                return;

            _contextProvider.MakeCurrent();
            try
            {
                resource.ReleaseNative(_gl);
                lock (_resourceGate)
                    _resources.Remove(resource);
            }
            finally
            {
                _contextProvider.ReleaseCurrent();
            }
        }
    }

    internal IOpenGLESContextProvider ContextProvider => _contextProvider;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        lock (_contextGate)
        {
            try
            {
                _contextProvider.MakeCurrent();
                try
                {
                    IOpenGLESNativeResource[] resources;
                    lock (_resourceGate)
                    {
                        resources = new IOpenGLESNativeResource[_resources.Count];
                        _resources.CopyTo(resources);
                    }

                    for (int i = 0; i < resources.Length; i++)
                    {
                        try
                        {
                            resources[i].ReleaseNative(_gl);
                        }
                        catch (Exception exception)
                        {
                            failure ??= exception;
                        }
                    }

                    lock (_resourceGate)
                        _resources.Clear();
                }
                finally
                {
                    _contextProvider.ReleaseCurrent();
                }
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                _gl.Dispose();
            }
        }

        if (_ownsContextProvider)
        {
            try
            {
                _contextProvider.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
