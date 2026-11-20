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
    private readonly GL _gl;
    private readonly ArbDirectStateAccess _directStateAccess;
    private readonly ArbTextureView _textureViewApi;
    private readonly ArbSamplerObjects _samplerObjects;
    private readonly ArbInternalformatQuery _internalFormatQuery;
    private readonly string _renderer;
    private int _disposeState;

    private OpenGLGraphicsDevice(
        IOpenGLContextProvider contextProvider,
        GL gl,
        ArbDirectStateAccess directStateAccess,
        ArbTextureView textureViewApi,
        ArbSamplerObjects samplerObjects,
        ArbInternalformatQuery internalFormatQuery,
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
        _renderer = renderer;
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

            string vendor = gl.GetStringS(StringName.Vendor);
            string renderer = gl.GetStringS(StringName.Renderer);
            string version = gl.GetStringS(StringName.Version);
            GraphicsDeviceCapabilities capabilities = QueryCapabilities(gl, major, minor, internalFormatQuery);
            cancellationToken.ThrowIfCancellationRequested();

            OpenGLGraphicsDevice device = new(
                contextProvider, gl, directStateAccess, textureViewApi, samplerObjects,
                internalFormatQuery, major, minor, vendor, renderer, version, capabilities);
            gl = null;
            directStateAccess = null;
            textureViewApi = null;
            samplerObjects = null;
            internalFormatQuery = null;
            return device;
        }
        catch
        {
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
        ArbInternalformatQuery internalFormatQuery)
    {
        static bool AtLeast(int major, int minor, int requiredMajor, int requiredMinor) =>
            major > requiredMajor || (major == requiredMajor && minor >= requiredMinor);

        bool hasCompute = AtLeast(major, minor, 4, 3);
        bool hasTessellation = AtLeast(major, minor, 4, 0);
        bool hasGeometry = AtLeast(major, minor, 3, 2);
        bool hasTimerQuery = AtLeast(major, minor, 3, 3) || gl.IsExtensionPresent("GL_ARB_timer_query");
        bool hasAnisotropy = gl.IsExtensionPresent("GL_EXT_texture_filter_anisotropic");
        bool hasSparseTextures = gl.IsExtensionPresent("GL_ARB_sparse_texture");
        bool hasMultiViewport = AtLeast(major, minor, 4, 1);
        bool hasDepthBounds = gl.IsExtensionPresent("GL_EXT_depth_bounds_test") ||
                              gl.IsExtensionPresent("GL_NV_depth_bounds_test");
        bool hasMeshShaders = gl.IsExtensionPresent("GL_NV_mesh_shader") ||
                              gl.IsExtensionPresent("GL_EXT_mesh_shader");

        GraphicsFeature features = GraphicsFeature.None;
        if (hasCompute) features |= GraphicsFeature.ComputeShaders;
        if (AtLeast(major, minor, 4, 0)) features |= GraphicsFeature.IndirectDrawing;
        if (AtLeast(major, minor, 4, 3)) features |= GraphicsFeature.MultiDrawIndirect;
        if (hasTessellation) features |= GraphicsFeature.TessellationShaders;
        if (hasGeometry) features |= GraphicsFeature.GeometryShaders;
        if (hasTimerQuery) features |= GraphicsFeature.TimestampQueries;
        if (hasAnisotropy) features |= GraphicsFeature.SamplerAnisotropy;
        if (hasSparseTextures) features |= GraphicsFeature.SparseResources;
        if (hasMultiViewport) features |= GraphicsFeature.MultiViewport;
        if (hasDepthBounds) features |= GraphicsFeature.DepthBoundsTest;
        if (hasMeshShaders)
        {
            features |= GraphicsFeature.MeshShaders;
            features |= GraphicsFeature.TaskShaders;
        }

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
            maxVertexAttributes, maxVertexBindings, maxUniformBindings,
            maxComputeInvocations, maxComputeX, maxComputeY, maxComputeZ,
            maxAnisotropy, maxPatchSize);

        bool hasInternalFormatQuery2 = AtLeast(major, minor, 4, 3) ||
                                       gl.IsExtensionPresent("GL_ARB_internalformat_query2");
        GraphicsFormatCapabilities[] formats = OpenGLFormatCapabilities.Query(
            gl, internalFormatQuery, hasInternalFormatQuery2);

        return new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(features),
            GraphicsQueueCapabilities.None,
            limits,
            formats);
    }

    private static uint ToUInt(int value) => value > 0 ? (uint)value : 0;

    internal ArbDirectStateAccess DirectStateAccess => _directStateAccess;
    internal ArbTextureView TextureViewApi => _textureViewApi;
    internal ArbSamplerObjects SamplerObjects => _samplerObjects;

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

    public IGraphicsCommandQueue GetQueue(GraphicsQueueType queueType, uint index = 0) =>
        throw new NotSupportedException("Command queues and command-list execution are not part of the initial OpenGL backend increment.");

    public IGraphicsQueue GetPresentationQueue(uint index = 0) =>
        throw new NotSupportedException("Presentation queues are not implemented yet.");

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

    public IGraphicsBindingLayout CreateBindingLayout(GraphicsBindingLayoutDescription description) =>
        throw new NotSupportedException("Descriptor binding layouts are not implemented yet.");

    public IGraphicsPipelineLayout CreatePipelineLayout(ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts) =>
        throw new NotSupportedException("Pipeline layouts are not implemented yet.");

    public IGraphicsBindingSet CreateBindingSet(IGraphicsBindingLayout bindingLayout) =>
        throw new NotSupportedException("Binding sets are not implemented yet.");

    public IGraphicsRenderPipeline CreateRenderPipeline(GraphicsRenderPipelineDescription description) =>
        throw new NotSupportedException("Render pipeline creation is not implemented yet.");

    public IGraphicsComputePipeline CreateComputePipeline(GraphicsComputePipelineDescription description) =>
        throw new NotSupportedException("Compute pipeline creation is not implemented yet.");

    public IGraphicsFence CreateFence(ulong initialValue = 0) =>
        throw new NotSupportedException("Fence synchronization is not implemented yet.");

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
                    foreach (IOpenGLNativeResource resource in _resources)
                        resource.ReleaseNative(_gl);

                    _gl.Finish();
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
