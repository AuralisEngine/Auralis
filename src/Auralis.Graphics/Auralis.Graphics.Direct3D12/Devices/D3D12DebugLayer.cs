using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Direct3D12.Interop;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>Configures the D3D12 debug layer before creating the first logical device.</summary>
internal static unsafe class D3D12DebugLayer
{
    public static void Configure(GraphicsDebugOptions options)
    {
        bool enableDebugLayer =
            (options & (GraphicsDebugOptions.EnableValidation |
                        GraphicsDebugOptions.EnableDebugLayer |
                        GraphicsDebugOptions.EnableGpuBasedValidation)) != 0;

        if (!enableDebugLayer)
            return;

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
            throw new GraphicsException("D3D12GetDebugInterface succeeded without returning ID3D12Debug.");

        try
        {
            debug->EnableDebugLayer();

            if ((options & GraphicsDebugOptions.EnableGpuBasedValidation) == 0)
                return;

            Guid debug1Iid = IID.IID_ID3D12Debug1;
            ID3D12Debug1* debug1 = null;
            int queryResult = debug->QueryInterface(&debug1Iid, (void**)&debug1);

            if (queryResult < 0)
            {
                if (debug1 != null)
                    debug1->Release();

                D3D12HResult.ThrowIfFailed(
                    queryResult,
                    "ID3D12Debug::QueryInterface(ID3D12Debug1)");
            }

            if (debug1 == null)
                throw new GraphicsException(
                    "ID3D12Debug::QueryInterface succeeded without returning ID3D12Debug1.");

            try
            {
                debug1->SetEnableGPUBasedValidation(true);
            }
            finally
            {
                debug1->Release();
            }
        }
        finally
        {
            debug->Release();
        }
    }
}
