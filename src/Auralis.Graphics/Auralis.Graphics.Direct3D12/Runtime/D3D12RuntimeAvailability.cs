using System.Runtime.InteropServices;

namespace Auralis.Graphics.Direct3D12.Runtime;

/// <summary>Result of the Direct3D 12 runtime-loader availability probe.</summary>
internal readonly record struct D3D12RuntimeStatus(
    bool IsAvailable,
    string? UnavailableReason);

/// <summary>
/// Checks whether the Windows DXGI and Direct3D 12 system libraries can be loaded from System32.
/// This is only a loader probe; it does not enumerate adapters or test device/feature support.
/// </summary>
internal static class D3D12RuntimeAvailability
{
    private const string DxgiLibraryName = "dxgi.dll";
    private const string D3D12LibraryName = "d3d12.dll";

    public static D3D12RuntimeStatus Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Evaluate(isWindows: false, hasDxgi: false, hasD3D12: false);
        }

        bool hasDxgi = CanLoadSystemLibrary(DxgiLibraryName);
        if (!hasDxgi)
        {
            return Evaluate(isWindows: true, hasDxgi: false, hasD3D12: false);
        }

        bool hasD3D12 = CanLoadSystemLibrary(D3D12LibraryName);
        return Evaluate(isWindows: true, hasDxgi: true, hasD3D12: hasD3D12);
    }

    /// <summary>Pure availability decision logic for deterministic, host-independent tests.</summary>
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

        // The probe owns only this temporary loader reference. Release it deterministically.
        try
        {
            return true;
        }
        finally
        {
            NativeLibrary.Free(libraryHandle);
        }
    }
}
