using System.Collections.Concurrent;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Initial desktop OpenGL device implementation. Native work is serialized because one current
/// context cannot be concurrently driven by unrelated threads.
/// </summary>
internal sealed class OpenGLGraphicsDevice : IGraphicsDevice
{
    private readonly IOpenGLContextProvider _contextProvider;
    private readonly object _contextGate = new();
    private readonly ConcurrentBag<IOpenGLNativeResource> _resources = [];
    private readonly ConcurrentQueue<IOpenGLNativeResource> _deferredResources = new();
    private readonly object _queueGate = new();
    private readonly OpenGLGraphicsCommandQueue?[] _queues = new OpenGLGraphicsCommandQueue?[3];
    private int _pendingSubmissionCount;
    private readonly GL _gl;
    private readonly ArbDirectStateAccess _directStateAccess;
    private readonly ArbTextureView _textureViewApi;
    private readonly ArbSamplerObjects _samplerObjects;
    private readonly ArbInternalformatQuery _internalFormatQuery;
    private readonly ArbSync _syncApi;
    private readonly ArbCopyImage _copyImageApi;
    private readonly ArbViewportArray _viewportApi;
    private readonly ArbBaseInstance _baseInstanceApi;
    private readonly ArbDrawIndirect _drawIndirectApi;
    private readonly ArbMultiDrawIndirect _multiDrawIndirectApi;
    private readonly ArbShaderImageLoadStore _imageLoadStoreApi;
    private readonly ArbDrawBuffersBlend _drawBuffersBlendApi;
    private readonly string _renderer;
    internal uint MaxUniformBufferBindings { get; }
    internal uint MaxStorageBufferBindings { get; }
    internal uint MaxTextureUnits { get; }
    internal uint MaxImageUnits { get; }
    internal uint UniformBufferOffsetAlignment { get; }
    internal uint StorageBufferOffsetAlignment { get; }
    private int _disposeState;

    private OpenGLGraphicsDevice(
        IOpenGLContextProvider contextProvider,
        GL gl,
        ArbDirectStateAccess directStateAccess,
        ArbTextureView textureViewApi,
        ArbSamplerObjects samplerObjects,
        ArbInternalformatQuery internalFormatQuery,
        ArbSync syncApi,
        ArbCopyImage copyImageApi,
        ArbViewportArray viewportApi,
        ArbBaseInstance baseInstanceApi,
        ArbDrawIndirect drawIndirectApi,
        ArbMultiDrawIndirect multiDrawIndirectApi,
        ArbShaderImageLoadStore imageLoadStoreApi,
        ArbDrawBuffersBlend drawBuffersBlendApi,
        uint maxUniformBufferBindings,
        uint maxStorageBufferBindings,
        uint maxTextureUnits,
        uint maxImageUnits,
        uint uniformBufferOffsetAlignment,
        uint storageBufferOffsetAlignment,
        int major,
        int minor,
        string vendor,
        string renderer,
        string version,
        GraphicsDeviceCapabilities capabilities)
    {
        _contextProvider = contextProvider;
        _gl = gl;
        _directStateAccess = directStateAccess;
        _textureViewApi = textureViewApi;
        _samplerObjects = samplerObjects;
        _internalFormatQuery = internalFormatQuery;
        _syncApi = syncApi;
        _copyImageApi = copyImageApi;
        _viewportApi = viewportApi;
        _baseInstanceApi = baseInstanceApi;
        _drawIndirectApi = drawIndirectApi;
        _multiDrawIndirectApi = multiDrawIndirectApi;
        _imageLoadStoreApi = imageLoadStoreApi;
        _drawBuffersBlendApi = drawBuffersBlendApi;
        _renderer = renderer;
        MaxUniformBufferBindings = maxUniformBufferBindings;
        MaxStorageBufferBindings = maxStorageBufferBindings;
        MaxTextureUnits = maxTextureUnits;
        MaxImageUnits = maxImageUnits;
        UniformBufferOffsetAlignment = Math.Max(1u, uniformBufferOffsetAlignment);
        StorageBufferOffsetAlignment = Math.Max(1u, storageBufferOffsetAlignment);
        Capabilities = capabilities;
        Backend = GraphicsBackend.OpenGL;
        Adapter = new GraphicsAdapterInfo(default, renderer, GraphicsAdapterType.Unknown, 0, 0, 0);
        Info = new GraphicsDeviceInfo(renderer, vendor, version, $"{major}.{minor} Core", 0, 0, false);
    }

    public GraphicsBackend Backend { get; }
    public GraphicsAdapterInfo Adapter { get; }
    public GraphicsDeviceInfo Info { get; }
    public GraphicsDeviceCapabilities Capabilities { get; }
    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    internal static OpenGLGraphicsDevice Create(
        IOpenGLContextProvider contextProvider,
        GraphicsDeviceOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GL? gl = null;
        ArbDirectStateAccess? directStateAccess = null;
        ArbTextureView? textureViewApi = null;
        ArbSamplerObjects? samplerObjects = null;
        ArbInternalformatQuery? internalFormatQuery = null;
        ArbSync? syncApi = null;
        ArbCopyImage? copyImageApi = null;
        ArbViewportArray? viewportApi = null;
        ArbBaseInstance? baseInstanceApi = null;
        ArbDrawIndirect? drawIndirectApi = null;
        ArbMultiDrawIndirect? multiDrawIndirectApi = null;
        ArbShaderImageLoadStore? imageLoadStoreApi = null;
        ArbDrawBuffersBlend? drawBuffersBlendApi = null;
        if (contextProvider.StateOwnership != OpenGLContextStateOwnership.Exclusive)
            throw new GraphicsBackendUnavailableException(
                GraphicsBackend.OpenGL,
                "The initial OpenGL backend requires exclusive ownership of context state; shared host-state preservation is not implemented.");

        contextProvider.MakeCurrent();
        try
        {
            gl = GL.GetApi(contextProvider.GetProcAddress);

            int major = gl.GetInteger(GLEnum.MajorVersion);
            int minor = gl.GetInteger(GLEnum.MinorVersion);
            int profile = gl.GetInteger(GLEnum.ContextProfileMask);
            if (major < 4 || (major == 4 && minor < 5))
                throw new GraphicsBackendUnavailableException(
                    GraphicsBackend.OpenGL,
                    $"OpenGL 4.5 is required; the current context reports {major}.{minor}.");
            if ((profile & (int)ContextProfileMask.CoreProfileBit) == 0)
                throw new GraphicsBackendUnavailableException(
                    GraphicsBackend.OpenGL,
                    "A desktop OpenGL Core Profile context is required.");

            // These Silk.NET wrappers load entry points promoted into the required desktop 4.5
            // core version. Construct them only after validation, with this context current.
            directStateAccess = new ArbDirectStateAccess(gl.Context);
            textureViewApi = new ArbTextureView(gl.Context);
            samplerObjects = new ArbSamplerObjects(gl.Context);
            internalFormatQuery = new ArbInternalformatQuery(gl.Context);
            syncApi = new ArbSync(gl.Context);
            copyImageApi = new ArbCopyImage(gl.Context);
            viewportApi = new ArbViewportArray(gl.Context);
            baseInstanceApi = new ArbBaseInstance(gl.Context);
            drawIndirectApi = new ArbDrawIndirect(gl.Context);
            multiDrawIndirectApi = new ArbMultiDrawIndirect(gl.Context);
            imageLoadStoreApi = new ArbShaderImageLoadStore(gl.Context);
            drawBuffersBlendApi = new ArbDrawBuffersBlend(gl.Context);

            string vendor = gl.GetStringS(StringName.Vendor);
            string renderer = gl.GetStringS(StringName.Renderer);
            string version = gl.GetStringS(StringName.Version);
            GraphicsDeviceCapabilities capabilities = QueryCapabilities(
                gl, major, minor, internalFormatQuery, contextProvider.SupportsPresentation);
            uint maxUniformBufferBindings = ToUInt(gl.GetInteger(GLEnum.MaxUniformBufferBindings));
            uint maxStorageBufferBindings = ToUInt(gl.GetInteger(GLEnum.MaxShaderStorageBufferBindings));
            uint maxTextureUnits = ToUInt(gl.GetInteger(GLEnum.MaxCombinedTextureImageUnits));
            uint maxImageUnits = ToUInt(gl.GetInteger(GLEnum.MaxImageUnits));
            uint uniformBufferOffsetAlignment = ToUInt(gl.GetInteger(GLEnum.UniformBufferOffsetAlignment));
            uint storageBufferOffsetAlignment = ToUInt(gl.GetInteger(GLEnum.ShaderStorageBufferOffsetAlignment));
            cancellationToken.ThrowIfCancellationRequested();

            OpenGLGraphicsDevice device = new(
                contextProvider, gl, directStateAccess, textureViewApi, samplerObjects,
                internalFormatQuery, syncApi, copyImageApi, viewportApi,
                baseInstanceApi, drawIndirectApi, multiDrawIndirectApi, imageLoadStoreApi,
                drawBuffersBlendApi, maxUniformBufferBindings, maxStorageBufferBindings, maxTextureUnits, maxImageUnits,
                uniformBufferOffsetAlignment, storageBufferOffsetAlignment,
                major, minor, vendor, renderer, version, capabilities);
            gl = null;
            directStateAccess = null;
            textureViewApi = null;
            samplerObjects = null;
            internalFormatQuery = null;
            syncApi = null;
            copyImageApi = null;
            viewportApi = null;
            baseInstanceApi = null;
            drawIndirectApi = null;
            multiDrawIndirectApi = null;
            imageLoadStoreApi = null;
            drawBuffersBlendApi = null;
            return device;
        }
        catch
        {
            drawBuffersBlendApi?.Dispose();
            imageLoadStoreApi?.Dispose();
            multiDrawIndirectApi?.Dispose();
            drawIndirectApi?.Dispose();
            baseInstanceApi?.Dispose();
            viewportApi?.Dispose();
            copyImageApi?.Dispose();
            syncApi?.Dispose();
            internalFormatQuery?.Dispose();
            samplerObjects?.Dispose();
            textureViewApi?.Dispose();
            directStateAccess?.Dispose();
            gl?.Dispose();
            throw;
        }
        finally
        {
            contextProvider.ReleaseCurrent();
        }
    }

    private static GraphicsDeviceCapabilities QueryCapabilities(
        GL gl,
        int major,
        int minor,
        ArbInternalformatQuery internalFormatQuery,
        bool supportsPresentation)
    {
        static bool AtLeast(int major, int minor, int requiredMajor, int requiredMinor) =>
            major > requiredMajor || (major == requiredMajor && minor >= requiredMinor);

        bool hasCompute = AtLeast(major, minor, 4, 3);
        bool hasTessellation = AtLeast(major, minor, 4, 0);
        bool hasGeometry = AtLeast(major, minor, 3, 2);
        bool hasAnisotropy = gl.IsExtensionPresent("GL_EXT_texture_filter_anisotropic");

        GraphicsFeature features = GraphicsFeature.None;
        if (hasCompute) features |= GraphicsFeature.ComputeShaders;
        if (AtLeast(major, minor, 4, 0)) features |= GraphicsFeature.IndirectDrawing;
        if (AtLeast(major, minor, 4, 3)) features |= GraphicsFeature.MultiDrawIndirect;
        if (hasTessellation) features |= GraphicsFeature.TessellationShaders;
        if (hasGeometry) features |= GraphicsFeature.GeometryShaders;
        if (hasAnisotropy) features |= GraphicsFeature.SamplerAnisotropy;
        // Timestamp queries, sparse resources, mesh/task shaders, depth bounds, and multi-viewport
        // are intentionally not advertised until the corresponding execution paths are implemented.

        features |= GraphicsFeature.LogicOperations | GraphicsFeature.FillModeNonSolid;
        if (AtLeast(major, minor, 3, 2))
            features |= GraphicsFeature.DepthClamp;

        uint maxTexture = ToUInt(gl.GetInteger(GLEnum.MaxTextureSize));
        uint maxTexture3D = ToUInt(gl.GetInteger(GLEnum.Max3DTextureSize));
        uint maxArrayLayers = ToUInt(gl.GetInteger(GLEnum.MaxArrayTextureLayers));
        uint maxVertexAttributes = ToUInt(gl.GetInteger(GLEnum.MaxVertexAttribs));
        uint maxVertexBindings = ToUInt(gl.GetInteger(GLEnum.MaxVertexAttribBindings));
        uint maxUniformBindings = ToUInt(gl.GetInteger(GLEnum.MaxUniformBufferBindings));
        uint maxComputeInvocations = hasCompute
            ? ToUInt(gl.GetInteger(GLEnum.MaxComputeWorkGroupInvocations))
            : 0;
        uint maxComputeX = 0;
        uint maxComputeY = 0;
        uint maxComputeZ = 0;
        uint maxPatchSize = hasTessellation ? ToUInt(gl.GetInteger(GLEnum.MaxPatchVertices)) : 0;
        float maxAnisotropy = 0;
        if (hasAnisotropy)
            gl.GetFloat(GLEnum.MaxTextureMaxAnisotropy, out maxAnisotropy);

        GraphicsDeviceLimits limits = new(
            0, maxTexture, maxTexture, maxTexture3D, maxArrayLayers,
            maxVertexAttributes, maxVertexBindings, 0,
            maxComputeInvocations, maxComputeX, maxComputeY, maxComputeZ,
            maxAnisotropy, maxPatchSize);

        bool hasInternalFormatQuery2 = AtLeast(major, minor, 4, 3) ||
                                       gl.IsExtensionPresent("GL_ARB_internalformat_query2");
        GraphicsFormatCapabilities[] formats = OpenGLFormatCapabilities.Query(
            gl, internalFormatQuery, hasInternalFormatQuery2);

        // Compute shaders are available at the API level, but the command-list dispatch path is
        // not implemented yet; do not advertise a compute-capable queue until it is executable.
        GraphicsQueueCapabilities queues = GraphicsQueueCapabilities.Graphics |
                                            GraphicsQueueCapabilities.Copy;
        if (supportsPresentation)
            queues |= GraphicsQueueCapabilities.Present;

        return new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(features),
            queues,
            limits,
            formats);
    }

    private static uint ToUInt(int value) => value > 0 ? (uint)value : 0;

    internal ArbDirectStateAccess DirectStateAccess => _directStateAccess;
    internal ArbTextureView TextureViewApi => _textureViewApi;
    internal ArbSamplerObjects SamplerObjects => _samplerObjects;
    internal ArbSync SyncApi => _syncApi;
    internal ArbCopyImage CopyImageApi => _copyImageApi;
    internal ArbViewportArray ViewportApi => _viewportApi;
    internal ArbBaseInstance BaseInstanceApi => _baseInstanceApi;
    internal ArbDrawIndirect DrawIndirectApi => _drawIndirectApi;
    internal ArbMultiDrawIndirect MultiDrawIndirectApi => _multiDrawIndirectApi;
    internal ArbShaderImageLoadStore ImageLoadStoreApi => _imageLoadStoreApi;
    internal ArbDrawBuffersBlend DrawBuffersBlendApi => _drawBuffersBlendApi;

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

    internal void WithContext(Action<GL> action) => WithContext(gl =>
    {
        action(gl);
        return true;
    });

    /// <summary>Registers a resource from inside a WithContext callback, while the context gate is held.</summary>
    internal void RegisterUnderContext(IOpenGLNativeResource resource) => _resources.Add(resource);

    /// <summary>
    /// Releases a resource under the same context serialization used for commands. If device disposal
    /// already owns the gate, it has responsibility for draining the registered resource set.
    /// </summary>
    internal void ReleaseResource(IOpenGLNativeResource resource)
    {
        lock (_contextGate)
        {
            if (IsDisposed)
                return;

            if (Volatile.Read(ref _pendingSubmissionCount) != 0)
            {
                _deferredResources.Enqueue(resource);
                return;
            }

            _contextProvider.MakeCurrent();
            try
            {
                resource.ReleaseNative(_gl);
            }
            finally
            {
                _contextProvider.ReleaseCurrent();
            }
        }
    }

    internal void OnSubmissionQueued() => Interlocked.Increment(ref _pendingSubmissionCount);

    internal void OnSubmissionCompleted()
    {
        int remaining = Interlocked.Decrement(ref _pendingSubmissionCount);
        if (remaining < 0)
            throw new InvalidOperationException("OpenGL submission lifetime accounting became unbalanced.");

        if (remaining != 0)
            return;

        while (_deferredResources.TryDequeue(out IOpenGLNativeResource? resource))
            resource.ReleaseNative(_gl);
    }

    public IGraphicsCommandQueue GetQueue(GraphicsQueueType queueType, uint index = 0)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (index != 0)
            throw new GraphicsValidationException("The OpenGL backend exposes one logical queue per queue role.");
        if (!Enum.IsDefined(queueType))
            throw new ArgumentOutOfRangeException(nameof(queueType));
        if (queueType == GraphicsQueueType.Compute)
            throw new GraphicsException(
                "Compute shaders are available, but OpenGL compute command recording and dispatch are not implemented yet.");

        lock (_queueGate)
        {
            return _queues[(int)queueType] ??= new OpenGLGraphicsCommandQueue(this, queueType);
        }
    }

    public IGraphicsQueue GetPresentationQueue(uint index = 0)
    {
        if (index != 0)
            throw new GraphicsValidationException("The OpenGL backend exposes one presentation-queue candidate.");
        if (!_contextProvider.SupportsPresentation)
            throw new GraphicsBackendUnavailableException(
                GraphicsBackend.OpenGL, "The injected OpenGL context provider has no presentation drawable.");
        return GetQueue(GraphicsQueueType.Graphics, 0);
    }

    public unsafe IGraphicsBuffer CreateBuffer(in GraphicsBufferDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        GraphicsBufferDescription bufferDescription = description;
        Capabilities.ValidateBufferDescription(bufferDescription);
        ulong sizeInBytes = bufferDescription.SizeInBytes;
        if (sizeInBytes > (ulong)nuint.MaxValue)
            throw new GraphicsValidationException("The requested buffer is larger than the native addressable size.");

        return WithContext(gl =>
        {
            int previousBinding = gl.GetInteger(GLEnum.ArrayBufferBinding);
            uint buffer = gl.GenBuffer();
            bool registered = false;
            try
            {
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)sizeInBytes, null, BufferUsageARB.DynamicDraw);
                OpenGLGraphicsBuffer resource = new(this, bufferDescription, buffer);
                RegisterUnderContext(resource);
                registered = true;
                return (IGraphicsBuffer)resource;
            }
            catch
            {
                if (!registered)
                    gl.DeleteBuffer(buffer);
                throw;
            }
            finally
            {
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, unchecked((uint)previousBinding));
            }
        });
    }

    public IGraphicsShader CreateShader(in GraphicsShaderDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return OpenGLGraphicsShader.Create(this, description);
    }

    public IGraphicsTexture CreateTexture(in GraphicsTextureDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        GraphicsTextureDescription textureDescription = description;
        Capabilities.ValidateTextureDescription(textureDescription);
        if ((textureDescription.CreationOptions & GraphicsResourceCreationOptions.AllowAliasing) != 0)
            throw new GraphicsValidationException("OpenGL does not expose resource aliasing through this backend.");

        return WithContext(gl => OpenGLGraphicsTexture.CreateUnderContext(
            this, gl, _directStateAccess, textureDescription));
    }

    public IGraphicsSampler CreateSampler(in GraphicsSamplerDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        GraphicsSamplerDescription samplerDescription = description;
        Capabilities.ValidateSamplerDescription(samplerDescription);

        return WithContext(gl => OpenGLGraphicsSampler.CreateUnderContext(
            this, _directStateAccess, _samplerObjects, samplerDescription));
    }

    public IGraphicsBindingLayout CreateBindingLayout(GraphicsBindingLayoutDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return new OpenGLGraphicsBindingLayout(this, description);
    }

    public IGraphicsPipelineLayout CreatePipelineLayout(ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return new OpenGLGraphicsPipelineLayout(this, bindingLayouts);
    }

    public IGraphicsBindingSet CreateBindingSet(IGraphicsBindingLayout bindingLayout)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(bindingLayout);
        if (bindingLayout is not OpenGLGraphicsBindingLayout openGlLayout ||
            !ReferenceEquals(openGlLayout.Device, this))
            throw new GraphicsValidationException("The binding layout must be created by this OpenGL device.");
        return new OpenGLGraphicsBindingSet(openGlLayout);
    }

    public IGraphicsRenderPipeline CreateRenderPipeline(GraphicsRenderPipelineDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return OpenGLGraphicsRenderPipeline.Create(this, description);
    }

    public IGraphicsComputePipeline CreateComputePipeline(GraphicsComputePipelineDescription description) =>
        throw new NotSupportedException("Compute pipeline creation is not implemented yet.");

    public IGraphicsFence CreateFence(ulong initialValue = 0)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return WithContext(gl =>
        {
            OpenGLGraphicsFence fence = new(this, _syncApi, initialValue);
            RegisterUnderContext(fence);
            return (IGraphicsFence)fence;
        });
    }

    public IGraphicsPresentationSurface CreatePresentationSurface(IGraphicsPresentationTarget target) =>
        throw new NotSupportedException("Presentation surfaces and swapchains are not implemented yet.");

    public void Dispose()
    {
        lock (_contextGate)
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
                return;

            try
            {
                _contextProvider.MakeCurrent();
                try
                {
                    _gl.Finish();
                    foreach (OpenGLGraphicsCommandQueue? queue in _queues)
                        queue?.DrainUnderContext(_gl);
                    while (_deferredResources.TryDequeue(out IOpenGLNativeResource? deferred))
                        deferred.ReleaseNative(_gl);

                    foreach (IOpenGLNativeResource resource in _resources)
                        resource.ReleaseNative(_gl);

                    _drawBuffersBlendApi.Dispose();
                    _imageLoadStoreApi.Dispose();
                    _multiDrawIndirectApi.Dispose();
                    _drawIndirectApi.Dispose();
                    _baseInstanceApi.Dispose();
                    _viewportApi.Dispose();
                    _copyImageApi.Dispose();
                    _syncApi.Dispose();
                    _internalFormatQuery.Dispose();
                    _samplerObjects.Dispose();
                    _textureViewApi.Dispose();
                    _directStateAccess.Dispose();
                    _gl.Dispose();
                }
                finally
                {
                    _contextProvider.ReleaseCurrent();
                }
            }
            finally
            {
                if (_contextProvider.Ownership == OpenGLContextOwnership.Owned)
                    _contextProvider.Dispose();
            }
        }
    }
}
