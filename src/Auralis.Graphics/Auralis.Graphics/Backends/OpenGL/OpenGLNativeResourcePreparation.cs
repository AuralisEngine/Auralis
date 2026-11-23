namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Runs every resource preparation hook needed before desktop GL native teardown.</summary>
internal static class OpenGLNativeResourcePreparation
{
    /// <summary>
    /// Attempts every preparation hook and returns all failures. Native teardown must not begin
    /// when this method reports failures; the caller may retry preparation later.
    /// </summary>
    internal static List<Exception>? PrepareAll(IOpenGLNativeResource[]? resources, int resourceCount)
    {
        if (resourceCount < 0 || resources is null && resourceCount != 0 ||
            resources is not null && resourceCount > resources.Length)
            throw new ArgumentOutOfRangeException(nameof(resourceCount));

        List<Exception>? failures = null;
        for (int i = 0; i < resourceCount; i++)
        {
            if (resources![i] is not IOpenGLNativeResourcePreparation preparation)
                continue;

            try
            {
                preparation.PrepareForNativeRelease();
            }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>(2)).Add(exception);
            }
        }

        return failures;
    }
}
