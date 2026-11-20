using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Validates compute dispatch group counts against the active context's indexed limits.</summary>
internal static class OpenGLComputeDispatchValidation
{
    internal static void Validate(
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint maxGroupCountX,
        uint maxGroupCountY,
        uint maxGroupCountZ)
    {
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            throw new GraphicsValidationException("Compute work-group counts must all be greater than zero.");

        if ((maxGroupCountX != 0 && groupCountX > maxGroupCountX) ||
            (maxGroupCountY != 0 && groupCountY > maxGroupCountY) ||
            (maxGroupCountZ != 0 && groupCountZ > maxGroupCountZ))
        {
            throw new GraphicsValidationException(
                $"Compute dispatch ({groupCountX}, {groupCountY}, {groupCountZ}) exceeds the OpenGL work-group count limits " +
                $"({maxGroupCountX}, {maxGroupCountY}, {maxGroupCountZ}).");
        }
    }
}
