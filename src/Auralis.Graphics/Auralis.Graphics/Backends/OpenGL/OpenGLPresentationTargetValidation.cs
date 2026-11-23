using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Presentation;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Validates that a common presentation snapshot is the drawable belonging to this GL context.</summary>
internal static class OpenGLPresentationTargetValidation
{
    /// <summary>Verifies a presentation snapshot identifies the drawable supplied by the injected context provider.</summary>
    /// <param name="provider">Provider that owns or exposes the current GL context and associated drawable.</param>
    /// <param name="snapshot">Snapshot of the application presentation target's native handles and extent.</param>
    /// <exception cref="GraphicsBackendUnavailableException">The provider has no drawable or the snapshot identifies another native target.</exception>
    internal static void ValidateTarget(
        IOpenGLContextProvider provider,
        in GraphicsPresentationTargetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(provider);
        GraphicsPresentationTargetSnapshot copy = snapshot;
        copy.Validate();

        if (!provider.SupportsPresentation)
            throw new GraphicsBackendUnavailableException(
                GraphicsBackend.OpenGL,
                "The injected OpenGL context provider has no presentation drawable.");

        if (!provider.IsPresentationTargetCompatible(copy))
            throw new GraphicsBackendUnavailableException(
                GraphicsBackend.OpenGL,
                "The target's native handles do not identify the drawable associated with the injected OpenGL context.");
    }

    /// <summary>Checks that the host provider can honor the requested presentation mode.</summary>
    /// <param name="provider">Provider responsible for configuring the host swap behavior.</param>
    /// <param name="presentMode">Requested presentation mode.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="presentMode" /> is undefined.</exception>
    /// <exception cref="GraphicsValidationException">The host provider cannot implement the requested mode.</exception>
    internal static void ValidatePresentMode(
        IOpenGLContextProvider provider,
        GraphicsPresentMode presentMode)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!Enum.IsDefined(presentMode))
            throw new ArgumentOutOfRangeException(nameof(presentMode));

        if (!provider.SupportsPresentMode(presentMode))
            throw new GraphicsValidationException(
                $"The OpenGL context provider cannot honor present mode {presentMode}.");
    }
}
