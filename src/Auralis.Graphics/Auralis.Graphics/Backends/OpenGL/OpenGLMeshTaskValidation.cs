using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Validates mesh/task work-group counts against the active extension's driver-reported limits.</summary>
internal static class OpenGLMeshTaskValidation
{
    /// <summary>Returns whether an advertised mesh/task API has complete, non-zero dispatch limits.</summary>
    /// <param name="extensionAvailable">Whether the exact extension API is advertised and available for use.</param>
    /// <param name="maximumX">Maximum work-group count on the X axis.</param>
    /// <param name="maximumY">Maximum work-group count on the Y axis.</param>
    /// <param name="maximumZ">Maximum work-group count on the Z axis.</param>
    /// <param name="maximumTotalCount">Maximum product of the three work-group dimensions.</param>
    /// <returns><see langword="true" /> only when the extension and every required limit are usable.</returns>
    internal static bool IsSupported(
        bool extensionAvailable,
        uint maximumX,
        uint maximumY,
        uint maximumZ,
        uint maximumTotalCount) =>
        extensionAvailable &&
        maximumX != 0 && maximumY != 0 && maximumZ != 0 &&
        maximumTotalCount != 0;

    /// <summary>Validates a non-zero mesh/task dispatch against per-axis and total driver limits.</summary>
    /// <param name="groupCountX">Requested work-group count on X.</param>
    /// <param name="groupCountY">Requested work-group count on Y.</param>
    /// <param name="groupCountZ">Requested work-group count on Z.</param>
    /// <param name="maximumX">Driver-reported X-axis limit.</param>
    /// <param name="maximumY">Driver-reported Y-axis limit.</param>
    /// <param name="maximumZ">Driver-reported Z-axis limit.</param>
    /// <param name="maximumTotalCount">Driver-reported maximum total work-group count.</param>
    /// <param name="requiredFeature">Feature to report when usable limits are absent.</param>
    /// <exception cref="GraphicsValidationException">A requested dimension is zero, exceeds an axis limit, or the total group count exceeds the driver limit.</exception>
    /// <exception cref="GraphicsFeatureNotSupportedException">One or more required driver limits are unavailable.</exception>
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

/// <summary>Identifies the GLSL mesh/task shader dialect whose source and dispatch contract are in use.</summary>
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
    /// <summary>Requires exactly one enabled EXT or NV mesh-shader directive and verifies that dialect is available.</summary>
    /// <param name="source">GLSL source to inspect.</param>
    /// <param name="extAvailable">Whether the EXT extension and usable dispatch API are available.</param>
    /// <param name="nvAvailable">Whether the NV extension and usable dispatch API are available.</param>
    /// <param name="requiredFeature">Feature reported when the selected dialect is unsupported.</param>
    /// <returns>The selected dialect, which determines shader and indirect-command semantics.</returns>
    /// <exception cref="GraphicsValidationException">The directives are ambiguous or do not enable exactly one dialect.</exception>
    /// <exception cref="GraphicsFeatureNotSupportedException">The selected dialect is not supported by the active context.</exception>
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
