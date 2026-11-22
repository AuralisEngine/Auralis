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
    internal bool SupportsMeshShaders =>
        ExtMeshShaders || NvMeshShaders;

    internal bool SupportsTaskShaders =>
        ExtTaskShaders || NvTaskShaders;

    internal bool SupportsMeshShader(OpenGLMeshShaderDialect dialect) => dialect switch
    {
        OpenGLMeshShaderDialect.Ext => ExtMeshShaders,
        OpenGLMeshShaderDialect.Nv => NvMeshShaders,
        _ => false
    };

    internal bool SupportsTaskShader(OpenGLMeshShaderDialect dialect) => dialect switch
    {
        OpenGLMeshShaderDialect.Ext => ExtTaskShaders,
        OpenGLMeshShaderDialect.Nv => NvTaskShaders,
        _ => false
    };

    internal static OpenGLMeshShaderDialectCapabilities Detect(
        bool extExtensionPresent,
        bool nvExtensionPresent,
        OpenGLMeshShaderEntryPoints entryPoints,
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
        bool extApiAvailable = entryPoints.SupportsExtMeshShader(extExtensionPresent);
        bool nvApiAvailable = entryPoints.SupportsNvMeshShader(nvExtensionPresent);

        bool extMesh = OpenGLMeshTaskValidation.IsSupported(
            extApiAvailable, extMeshCountX, extMeshCountY, extMeshCountZ, extMeshTotalCount);
        bool extTask = OpenGLMeshTaskValidation.IsSupported(
            extApiAvailable, extTaskCountX, extTaskCountY, extTaskCountZ, extTaskTotalCount);

        // NV exposes a one-dimensional task/mesh dispatch count. Do not borrow EXT limits,
        // even when both extension names are advertised by the same context.
        bool nv = OpenGLMeshTaskValidation.IsSupported(
            nvApiAvailable, nvDrawMeshTasksCount, 1, 1, nvDrawMeshTasksCount);

        return new OpenGLMeshShaderDialectCapabilities(extMesh, extTask, nv, nv);
    }
}
