using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Resources;

/// <summary>Timestamp interpretation metadata for one queue family/index.</summary>
public readonly record struct GraphicsTimestampProperties(
    bool Supported,
    ulong TimestampFrequencyHz,
    uint ValidBits)
{
    public void Validate()
    {
        if (Supported && (TimestampFrequencyHz == 0 || ValidBits is 0 or > 64))
            throw new ArgumentException("Supported timestamps require a non-zero frequency and 1-64 valid bits.");
        if (!Supported && (TimestampFrequencyHz != 0 || ValidBits != 0))
            throw new ArgumentException("Unsupported timestamps must report zero frequency and valid bits.");
    }

    public ulong ValidMask => !Supported ? 0 : ValidBits == 64 ? ulong.MaxValue : (1UL << (int)ValidBits) - 1;

    public double TicksToMilliseconds(ulong ticks)
    {
        if (!Supported || TimestampFrequencyHz == 0)
            throw new InvalidOperationException("Timestamp queries are not supported for this queue.");
        ulong maskedTicks = ticks & ValidMask;
        return maskedTicks * 1000d / TimestampFrequencyHz;
    }
}

/// <summary>Optional query extension for queue-specific timestamp frequency and counter precision.</summary>
public interface IGraphicsTimestampPropertiesDevice
{
    GraphicsTimestampProperties GetTimestampProperties(GraphicsQueueType queueType, uint queueIndex = 0);
}
