using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Direct3D12.Interop;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>DXGI adapter enumeration for the Direct3D 12 provider.</summary>
internal static unsafe class D3D12AdapterEnumerator
{
    // DXGI_ERROR_NOT_FOUND is the documented end-of-enumeration result from EnumAdapters1.
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    public static ReadOnlyMemory<GraphicsAdapterInfo> Enumerate(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new GraphicsBackendUnavailableException(
                GraphicsBackend.Direct3D12,
                "DXGI adapter enumeration is available only on Windows.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        IDXGIFactory1* factory = null;
        int createResult = CreateDXGIFactory1(__uuidof<IDXGIFactory1>(), (void**)&factory);
        if (createResult < 0)
        {
            if (factory != null)
                factory->Release();

            D3D12HResult.ThrowIfFailed(createResult, "CreateDXGIFactory1");
        }

        if (factory == null)
            throw new GraphicsException("CreateDXGIFactory1 succeeded without returning a factory.");

        try
        {
            List<GraphicsAdapterInfo> adapters = new(capacity: 4);
            uint adapterIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IDXGIAdapter1* adapter = null;
                int enumerationResult = factory->EnumAdapters1(adapterIndex, &adapter);
                if (enumerationResult == DxgiErrorNotFound)
                    break;

                if (enumerationResult < 0)
                {
                    if (adapter != null)
                        adapter->Release();

                    D3D12HResult.ThrowIfFailed(
                        enumerationResult,
                        $"IDXGIFactory1::EnumAdapters1({adapterIndex})");
                }

                if (adapter == null)
                    throw new GraphicsException(
                        $"IDXGIFactory1::EnumAdapters1({adapterIndex}) succeeded without returning an adapter.");

                try
                {
                    // The null output pointer is intentional: the runtime performs its documented
                    // adapter/feature-level support test without creating or retaining a device.
                    // S_FALSE is the successful result for this probe.
                    int supportResult = D3D12CreateDevice(
                        (IUnknown*)adapter,
                        D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                        __uuidof<ID3D12Device>(),
                        null);

                    if (supportResult >= 0)
                    {
                        DXGI_ADAPTER_DESC1 description = default;
                        int descriptionResult = adapter->GetDesc1(&description);
                        D3D12HResult.ThrowIfFailed(
                            descriptionResult,
                            $"IDXGIAdapter1::GetDesc1({adapterIndex})");

                        D3D12AdapterDescriptor descriptor =
                            D3D12AdapterInfoMapper.FromNative(description);
                        GraphicsAdapterInfo info = D3D12AdapterInfoMapper.Create(descriptor);

                        for (int previous = 0; previous < adapters.Count; previous++)
                        {
                            if (adapters[previous].Id == info.Id)
                                throw new GraphicsException(
                                    $"DXGI returned duplicate adapter LUID 0x{info.Id.Low:X16}.");
                        }

                        adapters.Add(info);
                    }
                }
                finally
                {
                    adapter->Release();
                }

                if (adapterIndex == uint.MaxValue)
                    throw new GraphicsException("DXGI adapter enumeration exceeded the supported adapter-index range.");

                adapterIndex++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return adapters.Count == 0
                ? ReadOnlyMemory<GraphicsAdapterInfo>.Empty
                : adapters.ToArray();
        }
        finally
        {
            factory->Release();
        }
    }
}
