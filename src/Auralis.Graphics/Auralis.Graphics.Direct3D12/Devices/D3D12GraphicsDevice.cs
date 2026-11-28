using System.Runtime.Versioning;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using TerraFX.Interop.DirectX;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>
/// Owns the native D3D12 logical device and exposes its immutable capability snapshot.
/// Command queues, resources, pipelines and presentation are introduced by their own implementation tasks.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class D3D12GraphicsDevice : IGraphicsDevice, IGraphicsDeviceHealth
{
    private readonly object _healthLock = new();
    private ID3D12Device* _nativeDevice;
    private int _status;
    private string? _failureReason;
    private Action<GraphicsDeviceStatus>? _statusChanged;

    internal D3D12GraphicsDevice(
        ID3D12Device* nativeDevice,
        GraphicsAdapterInfo adapter,
        GraphicsDeviceInfo info,
        GraphicsDeviceCapabilities capabilities)
    {
        if (nativeDevice == null)
            throw new ArgumentNullException(nameof(nativeDevice));

        _nativeDevice = nativeDevice;
        Adapter = adapter;
        Info = info;
        Capabilities = capabilities;
        _status = (int)GraphicsDeviceStatus.Operational;
    }

    public GraphicsBackend Backend => GraphicsBackend.Direct3D12;
    public GraphicsAdapterInfo Adapter { get; }
    public GraphicsDeviceInfo Info { get; }
    public GraphicsDeviceCapabilities Capabilities { get; }

    public bool IsDisposed =>
        (GraphicsDeviceStatus)Volatile.Read(ref _status) == GraphicsDeviceStatus.Disposed;

    public GraphicsDeviceStatus Status
    {
        get
        {
            PollDeviceHealth();
            return (GraphicsDeviceStatus)Volatile.Read(ref _status);
        }
    }

    public string? FailureReason
    {
        get
        {
            PollDeviceHealth();
            return Volatile.Read(ref _failureReason);
        }
    }

    public event Action<GraphicsDeviceStatus>? StatusChanged
    {
        add
        {
            lock (_healthLock)
                _statusChanged += value;
        }
        remove
        {
            lock (_healthLock)
                _statusChanged -= value;
        }
    }

    public IGraphicsCommandQueue GetQueue(GraphicsQueueType queueType, uint index = 0)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-04", "command queues and command lists");
    }

    public IGraphicsQueue GetPresentationQueue(uint index = 0)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-04", "command queues");
    }

    public IGraphicsBuffer CreateBuffer(in GraphicsBufferDescription description)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-06", "GPU buffers");
    }

    public IGraphicsTexture CreateTexture(in GraphicsTextureDescription description)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-06", "GPU textures");
    }

    public IGraphicsSampler CreateSampler(in GraphicsSamplerDescription description)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-06", "samplers");
    }

    public IGraphicsShader CreateShader(in GraphicsShaderDescription description)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-06", "shader objects");
    }

    public IGraphicsBindingLayout CreateBindingLayout(GraphicsBindingLayoutDescription description)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-09", "descriptor binding layouts");
    }

    public IGraphicsPipelineLayout CreatePipelineLayout(ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-09", "pipeline layouts");
    }

    public IGraphicsBindingSet CreateBindingSet(IGraphicsBindingLayout bindingLayout)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-09", "descriptor binding sets");
    }

    public IGraphicsRenderPipeline CreateRenderPipeline(GraphicsRenderPipelineDescription description)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-10", "graphics pipeline state objects");
    }

    public IGraphicsComputePipeline CreateComputePipeline(GraphicsComputePipelineDescription description)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-10", "compute pipeline state objects");
    }

    public IGraphicsFence CreateFence(ulong initialValue = 0)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-05", "fences");
    }

    public IGraphicsPresentationSurface CreatePresentationSurface(IGraphicsPresentationTarget target)
    {
        ThrowIfUnavailable();
        throw NotImplemented("D3D12-12", "Windows presentation surfaces");
    }

    public void ThrowIfUnavailable()
    {
        PollDeviceHealth();

        GraphicsDeviceStatus status = (GraphicsDeviceStatus)Volatile.Read(ref _status);
        if (status == GraphicsDeviceStatus.Lost)
            throw new GraphicsDeviceLostException(
                Volatile.Read(ref _failureReason) ?? "The Direct3D 12 device was removed.");

        if (status == GraphicsDeviceStatus.Disposed)
            throw new ObjectDisposedException(nameof(D3D12GraphicsDevice));
    }

    public void Dispose()
    {
        ID3D12Device* nativeDevice;
        Action<GraphicsDeviceStatus>? handler;

        lock (_healthLock)
        {
            if ((GraphicsDeviceStatus)_status == GraphicsDeviceStatus.Disposed)
                return;

            _status = (int)GraphicsDeviceStatus.Disposed;
            handler = _statusChanged;
            nativeDevice = _nativeDevice;
            _nativeDevice = null;
        }

        if (nativeDevice != null)
            nativeDevice->Release();

        handler?.Invoke(GraphicsDeviceStatus.Disposed);
    }

    private void PollDeviceHealth()
    {
        Action<GraphicsDeviceStatus>? handler = null;

        // Hold the lifecycle lock while querying the native pointer. This prevents Dispose from
        // releasing the COM object during GetDeviceRemovedReason.
        lock (_healthLock)
        {
            if ((GraphicsDeviceStatus)_status != GraphicsDeviceStatus.Operational ||
                _nativeDevice == null)
            {
                return;
            }

            int result = _nativeDevice->GetDeviceRemovedReason();
            if (result >= 0)
                return;

            _failureReason =
                $"ID3D12Device::GetDeviceRemovedReason returned HRESULT 0x{unchecked((uint)result):X8}.";
            _status = (int)GraphicsDeviceStatus.Lost;
            handler = _statusChanged;
        }

        handler?.Invoke(GraphicsDeviceStatus.Lost);
    }

    private static NotSupportedException NotImplemented(string task, string operation) =>
        new($"Direct3D 12 {operation} are not implemented yet; see {task} in the Auralis Direct3D 12 implementation plan.");
}
