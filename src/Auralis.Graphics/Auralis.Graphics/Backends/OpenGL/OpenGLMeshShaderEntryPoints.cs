namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Captures the native draw entry points exposed by one current desktop OpenGL context.
/// Extension presence and each draw path are deliberately tracked independently: a missing
/// multi-draw function must not disable direct or single-indirect mesh-task drawing.
/// </summary>
internal readonly record struct OpenGLMeshShaderEntryPoints(
    bool ExtDirect,
    bool ExtIndirect,
    bool ExtMultiDrawIndirect,
    bool NvDirect)
{
    internal static OpenGLMeshShaderEntryPoints Resolve(Func<string, nint> getProcAddress)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);

        return new OpenGLMeshShaderEntryPoints(
            getProcAddress("glDrawMeshTasksEXT") != 0,
            getProcAddress("glDrawMeshTasksIndirectEXT") != 0,
            getProcAddress("glMultiDrawMeshTasksIndirectEXT") != 0,
            getProcAddress("glDrawMeshTasksNV") != 0);
    }

    /// <summary>Whether any implemented EXT mesh-task draw path can execute.</summary>
    internal bool SupportsExtMeshShader(bool extensionPresent) =>
        extensionPresent && (ExtDirect || ExtIndirect || ExtMultiDrawIndirect);

    internal bool SupportsExtDirectMeshTasks(bool extensionPresent) =>
        extensionPresent && ExtDirect;

    internal bool SupportsExtIndirectMeshTasks(bool extensionPresent) =>
        extensionPresent && ExtIndirect;

    /// <summary>Availability of the optional EXT multi-draw-indirect path.</summary>
    internal bool SupportsExtMultiDrawMeshTasksIndirect(bool extensionPresent) =>
        extensionPresent && ExtMultiDrawIndirect;

    /// <summary>NV has its own dialect and one native direct draw entry point.</summary>
    internal bool SupportsNvMeshShader(bool extensionPresent) =>
        extensionPresent && NvDirect;
}
