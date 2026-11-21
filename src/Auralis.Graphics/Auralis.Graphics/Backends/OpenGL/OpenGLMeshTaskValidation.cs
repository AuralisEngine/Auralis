using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Validates EXT_mesh_shader work-group counts against driver-reported limits.</summary>
internal static class OpenGLMeshTaskValidation
{
    internal static bool IsSupported(
        bool extensionAvailable,
        uint maximumX,
        uint maximumY,
        uint maximumZ) =>
        extensionAvailable && maximumX != 0 && maximumY != 0 && maximumZ != 0;

    internal static void Validate(
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint maximumX,
        uint maximumY,
        uint maximumZ,
        GraphicsFeature requiredFeature = GraphicsFeature.MeshShaders)
    {
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            throw new GraphicsValidationException("Mesh-task work-group counts must all be greater than zero.");

        if (maximumX == 0 || maximumY == 0 || maximumZ == 0)
            throw new GraphicsFeatureNotSupportedException(
                requiredFeature,
                requiredFeature);

        if (groupCountX > maximumX || groupCountY > maximumY || groupCountZ > maximumZ)
        {
            throw new GraphicsValidationException(
                $"Mesh-task work-group count ({groupCountX}, {groupCountY}, {groupCountZ}) exceeds " +
                $"the driver limits ({maximumX}, {maximumY}, {maximumZ}).");
        }
    }
}
