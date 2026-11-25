using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Synchronization;

namespace Auralis.Graphics.Devices;

/// <summary>Non-zero bit mask selecting one or more physical devices in a device group.</summary>
public readonly record struct GraphicsDeviceGroupMask(uint Value)
{
    public void Validate(uint deviceCount)
    {
        if (deviceCount is 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(deviceCount), "Device groups support between 1 and 32 devices.");
        uint validMask = deviceCount == 32 ? uint.MaxValue : (1u << (int)deviceCount) - 1;
        if (Value == 0 || (Value & ~validMask) != 0)
            throw new ArgumentOutOfRangeException(nameof(Value));
    }

    public static GraphicsDeviceGroupMask All(uint deviceCount)
    {
        if (deviceCount is 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(deviceCount));
        return new GraphicsDeviceGroupMask(deviceCount == 32 ? uint.MaxValue : (1u << (int)deviceCount) - 1);
    }
}

/// <summary>Peer-memory access supported between two physical devices in a group.</summary>
[Flags]
public enum GraphicsPeerMemoryAccess
{
    None = 0,
    CopySource = 1 << 0,
    CopyDestination = 1 << 1,
    GenericRead = 1 << 2,
    GenericWrite = 1 << 3
}

/// <summary>Logical graphics device representing an explicit multi-GPU group and its physical member devices.</summary>
public interface IGraphicsDeviceGroup : IGraphicsDevice
{
    uint DeviceCount { get; }
    IGraphicsDevice GetDevice(uint index);
    GraphicsPeerMemoryAccess GetPeerMemoryAccess(uint sourceDeviceIndex, uint destinationDeviceIndex);
}

/// <summary>Optional provider extension for creating a logical device group from explicit adapter IDs.</summary>
public interface IGraphicsDeviceGroupProvider
{
    IGraphicsDeviceGroup CreateDeviceGroup(
        ReadOnlySpan<GraphicsAdapterId> adapters,
        GraphicsDeviceOptions options);
}

/// <summary>Optional group-queue extension for submitting command lists to an explicit device mask.</summary>
public interface IGraphicsDeviceGroupCommandQueue : IGraphicsCommandQueue
{
    IGraphicsDeviceGroup DeviceGroup { get; }
    /// <summary>Submits command lists created by the group device to the selected physical-device mask.</summary>
    void SubmitToDevices(
        ReadOnlySpan<IGraphicsCommandList> commandLists,
        GraphicsDeviceGroupMask deviceMask,
        IGraphicsFence? signalFence = null,
        ulong signalValue = 0);
}
