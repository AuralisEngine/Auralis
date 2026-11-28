using System.Runtime.Versioning;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Direct3D12.Interop;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>
/// Serializes process-wide D3D12 debug configuration with the first native device creation.
/// The D3D12 debug layer and GPU-based validation must be enabled before creating a device.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal static unsafe class D3D12DebugLayer
{
    private static readonly object Gate = new();
    private static bool _deviceCreated;
    private static bool _debugLayerEnabled;
    private static bool _gpuBasedValidationEnabled;

    public static ID3D12Device* CreateDevice(
        IUnknown* adapter,
        GraphicsDebugOptions options)
    {
        if (adapter == null)
            throw new ArgumentNullException(nameof(adapter));

        lock (Gate)
        {
            ConfigureLocked(options);

            Guid deviceIid = IID.IID_ID3D12Device;
            ID3D12Device* device = null;
            int result = D3D12CreateDevice(
                adapter,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                &deviceIid,
                (void**)&device);

            if (result < 0)
            {
                if (device != null)
                    device->Release();

                D3D12HResult.ThrowIfFailed(result, "D3D12CreateDevice");
            }

            if (device == null)
                throw new GraphicsException(
                    "D3D12CreateDevice succeeded without returning ID3D12Device.");

            _deviceCreated = true;
            return device;
        }
    }

    private static void ConfigureLocked(GraphicsDebugOptions options)
    {
        bool debugRequested =
            (options & (GraphicsDebugOptions.EnableValidation |
                        GraphicsDebugOptions.EnableDebugLayer |
                        GraphicsDebugOptions.EnableGpuBasedValidation)) != 0;
        bool gpuValidationRequested =
            (options & GraphicsDebugOptions.EnableGpuBasedValidation) != 0;

        if (!debugRequested)
            return;

        if (!_debugLayerEnabled)
        {
            if (_deviceCreated)
            {
                throw new GraphicsException(
                    "The D3D12 debug layer was requested after a Direct3D 12 device had already been created. " +
                    "Enable it in GraphicsDeviceOptions before the first D3D12 device creation.");
            }

            Guid debugIid = IID.IID_ID3D12Debug;
            ID3D12Debug* debug = null;
            int result = D3D12GetDebugInterface(&debugIid, (void**)&debug);

            if (result < 0)
            {
                if (debug != null)
                    debug->Release();

                D3D12HResult.ThrowIfFailed(result, "D3D12GetDebugInterface(ID3D12Debug)");
            }

            if (debug == null)
                throw new GraphicsException(
                    "D3D12GetDebugInterface succeeded without returning ID3D12Debug.");

            try
            {
                debug->EnableDebugLayer();
                _debugLayerEnabled = true;
            }
            finally
            {
                debug->Release();
            }
        }

        if (gpuValidationRequested && !_gpuBasedValidationEnabled)
        {
            if (_deviceCreated)
            {
                throw new GraphicsException(
                    "GPU-based validation was requested after a Direct3D 12 device had already been created. " +
                    "Enable it in GraphicsDeviceOptions before the first D3D12 device creation.");
            }

            Guid debug1Iid = IID.IID_ID3D12Debug1;
            ID3D12Debug1* debug1 = null;
            int result = D3D12GetDebugInterface(&debug1Iid, (void**)&debug1);

            if (result < 0)
            {
                if (debug1 != null)
                    debug1->Release();

                D3D12HResult.ThrowIfFailed(result, "D3D12GetDebugInterface(ID3D12Debug1)");
            }

            if (debug1 == null)
                throw new GraphicsException(
                    "D3D12GetDebugInterface succeeded without returning ID3D12Debug1.");

            try
            {
                debug1->SetEnableGPUBasedValidation(true);
                _gpuBasedValidationEnabled = true;
            }
            finally
            {
                debug1->Release();
            }
        }
    }
}
