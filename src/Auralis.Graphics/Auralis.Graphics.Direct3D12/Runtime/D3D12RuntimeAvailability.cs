using System.Runtime.InteropServices;

namespace Auralis.Graphics.Direct3D12.Runtime;

/// <summary>Result of the inexpensive Direct3D 12 runtime-loader probe.</summary>
internal readonly record struct D3D12RuntimeStatus(
    bool IsAvailable,
    string? UnavailableReason);

/// <summary>
/// Detects whether the Windows DXGI and D3D12 system libraries can be loaded.
/// This is a loader check only: it does not enumerate adapters or claim that any adapter supports
/// a particular Direct3D feature level. Those checks belong to provider/device creation.
/// </summary>
internal static class D3D12RuntimeAvailability
{
    private const string DxgiLibraryName = "dxgi.dll";
    private const string D3D12LibraryName = "d3d12.dll";

    public static D3D12RuntimeStatus Probe()
    {
        if (!OperatingSystem.IsWindows())
            return Evaluate(isWindows: false, hasDxgi: false, hasD3D12: false);

        bool hasDxgi = CanLoadSystemLibrary(DxgiLibraryName);
        if (!hasDxgi)
            return Evaluate(isWindows: true, hasDxgi: false, hasD3D12: false);

        bool hasD3D12 = CanLoadSystemLibrary(D3D12LibraryName);
        return Evaluate(isWindows: true, hasDxgi: true, hasD3D12);
    }

    /// <summary>Pure decision logic, exposed internally for host-independent tests.</summary>
    internal static D3D12RuntimeStatus Evaluate(
        bool isWindows,
        bool hasDxgi,
        bool hasD3D12)
    {
        if (!isWindows)
        {
            return new D3D12RuntimeStatus(
                false,
                "The Direct3D 12 backend is available only on Windows.");
        }

        if (!hasDxgi)
        {
            return new D3D12RuntimeStatus(
                false,
                "The DXGI system library (dxgi.dll) could not be loaded from System32.");
        }

        if (!hasD3D12)
        {
            return new D3D12RuntimeStatus(
                false,
                "The Direct3D 12 system library (d3d12.dll) could not be loaded from System32.");
        }

        return new D3D12RuntimeStatus(true, null);
    }

    private static bool CanLoadSystemLibrary(string libraryName)
    {
        if (!NativeLibrary.TryLoad(
                libraryName,
                typeof(D3D12RuntimeAvailability).Assembly,
                DllImportSearchPath.System32,
                out nint libraryHandle))
        {
            return false;
        }

        NativeLibrary.Free(libraryHandle);
        return true;
    }
}
