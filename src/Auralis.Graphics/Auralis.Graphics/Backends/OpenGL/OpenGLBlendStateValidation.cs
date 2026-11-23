using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Pipelines;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Validates blend-state differences that need indexed OpenGL draw-buffer state.</summary>
internal static class OpenGLBlendStateValidation
{
    /// <summary>Checks whether per-attachment blend-enable or color-write-mask differences can be represented by the context.</summary>
    /// <param name="attachments">Blend states in color-attachment order.</param>
    /// <param name="colorTargetCount">Number of active color targets.</param>
    /// <param name="supportsIndependentBlend">Whether indexed draw-buffer blend state is available.</param>
    /// <exception cref="GraphicsFeatureNotSupportedException">A later attachment differs in blend enablement or write mask and independent blend is unavailable.</exception>
    internal static void Validate(
        ReadOnlySpan<GraphicsBlendAttachmentState> attachments,
        int colorTargetCount,
        bool supportsIndependentBlend)
    {
        if (colorTargetCount <= 1 || attachments.IsEmpty)
            return;

        GraphicsBlendAttachmentState first = attachments[0];
        for (int i = 1; i < colorTargetCount; i++)
        {
            GraphicsBlendAttachmentState current = attachments[i];
            if (current.BlendEnabled != first.BlendEnabled ||
                current.WriteMask != first.WriteMask)
            {
                if (!supportsIndependentBlend)
                    throw new GraphicsFeatureNotSupportedException(
                        GraphicsFeature.IndependentBlend,
                        GraphicsFeature.IndependentBlend);
                return;
            }
        }
    }
}
