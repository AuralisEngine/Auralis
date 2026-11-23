using System.Globalization;
using Auralis.Graphics.Abstractions;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>A parsed OpenGL ES major/minor version pair.</summary>
public readonly record struct OpenGLESVersion(int Major, int Minor) : IComparable<OpenGLESVersion>
{
    /// <summary>Gets the minimum GLES API version required by this backend.</summary>
    public static OpenGLESVersion RequiredBaseline { get; } = new(3, 1);

    /// <summary>Determines whether this version is at least the specified major/minor version.</summary>
    public bool IsAtLeast(int major, int minor) =>
        Major > major || (Major == major && Minor >= minor);

    /// <summary>Compares this version lexicographically by major and then minor version.</summary>
    public int CompareTo(OpenGLESVersion other)
    {
        int majorComparison = Major.CompareTo(other.Major);
        return majorComparison != 0 ? majorComparison : Minor.CompareTo(other.Minor);
    }

    /// <summary>
    /// Parses the API version from a GLES GL_VERSION string, for example
    /// "OpenGL ES 3.2 V@0502.0" or "OpenGL ES 3.1 Mesa 24.0".
    /// Desktop OpenGL and GLSL version strings are intentionally rejected.
    /// </summary>
    public static bool TryParse(string? versionString, out OpenGLESVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(versionString))
            return false;

        const string marker = "OpenGL ES";
        int markerIndex = versionString.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;

        int index = markerIndex + marker.Length;

        // Allow the optional legacy profile suffix ("-CM"), but do not scan arbitrary words:
        // otherwise a GLSL version embedded later in the same string could be mistaken for GL_VERSION.
        if (index < versionString.Length && versionString[index] == '-')
        {
            index++;
            int profileStart = index;
            while (index < versionString.Length && char.IsAsciiLetter(versionString[index]))
                index++;
            if (profileStart == index)
                return false;
        }

        while (index < versionString.Length && char.IsWhiteSpace(versionString[index]))
            index++;
        if (index >= versionString.Length || !char.IsAsciiDigit(versionString[index]))
            return false;

        int majorStart = index;
        while (index < versionString.Length && char.IsAsciiDigit(versionString[index]))
            index++;
        if (majorStart == index || index >= versionString.Length || versionString[index] != '.')
            return false;

        if (!int.TryParse(versionString.AsSpan(majorStart, index - majorStart),
                NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            return false;

        index++;
        int minorStart = index;
        while (index < versionString.Length && char.IsAsciiDigit(versionString[index]))
            index++;
        if (minorStart == index ||
            !int.TryParse(versionString.AsSpan(minorStart, index - minorStart),
                NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
            return false;

        if (major < 1 || minor < 0)
            return false;

        version = new OpenGLESVersion(major, minor);
        return true;
    }

    /// <summary>Parses a recognized OpenGL ES API version or throws when the string is not a GLES version.</summary>
    /// <param name="versionString">Driver-provided GL_VERSION string.</param>
    /// <returns>The parsed major/minor API version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="versionString" /> is <see langword="null" />.</exception>
    /// <exception cref="FormatException">The string does not contain a recognized OpenGL ES version prefix and numeric version.</exception>
    public static OpenGLESVersion Parse(string versionString)
    {
        ArgumentNullException.ThrowIfNull(versionString);
        return TryParse(versionString, out OpenGLESVersion version)
            ? version
            : throw new FormatException($"'{versionString}' is not a recognized OpenGL ES version string.");
    }

    /// <summary>Formats the version as <c>major.minor</c>.</summary>
    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>
/// Conservative capability calculation for an already-created GLES context. Version promotion is used
/// only for features guaranteed by the corresponding GLES core specification; extension-only features
/// require the exact extension name. Device-specific limits and format support must still be queried
/// from the active implementation before those capabilities are exposed by a device.
/// </summary>
public sealed class OpenGLESCapabilityProfile
{
    private readonly string[] _extensions;

    /// <summary>Gets the parsed API version of the supplied context.</summary>
    public OpenGLESVersion Version { get; }

    /// <summary>Gets features promoted by the core version or enabled by the exact advertised extensions and resolved entry points.</summary>
    public GraphicsFeatureSet Features { get; }

    /// <summary>Gets the logical queue roles exposed for the context, including presentation only when a drawable is available.</summary>
    public GraphicsQueueCapabilities QueueCapabilities { get; }

    /// <summary>Gets whether the context meets the GLES 3.1 minimum baseline.</summary>
    public bool MeetsRequiredBaseline => Version.CompareTo(OpenGLESVersion.RequiredBaseline) >= 0;

    private OpenGLESCapabilityProfile(
        OpenGLESVersion version,
        string[] extensions,
        GraphicsFeatureSet features,
        GraphicsQueueCapabilities queueCapabilities)
    {
        Version = version;
        _extensions = extensions;
        Features = features;
        QueueCapabilities = queueCapabilities;
    }

    /// <summary>Checks for an exact, case-sensitive extension token in the profile.</summary>
    /// <param name="extension">Extension name without surrounding whitespace.</param>
    /// <returns><see langword="true" /> when the exact extension was advertised.</returns>
    /// <exception cref="ArgumentException"><paramref name="extension" /> is empty or whitespace.</exception>
    public bool HasExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        for (int i = 0; i < _extensions.Length; i++)
        {
            if (string.Equals(_extensions[i], extension, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Throws when the supplied context is older than the backend's GLES 3.1 baseline.</summary>
    /// <exception cref="NotSupportedException">The API version is below GLES 3.1.</exception>
    public void EnsureRequiredBaseline()
    {
        if (!MeetsRequiredBaseline)
            throw new NotSupportedException(
                $"OpenGL ES {OpenGLESVersion.RequiredBaseline} or newer is required; the context reports {Version}.");
    }

    /// <summary>Creates an immutable profile from a GLES version, advertised extension list, and entry-point availability.</summary>
    /// <param name="version">Parsed GLES API version.</param>
    /// <param name="extensions">Exact extension tokens reported by the active context; the input is copied.</param>
    /// <param name="supportsPresentation">Whether the provider has a presentable host drawable.</param>
    /// <param name="multiDrawIndirectEntryPointsAvailable">Whether required native multi-draw indirect functions resolved.</param>
    /// <param name="indexedBlendEntryPointsAvailable">Whether indexed blend-state functions resolved.</param>
    /// <param name="baseVertexEntryPointsAvailable">Whether base-vertex draw functions resolved.</param>
    /// <param name="baseInstanceEntryPointsAvailable">Whether base-instance draw functions resolved.</param>
    /// <param name="textureViewEntryPointAvailable">Whether the matching native texture-view function resolved.</param>
    /// <param name="meshShaderEntryPointsAvailable">Whether the required mesh/task dispatch functions resolved.</param>
    /// <returns>A capability profile; device limits and format usage still require separate driver queries.</returns>
    public static OpenGLESCapabilityProfile Create(
        OpenGLESVersion version,
        ReadOnlySpan<string> extensions,
        bool supportsPresentation,
        bool multiDrawIndirectEntryPointsAvailable = true,
        bool indexedBlendEntryPointsAvailable = true,
        bool baseVertexEntryPointsAvailable = true,
        bool baseInstanceEntryPointsAvailable = true,
        bool textureViewEntryPointAvailable = true,
        bool meshShaderEntryPointsAvailable = true) =>
        CreateFromOwnedExtensions(
            version,
            extensions.ToArray(),
            supportsPresentation,
            multiDrawIndirectEntryPointsAvailable,
            indexedBlendEntryPointsAvailable,
            baseVertexEntryPointsAvailable,
            baseInstanceEntryPointsAvailable,
            textureViewEntryPointAvailable,
            meshShaderEntryPointsAvailable);

    internal static OpenGLESCapabilityProfile CreateFromOwnedExtensions(
        OpenGLESVersion version,
        string[] extensions,
        bool supportsPresentation,
        bool multiDrawIndirectEntryPointsAvailable = true,
        bool indexedBlendEntryPointsAvailable = true,
        bool baseVertexEntryPointsAvailable = true,
        bool baseInstanceEntryPointsAvailable = true,
        bool textureViewEntryPointAvailable = true,
        bool meshShaderEntryPointsAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (version.Major < 1 || version.Minor < 0)
            throw new ArgumentOutOfRangeException(nameof(version));

        // Ownership of this array transfers to the profile. Public Create continues to copy
        // caller-owned spans before entering this method.
        for (int i = 0; i < extensions.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(extensions[i]) ||
                extensions[i].IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
                throw new ArgumentException("Each GLES extension must be one non-empty token.", nameof(extensions));
            for (int j = 0; j < i; j++)
            {
                if (string.Equals(extensions[i], extensions[j], StringComparison.Ordinal))
                    throw new ArgumentException($"GLES extension '{extensions[i]}' is duplicated.", nameof(extensions));
            }
        }

        bool es31 = version.IsAtLeast(3, 1);
        bool es32 = version.IsAtLeast(3, 2);
        GraphicsFeature features = GraphicsFeature.None;

        if (version.IsAtLeast(3, 0))
            features |= GraphicsFeature.TimelineSynchronization;

        if (es31)
        {
            features |= GraphicsFeature.ComputeShaders | GraphicsFeature.IndirectDrawing;
        }

        if (es32)
        {
            features |= GraphicsFeature.GeometryShaders | GraphicsFeature.TessellationShaders;
        }

        if (Contains(extensions, "GL_EXT_texture_filter_anisotropic"))
            features |= GraphicsFeature.SamplerAnisotropy;

        // These features are exposed only when the exact GLES extension is advertised.
        if (multiDrawIndirectEntryPointsAvailable &&
            Contains(extensions, "GL_EXT_multi_draw_indirect"))
            features |= GraphicsFeature.MultiDrawIndirect;
        if (indexedBlendEntryPointsAvailable &&
            Contains(extensions, "GL_EXT_draw_buffers_indexed"))
            features |= GraphicsFeature.IndependentBlend;

        if (baseVertexEntryPointsAvailable &&
            Contains(extensions, "GL_EXT_draw_elements_base_vertex"))
            features |= GraphicsFeature.BaseVertexDrawing;
        if (baseInstanceEntryPointsAvailable &&
            Contains(extensions, "GL_EXT_base_instance") &&
            Contains(extensions, "GL_EXT_draw_elements_base_vertex"))
            features |= GraphicsFeature.BaseInstanceDrawing;
        if (textureViewEntryPointAvailable &&
            (Contains(extensions, "GL_EXT_texture_view") || Contains(extensions, "GL_OES_texture_view")))
            features |= GraphicsFeature.TextureViews;

        // EXT_mesh_shader defines both stages for GLES 3.1+ with independent driver limits.
        if (meshShaderEntryPointsAvailable && Contains(extensions, "GL_EXT_mesh_shader"))
            features |= GraphicsFeature.MeshShaders | GraphicsFeature.TaskShaders;

        GraphicsQueueCapabilities queues =
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Copy;
        if (es31)
            queues |= GraphicsQueueCapabilities.Compute;
        if (supportsPresentation)
            queues |= GraphicsQueueCapabilities.Present;

        return new OpenGLESCapabilityProfile(
            version,
            extensions,
            new GraphicsFeatureSet(features),
            queues);
    }

    private static bool Contains(string[] extensions, string target)
    {
        for (int i = 0; i < extensions.Length; i++)
        {
            if (string.Equals(extensions[i], target, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
