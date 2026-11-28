using System.Runtime.InteropServices;
using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Direct3D12.Interop;

/// <summary>
/// Describes a failed native Direct3D 12 or DXGI operation without discarding its HRESULT.
/// The inner COMException preserves the same HRESULT for diagnostics and interop tooling.
/// </summary>
internal sealed class D3D12NativeException : GraphicsException
{
    private D3D12NativeException(string operation, int hresultCode, string message)
        : base(message, new COMException(message, hresultCode))
    {
        Operation = operation;
        HResultCode = hresultCode;
    }

    public string Operation { get; }

    /// <summary>The original signed HRESULT returned by the native API.</summary>
    public int HResultCode { get; }

    public static D3D12NativeException Create(string operation, int hresultCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        string message =
            $"{operation} failed with HRESULT 0x{unchecked((uint)hresultCode):X8}.";

        return new D3D12NativeException(operation, hresultCode, message);
    }
}

/// <summary>Common HRESULT handling for the Direct3D 12 and DXGI implementation.</summary>
internal static class D3D12HResult
{
    /// <summary>
    /// Throws only for failed HRESULT values (the sign bit is set). S_FALSE and other non-negative
    /// success statuses are preserved as successful results.
    /// </summary>
    public static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
            throw D3D12NativeException.Create(operation, hresult);
    }
}
