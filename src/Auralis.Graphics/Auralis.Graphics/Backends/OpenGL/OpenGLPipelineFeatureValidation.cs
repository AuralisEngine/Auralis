using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Validates optional pipeline state against actually loaded OpenGL extension wrappers.</summary>
internal static class OpenGLPipelineFeatureValidation
{
    /// <summary>Validates a requested depth-bias clamp against finite-value and extension support requirements.</summary>
    /// <param name="clamp">Depth-bias clamp value from pipeline state.</param>
    /// <param name="supported">Whether the required clamp feature and native entry points are available.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="clamp" /> is not finite.</exception>
    /// <exception cref="GraphicsFeatureNotSupportedException">A non-zero clamp is requested without feature support.</exception>
    internal static void ValidateDepthBiasClamp(float clamp, bool supported)
    {
        if (!float.IsFinite(clamp))
            throw new ArgumentOutOfRangeException(nameof(clamp));
        if (clamp != 0 && !supported)
            throw new GraphicsFeatureNotSupportedException(
                GraphicsFeature.DepthBiasClamp,
                GraphicsFeature.DepthBiasClamp);
    }

    /// <summary>Checks that enabled depth-bounds testing is supported by the active context.</summary>
    /// <param name="enabled">Whether the pipeline requests depth-bounds testing.</param>
    /// <param name="supported">Whether the extension and required wrapper are available.</param>
    /// <exception cref="GraphicsFeatureNotSupportedException">Depth-bounds testing is enabled but unavailable.</exception>
    internal static void ValidateDepthBounds(bool enabled, bool supported)
    {
        if (enabled && !supported)
            throw new GraphicsFeatureNotSupportedException(
                GraphicsFeature.DepthBoundsTest,
                GraphicsFeature.DepthBoundsTest);
    }
}
