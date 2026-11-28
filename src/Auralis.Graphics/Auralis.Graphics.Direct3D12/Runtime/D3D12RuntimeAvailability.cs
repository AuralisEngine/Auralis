using System.Reflection;
using System.Runtime.InteropServices;

namespace Auralis.Graphics.Direct3D12.Runtime;

internal enum D3D12RuntimeStatus
{
    UnsupportedPlatform,
    MissingDxgi,
    MissingDirect3D12,
    Available
}

internal readonly record struct D3D12RuntimeAvailability(
    D3D12RuntimeStatus Status,
    string? UnavailableReason)
{
    public bool IsAvailable => Status == D3D12RuntimeStatus.Available;

    public static D3D12RuntimeAvailability Evaluate(
        bool isWindows,
        bool dxgiAvailable,
        bool direct3D12Available)
    {
        if (!isWindows)
        {
            return new(D3D12RuntimeStatus.UnsupportedPlatform,
                "Direct3D 12 is available only on Windows.");
        }

        if (!dxgiAvailable)
        {
            return new(D3D12RuntimeStatus.MissingDxgi,
                "The Windows DXGI runtime library (dxgi.dll) could not be loaded.");
        }

        if (!direct3D12Available)
        {
            return new(D3D12RuntimeStatus.MissingDirect3D12,
                "The Windows Direct3D 12 runtime library (d3d12.dll) could not be loaded.");
        }

        return new(D3D12RuntimeStatus.Available, null);
    }

    public static D3D12RuntimeAvailability Probe()
    {
        if (!OperatingSystem.IsWindows())
            return Evaluate(isWindows: false, dxgiAvailable: false, direct3D12Available: false);

        bool dxgiAvailable = TryLoadSystemLibrary("dxgi.dll");
        if (!dxgiAvailable)
            return Evaluate(isWindows: true, dxgiAvailable: false, direct3D12Available: false);

        bool direct3D12Available = TryLoadSystemLibrary("d3d12.dll");
        return Evaluate(isWindows: true, dxgiAvailable: true, direct3D12Available);
    }

    private static bool TryLoadSystemLibrary(string libraryName)
    {
        if (!NativeLibrary.TryLoad(
                libraryName,
                typeof(D3D12RuntimeAvailability).Assembly,
                DllImportSearchPath.System32,
                out IntPtr libraryHandle))
        {
            return false;
        }

        NativeLibrary.Free(libraryHandle);
        return true;
    }
}
