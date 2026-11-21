using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Commands;

/// <summary>Shared validation rules for command and timeline operations.</summary>
internal static class GraphicsCommandValidation
{
    internal static void ValidateIndexedIndirectIndexBufferOffset(ulong indexBufferOffset)
    {
        if (indexBufferOffset != 0)
            throw new GraphicsValidationException(
                "Indexed indirect drawing does not support a non-zero index-buffer offset.");
    }

    internal static void ValidateTextureCopyNotAliased(
        IGraphicsTexture source,
        IGraphicsTexture destination)
    {
        if (ReferenceEquals(source, destination))
            throw new GraphicsValidationException("A texture cannot be copied onto itself.");
    }

    internal static void ValidateCpuFenceSignalDoesNotOvertakeGpu(
        ulong cpuSignalValue,
        ulong? earliestPendingGpuValue)
    {
        if (earliestPendingGpuValue is ulong pendingValue && pendingValue < cpuSignalValue)
            throw new GraphicsValidationException(
                $"CPU fence signal value {cpuSignalValue} cannot overtake pending GPU signal value {pendingValue}.");
    }
}
