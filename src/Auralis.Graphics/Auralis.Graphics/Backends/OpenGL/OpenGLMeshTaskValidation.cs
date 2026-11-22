using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Validates mesh/task work-group counts against the active extension's driver-reported limits.</summary>
internal static class OpenGLMeshTaskValidation
{
    internal static bool IsSupported(
        bool extensionAvailable,
        uint maximumX,
        uint maximumY,
        uint maximumZ,
        uint maximumTotalCount) =>
        extensionAvailable &&
        maximumX != 0 && maximumY != 0 && maximumZ != 0 &&
        maximumTotalCount != 0;

    internal static void Validate(
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint maximumX,
        uint maximumY,
        uint maximumZ,
        uint maximumTotalCount,
        GraphicsFeature requiredFeature = GraphicsFeature.MeshShaders)
    {
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            throw new GraphicsValidationException("Mesh-task work-group counts must all be greater than zero.");

        if (maximumX == 0 || maximumY == 0 || maximumZ == 0 || maximumTotalCount == 0)
            throw new GraphicsFeatureNotSupportedException(
                requiredFeature,
                requiredFeature);

        if (groupCountX > maximumX || groupCountY > maximumY || groupCountZ > maximumZ)
        {
            throw new GraphicsValidationException(
                $"Mesh-task work-group count ({groupCountX}, {groupCountY}, {groupCountZ}) exceeds " +
                $"the per-axis driver limits ({maximumX}, {maximumY}, {maximumZ}).");
        }

        UInt128 totalGroupCount = (UInt128)groupCountX * groupCountY * groupCountZ;
        if (totalGroupCount > maximumTotalCount)
        {
            throw new GraphicsValidationException(
                $"Mesh-task dispatch requests {totalGroupCount} total work groups; " +
                $"the driver limit is {maximumTotalCount}.");
        }
    }
}

internal enum OpenGLMeshShaderDialect : byte
{
    None,
    Ext,
    Nv
}

/// <summary>
/// Identifies and validates the GLSL mesh/task extension dialect selected by a shader source.
/// The EXT and NV stages have distinct GLSL contracts even though their OpenGL shader-stage
/// tokens use the same numeric values.
/// </summary>
internal static class OpenGLMeshShaderDialectValidation
{
    internal static OpenGLMeshShaderDialect ValidateGlslSource(
        string source,
        bool extAvailable,
        bool nvAvailable,
        GraphicsFeature requiredFeature)
    {
        ArgumentNullException.ThrowIfNull(source);
        GlslDirectiveScanner.ExtensionResult ext =
            GlslDirectiveScanner.FindExtension(source, "GL_EXT_mesh_shader");
        GlslDirectiveScanner.ExtensionResult nv =
            GlslDirectiveScanner.FindExtension(source, "GL_NV_mesh_shader");
        if (ext.Ambiguous || nv.Ambiguous)
            throw new GraphicsValidationException(
                "Mesh-shader extension directives in non-literal preprocessor branches are ambiguous. " +
                "Use an unconditional directive or a branch guarded by a literal #if 0/#if 1.");
        bool requestsExt = ext.Found && ext.Enabled;
        bool requestsNv = nv.Found && nv.Enabled;
        if (requestsExt == requestsNv)
        {
            throw new GraphicsValidationException(
                "A GLSL mesh/task shader must enable exactly one of GL_EXT_mesh_shader or GL_NV_mesh_shader. " +
                "The accepted enabled behaviors are : require, : enable, and : warn; : disable is not enabled.");
        }

        if (requestsExt)
        {
            if (!extAvailable)
                throw new GraphicsFeatureNotSupportedException(requiredFeature, requiredFeature);
            return OpenGLMeshShaderDialect.Ext;
        }

        if (!nvAvailable)
            throw new GraphicsFeatureNotSupportedException(requiredFeature, requiredFeature);
        return OpenGLMeshShaderDialect.Nv;
    }

    private static bool HasExtensionDirective(string source, string extension)
    {
        GlslDirectiveScanner.ExtensionResult result = GlslDirectiveScanner.FindExtension(source, extension);
        return result.Found && result.Enabled && !result.Ambiguous;
    }
}
