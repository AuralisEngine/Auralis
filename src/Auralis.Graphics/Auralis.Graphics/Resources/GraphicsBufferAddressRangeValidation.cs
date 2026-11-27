using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Resources;

/// <summary>Shared, allocation-free validation for buffer-backed GPU virtual-address ranges.</summary>
internal static class GraphicsBufferAddressRangeValidation
{
    /// <summary>
    /// Validates resource lifetime, ownership, usage, CPU buffer bounds, device-address arithmetic,
    /// full inclusive GPU-address range, and optional effective-address alignment.
    /// </summary>
    public static ulong Validate(
        IGraphicsBuffer buffer,
        IGraphicsDevice? expectedDevice,
        ulong offset,
        ulong size,
        ulong alignment,
        GraphicsBufferUsage requiredUsage,
        string parameterName,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.IsDisposed || buffer.Device.IsDisposed)
            throw new ObjectDisposedException(parameterName);
        if (expectedDevice is not null && !ReferenceEquals(buffer.Device, expectedDevice))
            throw new ArgumentException("The buffer and consuming GPU object must belong to the same device.", parameterName);

        GraphicsBufferDescription description = buffer.Description;
        description.Validate();
        if ((description.Usage & requiredUsage) != requiredUsage)
            throw new ArgumentException($"The buffer must declare usage {requiredUsage}.", parameterName);
        if (buffer is not IGraphicsBufferDeviceAddress address || address.DeviceAddress == 0)
            throw new ArgumentException("The buffer must expose a non-zero GPU device address.", parameterName);
        if (alignment == 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "GPU address alignment must be non-zero.");
        if (size == 0 && !allowEmpty)
            throw new ArgumentOutOfRangeException(nameof(size), "The GPU-address range must not be empty.");
        if (offset > description.SizeInBytes || size > description.SizeInBytes - offset)
            throw new ArgumentOutOfRangeException(parameterName, "The selected buffer range exceeds the resource bounds.");
        if (offset > ulong.MaxValue - address.DeviceAddress)
            throw new ArgumentOutOfRangeException(parameterName, "The effective GPU device address overflows.");

        ulong effectiveAddress = address.DeviceAddress + offset;
        if (effectiveAddress == 0 || effectiveAddress % alignment != 0)
            throw new ArgumentException(
                $"The effective GPU device address must be non-zero and aligned to {alignment} bytes.",
                parameterName);
        if (size != 0 && size - 1 > ulong.MaxValue - effectiveAddress)
            throw new ArgumentOutOfRangeException(parameterName, "The full GPU-address range overflows.");

        return effectiveAddress;
    }
}
