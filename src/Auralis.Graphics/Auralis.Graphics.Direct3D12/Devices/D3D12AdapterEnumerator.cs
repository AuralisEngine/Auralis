using System.Runtime.Versioning;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Direct3D12.Interop;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>DXGI adapter discovery and native adapter-reference acquisition.</summary>
[SupportedOSPlatform("windows10.0.10240")]
internal static unsafe class D3D12AdapterEnumerator
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiErrorUnsupported = unchecked((int)0x887A0004);
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int EInvalidArg = unchecked((int)0x80070057);

    public static ReadOnlyMemory<GraphicsAdapterInfo> Enumerate(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDXGIFactory1* factory = CreateFactory();
        IDXGIFactory4* factory4 = null;
        try
        {
            factory4 = TryGetFactory4(factory);
            List<GraphicsAdapterInfo> adapters = new(capacity: 4);
            uint adapterIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IDXGIAdapter1* adapter = null;
                int enumerationResult = factory->EnumAdapters1(adapterIndex, &adapter);
                if (enumerationResult == DxgiErrorNotFound)
                {
                    if (adapter != null)
                        adapter->Release();

                    break;
                }

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

            if (factory4 != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Guid adapterIid = IID.IID_IDXGIAdapter1;
                IDXGIAdapter1* warpAdapter = null;
                int warpResult = factory4->EnumWarpAdapter(&adapterIid, (void**)&warpAdapter);

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

    /// <summary>Returns one owned IDXGIAdapter1 reference matching the backend-scoped LUID identity.</summary>
    public static IDXGIAdapter1* AcquireAdapter(
        GraphicsAdapterId adapterId,
        CancellationToken cancellationToken)
    {
        if (!D3D12AdapterInfoMapper.IsD3D12AdapterId(adapterId))
            throw new ArgumentException("The adapter identifier was not produced by the Direct3D 12 provider.", nameof(adapterId));

        cancellationToken.ThrowIfCancellationRequested();

        IDXGIFactory1* factory = CreateFactory();
        IDXGIFactory4* factory4 = null;
        try
        {
            factory4 = TryGetFactory4(factory);
            uint adapterIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IDXGIAdapter1* adapter = null;
                int result = factory->EnumAdapters1(adapterIndex, &adapter);
                if (result == DxgiErrorNotFound)
                {
                    if (adapter != null)
                        adapter->Release();

                    break;
                }

                if (result < 0)
                {
                    if (adapter != null)
                        adapter->Release();

                    D3D12HResult.ThrowIfFailed(result, $"IDXGIFactory1::EnumAdapters1({adapterIndex})");
                }

                if (adapter == null)
                    throw new GraphicsException(
                        $"IDXGIFactory1::EnumAdapters1({adapterIndex}) succeeded without returning an adapter.");

                DXGI_ADAPTER_DESC1 description = default;
                try
                {
                    int descriptionResult = adapter->GetDesc1(&description);
                    D3D12HResult.ThrowIfFailed(
                        descriptionResult,
                        $"IDXGIAdapter1::GetDesc1({adapterIndex})");

                    D3D12AdapterDescriptor descriptor = D3D12AdapterInfoMapper.FromNative(description);
                    GraphicsAdapterId currentId = D3D12AdapterInfoMapper.CreateAdapterId(
                        descriptor.LuidLowPart,
                        descriptor.LuidHighPart);

                    if (currentId == adapterId)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return adapter; // Transfer the owned COM reference to the caller.
                    }
                }
                catch
                {
                    adapter->Release();
                    throw;
                }

                adapter->Release();
                if (adapterIndex == uint.MaxValue)
                    break;

                adapterIndex++;
            }

            if (factory4 != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Guid adapterIid = IID.IID_IDXGIAdapter1;
                IDXGIAdapter1* warpAdapter = null;
                int warpResult = factory4->EnumWarpAdapter(&adapterIid, (void**)&warpAdapter);

                if (warpResult >= 0)
                {
                    if (warpAdapter == null)
                        throw new GraphicsException(
                            "IDXGIFactory4::EnumWarpAdapter succeeded without returning an adapter.");

                    DXGI_ADAPTER_DESC1 description = default;
                    try
                    {
                        int descriptionResult = warpAdapter->GetDesc1(&description);
                        D3D12HResult.ThrowIfFailed(descriptionResult, "IDXGIAdapter1::GetDesc1(WARP)");

                        D3D12AdapterDescriptor descriptor = D3D12AdapterInfoMapper.FromNative(description);
                        GraphicsAdapterId warpId = D3D12AdapterInfoMapper.CreateAdapterId(
                            descriptor.LuidLowPart,
                            descriptor.LuidHighPart);

                        if (warpId == adapterId)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            IDXGIAdapter1* result = warpAdapter;
                            warpAdapter = null;
                            return result;
                        }
                    }
                    finally
                    {
                        if (warpAdapter != null)
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
        }
        finally
        {
            if (factory4 != null)
                factory4->Release();
            factory->Release();
        }

        throw new GraphicsBackendUnavailableException(
            GraphicsBackend.Direct3D12,
            $"The selected adapter with LUID 0x{adapterId.Low:X16} is no longer present.");
    }

    private static IDXGIFactory1* CreateFactory()
    {
        Guid iid = IID.IID_IDXGIFactory1;
        IDXGIFactory1* factory = null;
        int result = CreateDXGIFactory1(&iid, (void**)&factory);

        if (result < 0)
        {
            if (factory != null)
                factory->Release();

            D3D12HResult.ThrowIfFailed(result, "CreateDXGIFactory1");
        }

        if (factory == null)
            throw new GraphicsException("CreateDXGIFactory1 succeeded without returning a factory.");

        return factory;
    }

    private static IDXGIFactory4* TryGetFactory4(IDXGIFactory1* factory)
    {
        Guid iid = IID.IID_IDXGIFactory4;
        IDXGIFactory4* factory4 = null;
        int result = factory->QueryInterface(&iid, (void**)&factory4);

        if (result >= 0)
        {
            if (factory4 == null)
                throw new GraphicsException(
                    "IDXGIFactory1::QueryInterface(IDXGIFactory4) succeeded without returning a factory.");

            return factory4;
        }

        if (factory4 != null)
        {
            factory4->Release();
            factory4 = null;
        }

        if (result != ENoInterface)
            D3D12HResult.ThrowIfFailed(result, "IDXGIFactory1::QueryInterface(IDXGIFactory4)");

        return null;
    }

    private static void TryAddD3D12Adapter(
        IDXGIAdapter1* adapter,
        List<GraphicsAdapterInfo> adapters,
        string operation)
    {
        Guid deviceIid = IID.IID_ID3D12Device;
        ID3D12Device* device = null;
        int createResult = D3D12CreateDevice(
            (IUnknown*)adapter,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            &deviceIid,
            (void**)&device);

        if (createResult < 0)
        {
            if (device != null)
                device->Release();

            if (createResult == EInvalidArg ||
                createResult == DxgiErrorUnsupported ||
                createResult == ENoInterface)
            {
                return;
            }

            D3D12HResult.ThrowIfFailed(createResult, $"D3D12CreateDevice support probe for {operation}");
        }

        if (device == null)
            throw new GraphicsException($"D3D12CreateDevice succeeded without returning a device for {operation}.");

        try
        {
            DXGI_ADAPTER_DESC1 description = default;
            int descriptionResult = adapter->GetDesc1(&description);
            D3D12HResult.ThrowIfFailed(
                descriptionResult,
                $"IDXGIAdapter1::GetDesc1 for {operation}");

            D3D12AdapterDescriptor descriptor = D3D12AdapterInfoMapper.FromNative(description);
            D3D12FeatureSnapshot features = D3D12DeviceCapabilityQuery.QueryFeatures(device);
            GraphicsAdapterInfo info = D3D12AdapterInfoMapper.Create(descriptor, features);

            // WARP can overlap the ordinary DXGI enumeration path. LUID is the stable identity.
            for (int previous = 0; previous < adapters.Count; previous++)
            {
                if (adapters[previous].Id == info.Id)
                    return;
            }

            adapters.Add(info);
        }
        finally
        {
            device->Release();
        }
    }
}
