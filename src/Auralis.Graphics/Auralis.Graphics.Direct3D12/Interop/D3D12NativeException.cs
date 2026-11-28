using System.Runtime.InteropServices;
using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Direct3D12.Interop;

/// <summary>
/// Describes a failed Direct3D 12 or DXGI HRESULT without losing the native status.
/// The signed value is retained on this exception and by its inner COMException.
/// </summary>
internal sealed class D3D12NativeException : GraphicsException
{
    private D3D12NativeException(string operation, int hresultCode, string message)
        : base(message, new COMException(message, hresultCode))
    {
        Operation = operation;
        HResultCode = hresultCode;
        HResult = hresultCode;
    }

    public string Operation { get; }

    /// <summary>The original signed 32-bit HRESULT returned by the native API.</summary>
    public int HResultCode { get; }

    /// <summary>The original HRESULT interpreted as its unsigned bit pattern.</summary>
    public uint UnsignedHResultCode => unchecked((uint)HResultCode);

    public static D3D12NativeException Create(string operation, int hresultCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (hresultCode >= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hresultCode),
                hresultCode,
                "A native exception can only represent a failed HRESULT.");
        }

        string message =
            $"{operation} failed with HRESULT 0x{unchecked((uint)hresultCode):X8}.";

        return new D3D12NativeException(operation, hresultCode, message);
    }
}

/// <summary>HRESULT handling shared by Direct3D 12 and DXGI call sites.</summary>
internal static class D3D12HResult
{
    /// <summary>
    /// Throws when the native HRESULT has its failure bit set.
    /// All non-negative statuses, including S_FALSE, remain successful statuses.
    /// Silk.NET exposes native HRESULT results as signed 32-bit integers.
    /// </summary>
    public static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw D3D12NativeException.Create(operation, hresult);
        }
    }
}
