using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Validates compute dispatch group counts against the active context's indexed limits.</summary>
internal static class OpenGLComputeDispatchValidation
{
    /// <summary>Validates compute work-group counts against the three limits queried from the current context.</summary>
    /// <param name="groupCountX">Requested number of work groups along X.</param>
    /// <param name="groupCountY">Requested number of work groups along Y.</param>
    /// <param name="groupCountZ">Requested number of work groups along Z.</param>
    /// <param name="maxGroupCountX">Driver-reported X-axis maximum.</param>
    /// <param name="maxGroupCountY">Driver-reported Y-axis maximum.</param>
    /// <param name="maxGroupCountZ">Driver-reported Z-axis maximum.</param>
    /// <remarks>A zero count on any axis is accepted as a no-op and does not require non-zero queried limits.</remarks>
    /// <exception cref="GraphicsValidationException">A non-zero dispatch lacks usable queried limits or exceeds an axis limit.</exception>
    internal static void Validate(
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint maxGroupCountX,
        uint maxGroupCountY,
        uint maxGroupCountZ)
    {
        // OpenGL defines a zero count in any dimension as a valid no-op.
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            return;

        if (maxGroupCountX == 0 || maxGroupCountY == 0 || maxGroupCountZ == 0)
            throw new GraphicsValidationException(
                "The OpenGL device did not report valid compute dispatch-count limits.");

        if (groupCountX > maxGroupCountX ||
            groupCountY > maxGroupCountY ||
            groupCountZ > maxGroupCountZ)
        {
            throw new GraphicsValidationException(
                $"Compute dispatch ({groupCountX}, {groupCountY}, {groupCountZ}) exceeds the OpenGL work-group count limits " +
                $"({maxGroupCountX}, {maxGroupCountY}, {maxGroupCountZ}).");
        }
    }
}
