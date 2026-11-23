using System.Buffers.Binary;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Validates the binary header and alignment required before uploading SPIR-V through <c>GL_ARB_gl_spirv</c>.</summary>
/// <remarks>
/// This validation does not parse instructions or identify the module execution model. In
/// particular, it cannot currently distinguish EXT from NV mesh/task shader execution models;
/// mesh/task SPIR-V therefore remains unsupported rather than being routed to an assumed dialect.
/// </remarks>
internal static class OpenGLSpirVValidation
{
    internal const uint MagicNumber = 0x07230203;
    internal const int MinimumHeaderSize = 20;

    /// <summary>Checks that SPIR-V is supported and that the supplied code has a valid minimum binary header.</summary>
    /// <param name="description">Shader description whose code span contains the SPIR-V module.</param>
    /// <param name="supported">Whether the active device has the required SPIR-V ingestion capability.</param>
    /// <exception cref="GraphicsFeatureNotSupportedException">The device does not support SPIR-V shader ingestion.</exception>
    /// <exception cref="GraphicsValidationException">The binary is shorter than the five-word header, is not four-byte aligned, or has an invalid magic number.</exception>
    internal static void Validate(in GraphicsShaderDescription description, bool supported)
    {
        if (!supported)
            throw new GraphicsFeatureNotSupportedException(
                GraphicsFeature.SpirVShaders,
                GraphicsFeature.SpirVShaders);

        ReadOnlySpan<byte> code = description.Code.Span;
        if (code.Length < MinimumHeaderSize || (code.Length & 3) != 0)
            throw new GraphicsValidationException(
                "SPIR-V shader binaries must contain a complete 20-byte header and be four-byte aligned.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(code);
        if (magic != MagicNumber)
            throw new GraphicsValidationException(
                $"The supplied shader binary has an invalid SPIR-V magic number (0x{magic:X8}).");
    }
}
