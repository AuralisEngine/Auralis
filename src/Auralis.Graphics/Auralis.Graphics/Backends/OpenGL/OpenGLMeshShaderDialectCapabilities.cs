namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Reports mesh and task support independently for the two native GLSL dialects.
/// The public graphics feature flags are the union of these capabilities, while shader and
/// pipeline validation must consult the exact dialect declared by the source.
/// </summary>
internal readonly record struct OpenGLMeshShaderDialectCapabilities(
    bool ExtMeshShaders,
    bool ExtTaskShaders,
    bool NvMeshShaders,
    bool NvTaskShaders)
{
    /// <summary>Gets whether at least one dialect has usable mesh-shader support.</summary>
    internal bool SupportsMeshShaders =>
        ExtMeshShaders || NvMeshShaders;

    /// <summary>Gets whether at least one dialect has usable task-shader support.</summary>
    internal bool SupportsTaskShaders =>
        ExtTaskShaders || NvTaskShaders;

    /// <summary>Checks mesh-stage support for the exact GLSL dialect selected by the shader.</summary>
    internal bool SupportsMeshShader(OpenGLMeshShaderDialect dialect) => dialect switch
    {
        OpenGLMeshShaderDialect.Ext => ExtMeshShaders,
        OpenGLMeshShaderDialect.Nv => NvMeshShaders,
        _ => false
    };

    /// <summary>Checks task-stage support for the exact GLSL dialect selected by the shader.</summary>
    internal bool SupportsTaskShader(OpenGLMeshShaderDialect dialect) => dialect switch
    {
        OpenGLMeshShaderDialect.Ext => ExtTaskShaders,
        OpenGLMeshShaderDialect.Nv => NvTaskShaders,
        _ => false
    };

    /// <summary>Builds independent EXT and NV support flags from advertised APIs and their own queried limits.</summary>
    /// <remarks>NV's one-dimensional maximum is never inferred from or substituted with EXT's three-dimensional limits.</remarks>
    internal static OpenGLMeshShaderDialectCapabilities Detect(
        bool extApiAvailable,
        bool nvApiAvailable,
        uint extMeshCountX,
        uint extMeshCountY,
        uint extMeshCountZ,
        uint extMeshTotalCount,
        uint extTaskCountX,
        uint extTaskCountY,
        uint extTaskCountZ,
        uint extTaskTotalCount,
        uint nvDrawMeshTasksCount)
    {

        bool extMesh = OpenGLMeshTaskValidation.IsSupported(
            extApiAvailable, extMeshCountX, extMeshCountY, extMeshCountZ, extMeshTotalCount);
        bool extTask = extMesh && OpenGLMeshTaskValidation.IsSupported(
            extApiAvailable, extTaskCountX, extTaskCountY, extTaskCountZ, extTaskTotalCount);

        // NV exposes a one-dimensional task/mesh dispatch count. Do not borrow EXT limits,
        // even when both extension names are advertised by the same context.
        bool nvMesh = OpenGLMeshTaskValidation.IsSupported(
            nvApiAvailable, nvDrawMeshTasksCount, 1, 1, nvDrawMeshTasksCount);
        bool nvTask = nvMesh;

        return new OpenGLMeshShaderDialectCapabilities(extMesh, extTask, nvMesh, nvTask);
    }
}
