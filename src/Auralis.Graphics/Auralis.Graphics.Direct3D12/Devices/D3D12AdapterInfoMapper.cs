using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Auralis.Graphics.Abstractions;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>Minimal immutable DXGI adapter data used by the managed adapter-info translation.</summary>
internal readonly record struct D3D12AdapterDescriptor(
    string? Name,
    uint VendorId,
    uint DeviceId,
    ulong DedicatedMemoryBytes,
    uint LuidLowPart,
    int LuidHighPart,
    bool IsSoftware,
    bool IsRemote);

/// <summary>
/// Converts DXGI adapter metadata into backend-independent information without guessing at
/// integrated-versus-discrete classification (DXGI_ADAPTER_DESC1 does not provide that distinction).
/// </summary>
internal static class D3D12AdapterInfoMapper
{
    // "D3D12ADP" in ASCII. The high half namespaces these identifiers away from other providers;
    // the low half is the DXGI adapter LUID, not an enumeration index or display name.
    private const ulong AdapterIdentifierNamespace = 0x4433443132414450UL;

    public static GraphicsAdapterInfo Create(in D3D12AdapterDescriptor descriptor)
    {
        GraphicsAdapterId id = CreateAdapterId(descriptor.LuidLowPart, descriptor.LuidHighPart);

        GraphicsAdapterType type = descriptor.IsSoftware
            ? GraphicsAdapterType.Cpu
            : descriptor.IsRemote
                ? GraphicsAdapterType.Virtual
                : GraphicsAdapterType.Unknown;

        string name = string.IsNullOrWhiteSpace(descriptor.Name)
            ? "Unknown Direct3D 12 adapter"
            : descriptor.Name;

        return new GraphicsAdapterInfo(
            id,
            name,
            type,
            descriptor.VendorId,
            descriptor.DeviceId,
            descriptor.DedicatedMemoryBytes);
    }

    public static GraphicsAdapterId CreateAdapterId(uint luidLowPart, int luidHighPart)
    {
        ulong luid = ((ulong)unchecked((uint)luidHighPart) << 32) | luidLowPart;
        if (luid == 0)
            throw new ArgumentException("DXGI returned a zero adapter LUID.", nameof(luidLowPart));

        return new GraphicsAdapterId(AdapterIdentifierNamespace, luid);
    }

    public static D3D12AdapterDescriptor FromNative(in DXGI_ADAPTER_DESC1 description)
    {
        ref char firstCharacter = ref Unsafe.AsRef(in description.Description.e0);
        ReadOnlySpan<char> descriptionChars =
            MemoryMarshal.CreateReadOnlySpan(ref firstCharacter, 128);

        int terminator = descriptionChars.IndexOf('\0');
        if (terminator >= 0)
            descriptionChars = descriptionChars[..terminator];

        string name = descriptionChars.IsEmpty ? string.Empty : new string(descriptionChars);

        const uint softwareFlag =
            (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE;
        const uint remoteFlag =
            (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_REMOTE;

        return new D3D12AdapterDescriptor(
            name,
            description.VendorId,
            description.DeviceId,
            (ulong)description.DedicatedVideoMemory,
            description.AdapterLuid.LowPart,
            description.AdapterLuid.HighPart,
            (description.Flags & softwareFlag) != 0,
            (description.Flags & remoteFlag) != 0);
    }
}
