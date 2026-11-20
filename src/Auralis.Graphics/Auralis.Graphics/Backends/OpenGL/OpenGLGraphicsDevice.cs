using System.Collections.Concurrent;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGL;

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
    private readonly string _renderer;
    private int _disposeState;

    private OpenGLGraphicsDevice(
        IOpenGLContextProvider contextProvider,
        GL gl,
        int major,
        int minor,
        string vendor,
        string renderer,
        string version,
        GraphicsDeviceCapabilities capabilities)
    {
        _contextProvider = contextProvider;
        _gl = gl;
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

            string vendor = gl.GetStringS(StringName.Vendor);
            string renderer = gl.GetStringS(StringName.Renderer);
            string version = gl.GetStringS(StringName.Version);
            GraphicsDeviceCapabilities capabilities = QueryCapabilities(gl, major, minor);
            cancellationToken.ThrowIfCancellationRequested();

            OpenGLGraphicsDevice device = new(
                contextProvider, gl, major, minor, vendor, renderer, version, capabilities);
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

    private static GraphicsDeviceCapabilities QueryCapabilities(GL gl, int major, int minor)
    {
        static bool AtLeast(int major, int minor, int requiredMajor, int requiredMinor) =>
            major > requiredMajor || (major == requiredMajor && minor >= requiredMinor);

        GraphicsFeature features = GraphicsFeature.None;
        if (AtLeast(major, minor, 4, 3)) features |= GraphicsFeature.ComputeShaders;
        if (AtLeast(major, minor, 4, 0)) features |= GraphicsFeature.IndirectDrawing | GraphicsFeature.TessellationShaders;
        if (AtLeast(major, minor, 4, 3)) features |= GraphicsFeature.MultiDrawIndirect;
        if (AtLeast(major, minor, 3, 2)) features |= GraphicsFeature.GeometryShaders | GraphicsFeature.DepthClamp;
        if (AtLeast(major, minor, 1, 0)) features |= GraphicsFeature.LogicOperations | GraphicsFeature.FillModeNonSolid;

        uint maxTexture = ToUInt(gl.GetInteger(GLEnum.MaxTextureSize));
        uint maxTexture3D = ToUInt(gl.GetInteger(GLEnum.Max3DTextureSize));
        uint maxArrayLayers = ToUInt(gl.GetInteger(GLEnum.MaxArrayTextureLayers));
        uint maxVertexAttributes = ToUInt(gl.GetInteger(GLEnum.MaxVertexAttribs));
        uint maxUniformBindings = ToUInt(gl.GetInteger(GLEnum.MaxUniformBufferBindings));
        uint maxComputeInvocations = AtLeast(major, minor, 4, 3)
            ? ToUInt(gl.GetInteger(GLEnum.MaxComputeWorkGroupInvocations))
            : 0;

        GraphicsDeviceLimits limits = new(
            0, maxTexture, maxTexture, maxTexture3D, maxArrayLayers,
            maxVertexAttributes, 0, maxUniformBindings,
            maxComputeInvocations, 0, 0, 0, 0);

        // Format support is deliberately empty until internal-format operations are queried per format.
        return new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(features),
            GraphicsQueueCapabilities.None,
            limits,
            ReadOnlySpan<GraphicsFormatCapabilities>.Empty);
    }

    private static uint ToUInt(int value) => value > 0 ? (uint)value : 0;

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

    internal void Register(IOpenGLNativeResource resource) => _resources.Add(resource);

    public IGraphicsCommandQueue GetQueue(GraphicsQueueType queueType, uint index = 0) =>
        throw new GraphicsBackendUnavailableException(Backend,
            "Command queues and command-list execution are not part of the initial OpenGL backend increment.");

    public IGraphicsQueue GetPresentationQueue(uint index = 0) =>
        throw new GraphicsBackendUnavailableException(Backend,
            "Presentation queues are not implemented yet.");

    public IGraphicsBuffer CreateBuffer(in GraphicsBufferDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Capabilities.ValidateBufferDescription(description);
        if (description.SizeInBytes > (ulong)nuint.MaxValue)
            throw new GraphicsValidationException("The requested buffer is larger than the native addressable size.");
        if (description.MemoryAccess is GraphicsMemoryAccess.GpuToCpu or GraphicsMemoryAccess.CpuReadWrite)
            throw new GraphicsValidationException("GPU-to-CPU buffer reads are not enabled in this initial backend increment.");

        uint handle = WithContext(gl =>
        {
            uint buffer = gl.GenBuffer();
            try
            {
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)description.SizeInBytes, null, BufferUsageARB.DynamicDraw);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
                return buffer;
            }
            catch
            {
                gl.DeleteBuffer(buffer);
                throw;
            }
        });

        OpenGLGraphicsBuffer resource = new(this, description, handle);
        Register(resource);
        return resource;
    }

    public IGraphicsShader CreateShader(in GraphicsShaderDescription description)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        OpenGLGraphicsShader resource = OpenGLGraphicsShader.Create(this, description);
        Register(resource);
        return resource;
    }

    public IGraphicsTexture CreateTexture(in GraphicsTextureDescription description) =>
        throw new GraphicsBackendUnavailableException(Backend, "Texture allocation and format capability discovery are not implemented yet.");

    public IGraphicsSampler CreateSampler(in GraphicsSamplerDescription description) =>
        throw new GraphicsBackendUnavailableException(Backend, "Sampler creation is not implemented yet.");

    public IGraphicsBindingLayout CreateBindingLayout(GraphicsBindingLayoutDescription description) =>
        throw new GraphicsBackendUnavailableException(Backend, "Descriptor binding layouts are not implemented yet.");

    public IGraphicsPipelineLayout CreatePipelineLayout(ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts) =>
        throw new GraphicsBackendUnavailableException(Backend, "Pipeline layouts are not implemented yet.");

    public IGraphicsBindingSet CreateBindingSet(IGraphicsBindingLayout bindingLayout) =>
        throw new GraphicsBackendUnavailableException(Backend, "Binding sets are not implemented yet.");

    public IGraphicsRenderPipeline CreateRenderPipeline(GraphicsRenderPipelineDescription description) =>
        throw new GraphicsBackendUnavailableException(Backend, "Render pipeline creation is not implemented yet.");

    public IGraphicsComputePipeline CreateComputePipeline(GraphicsComputePipelineDescription description) =>
        throw new GraphicsBackendUnavailableException(Backend, "Compute pipeline creation is not implemented yet.");

    public IGraphicsFence CreateFence(ulong initialValue = 0) =>
        throw new GraphicsBackendUnavailableException(Backend, "Fence synchronization is not implemented yet.");

    public IGraphicsPresentationSurface CreatePresentationSurface(IGraphicsPresentationTarget target) =>
        throw new GraphicsBackendUnavailableException(Backend, "Presentation surfaces and swapchains are not implemented yet.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        lock (_contextGate)
        {
            _contextProvider.MakeCurrent();
            try
            {
                foreach (IOpenGLNativeResource resource in _resources)
                    resource.ReleaseNative(_gl);
                _gl.Finish();
                _gl.Dispose();
            }
            finally
            {
                _contextProvider.ReleaseCurrent();
            }
        }

        if (_contextProvider.Ownership == OpenGLContextOwnership.Owned)
            _contextProvider.Dispose();
    }
}
