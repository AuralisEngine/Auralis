using System.Runtime.InteropServices;
using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Direct3D12.Interop;

/// <summary>Converts failing HRESULT values into backend diagnostics without losing the native code.</summary>
internal static class D3D12HResult
{
    public static void ThrowIfFailed(int hresult, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (hresult >= 0)
            return;

        throw new D3D12NativeException(hresult, operation);
    }
}

/// <summary>A native Direct3D 12/DXGI failure with the original HRESULT preserved.</summary>
internal sealed class D3D12NativeException : GraphicsException
{
    public int NativeHResult { get; }

    public string Operation { get; }

    public D3D12NativeException(int hresult, string operation)
        : base(CreateMessage(hresult, operation), new COMException(CreateMessage(hresult, operation), hresult))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (hresult >= 0)
            throw new ArgumentOutOfRangeException(nameof(hresult), "A native failure exception requires a failing HRESULT.");

        NativeHResult = hresult;
        Operation = operation;
    }

    private static string CreateMessage(int hresult, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return $"{operation} failed with HRESULT 0x{unchecked((uint)hresult):X8}.";
    }
}
