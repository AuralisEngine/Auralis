using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Direct3D12.Interop;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>DXGI adapter enumeration for the Direct3D 12 provider.</summary>
internal static unsafe class D3D12AdapterEnumerator
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiErrorUnsupported = unchecked((int)0x887A0004);
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int EInvalidArg = unchecked((int)0x80070057);

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

        IDXGIFactory4* factory4 = null;
        try
        {
            int queryResult = factory->QueryInterface(__uuidof<IDXGIFactory4>(), (void**)&factory4);
            if (queryResult < 0)
            {
                if (factory4 != null)
                {
                    factory4->Release();
                    factory4 = null;
                }

                if (queryResult != ENoInterface)
                    D3D12HResult.ThrowIfFailed(queryResult, "IDXGIFactory1::QueryInterface(IDXGIFactory4)");
            }
            else if (factory4 == null)
            {
                throw new GraphicsException(
                    "IDXGIFactory1::QueryInterface(IDXGIFactory4) succeeded without returning a factory.");
            }

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
                    TryAddD3D12Adapter(adapter, adapters, $"DXGI adapter {adapterIndex}");
                }
                finally
                {
                    adapter->Release();
                }

                if (adapterIndex == uint.MaxValue)
                    throw new GraphicsException("DXGI adapter enumeration exceeded the supported adapter-index range.");

                adapterIndex++;
            }

            // WARP is obtained separately from EnumAdapters1. Include it as a CPU adapter when
            // the runtime exposes it and the D3D12 feature-level support probe succeeds.
            if (factory4 != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IDXGIAdapter1* warpAdapter = null;
                int warpResult = factory4->EnumWarpAdapter(
                    __uuidof<IDXGIAdapter1>(),
                    (void**)&warpAdapter);

                if (warpResult >= 0)
                {
                    if (warpAdapter == null)
                        throw new GraphicsException(
                            "IDXGIFactory4::EnumWarpAdapter succeeded without returning an adapter.");

                    try
                    {
                        TryAddD3D12Adapter(warpAdapter, adapters, "DXGI WARP adapter");
                    }
                    finally
                    {
                        warpAdapter->Release();
                    }
                }
                else
                {
                    if (warpAdapter != null)
                        warpAdapter->Release();

                    if (warpResult != DxgiErrorUnsupported && warpResult != ENoInterface)
                        D3D12HResult.ThrowIfFailed(warpResult, "IDXGIFactory4::EnumWarpAdapter");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return adapters.Count == 0
                ? ReadOnlyMemory<GraphicsAdapterInfo>.Empty
                : adapters.ToArray();
        }
        finally
        {
            if (factory4 != null)
                factory4->Release();

            factory->Release();
        }
    }

    private static void TryAddD3D12Adapter(
        IDXGIAdapter1* adapter,
        List<GraphicsAdapterInfo> adapters,
        string operation)
    {
        // The null output pointer is intentional: the runtime performs its documented
        // adapter/feature-level support test without creating or retaining a logical device.
        // S_FALSE is the successful result for this probe.
        int supportResult = D3D12CreateDevice(
            (IUnknown*)adapter,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            __uuidof<ID3D12Device>(),
            null);

        if (supportResult < 0)
        {
            // Unsupported feature level / interface means this DXGI adapter is not a D3D12
            // adapter. Other errors may indicate a real runtime or driver failure and must surface.
            if (supportResult == EInvalidArg ||
                supportResult == DxgiErrorUnsupported ||
                supportResult == ENoInterface)
            {
                return;
            }

            D3D12HResult.ThrowIfFailed(supportResult, $"D3D12CreateDevice support probe for {operation}");
        }

        DXGI_ADAPTER_DESC1 description = default;
        int descriptionResult = adapter->GetDesc1(&description);
        D3D12HResult.ThrowIfFailed(
            descriptionResult,
            $"IDXGIAdapter1::GetDesc1 for {operation}");

        D3D12AdapterDescriptor descriptor = D3D12AdapterInfoMapper.FromNative(description);
        GraphicsAdapterInfo info = D3D12AdapterInfoMapper.Create(descriptor);

        // A WARP adapter may also appear in EnumAdapters1 on some runtime configurations.
        // The LUID is the identity, so retain a single entry if the two enumeration paths overlap.
        for (int previous = 0; previous < adapters.Count; previous++)
        {
            if (adapters[previous].Id == info.Id)
                return;
        }

        adapters.Add(info);
    }
}
