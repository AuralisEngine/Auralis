using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Commands;

/// <summary>Shared validation rules for command and timeline operations.</summary>
internal static class GraphicsCommandValidation
{
    /// <summary>
    /// Validates an indirect command sequence. Returns false for a valid zero-count no-op.
    /// Buffer ownership and recording/pipeline state are validated by the calling command list.
    /// </summary>
    internal static bool TryValidateIndirectCommandSequence(
        in GraphicsBufferDescription description,
        ulong offset,
        uint drawCount,
        uint commandSize,
        uint stride,
        out uint effectiveStride)
    {
        if ((description.Usage & GraphicsBufferUsage.IndirectArguments) == 0)
            throw new GraphicsValidationException("Indirect commands require IndirectArguments buffer usage.");
        if (commandSize == 0 || (commandSize & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(commandSize));
        if ((offset & 3) != 0 || offset > (ulong)nint.MaxValue || offset > description.SizeInBytes)
            throw new GraphicsValidationException("Indirect argument offset must be four-byte aligned, natively addressable, and within the buffer.");
        effectiveStride = stride == 0 ? commandSize : stride;
        if (effectiveStride < commandSize || (effectiveStride & 3) != 0)
            throw new GraphicsValidationException(
                $"Indirect command stride must be zero or a four-byte-aligned value at least {commandSize} bytes.");

        if (drawCount == 0)
            return false;

        ulong byteCount = checked(checked((ulong)(drawCount - 1) * effectiveStride) + commandSize);
        if (byteCount > description.SizeInBytes - offset)
            throw new GraphicsValidationException("Indirect command sequence reads outside the argument buffer.");
        ulong lastCommandOffset = checked(offset + checked((ulong)(drawCount - 1) * effectiveStride));
        if (lastCommandOffset > (ulong)nint.MaxValue)
            throw new GraphicsValidationException("Indirect command sequence exceeds the native offset range.");
        return true;
    }

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

/// <summary>
/// Completes submitted command-list lifecycle transitions only after failed GPU work has been
/// brought to an idle point. The caller owns the backend context serialization while this runs.
/// </summary>
internal static class GraphicsCommandListSubmissionRecovery
{
    internal static void RecoverFailedSubmission<TList>(
        IReadOnlyList<TList> submittedLists,
        int submittedCount,
        bool commandsStarted,
        Action finishGpuWork,
        Action<TList> markCompleted)
    {
        ArgumentNullException.ThrowIfNull(submittedLists);
        ArgumentNullException.ThrowIfNull(finishGpuWork);
        ArgumentNullException.ThrowIfNull(markCompleted);
        if ((uint)submittedCount > (uint)submittedLists.Count)
            throw new ArgumentOutOfRangeException(nameof(submittedCount));

        // A failed Finish must leave submitted lists Pending so disposal can retry the safe path.
        if (commandsStarted)
            finishGpuWork();

        for (int i = 0; i < submittedCount; i++)
            markCompleted(submittedLists[i]);
    }
}
