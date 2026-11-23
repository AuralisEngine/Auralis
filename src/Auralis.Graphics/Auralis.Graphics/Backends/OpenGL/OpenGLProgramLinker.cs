using System.Runtime.InteropServices;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Links backend-owned shader objects into a single OpenGL program.</summary>
internal static class OpenGLProgramLinker
{
    /// <summary>Links one live backend-owned shader into a new native GL program.</summary>
    /// <param name="gl">Current desktop OpenGL dispatch object.</param>
    /// <param name="shader">Shader created by this backend.</param>
    /// <returns>The linked program name, owned by the caller.</returns>
    /// <exception cref="GraphicsValidationException">The shader is not a live OpenGL shader object.</exception>
    /// <exception cref="GraphicsException">Program creation or linking fails; the temporary program is deleted.</exception>
        Link(gl, MemoryMarshal.CreateReadOnlySpan(ref shader, 1));

    /// <summary>Links an ordered collection of live backend-owned shader stages into one native GL program.</summary>
    /// <param name="gl">Current desktop OpenGL dispatch object.</param>
    /// <param name="shaders">Shaders to attach before linking.</param>
    /// <returns>The linked program name, owned by the caller.</returns>
    /// <exception cref="GraphicsValidationException">A shader is not a live OpenGL shader object.</exception>
    /// <exception cref="GraphicsException">Program creation or linking fails; the temporary program is deleted.</exception>
    {
        uint program = gl.CreateProgram();
        if (program == 0)
            throw new GraphicsException("OpenGL failed to create a shader program.");

        try
        {
            for (int i = 0; i < shaders.Length; i++)
            {
                if (shaders[i] is not OpenGLGraphicsShader shader || shader.IsDisposed)
                    throw new GraphicsValidationException("All program shaders must be live objects from this OpenGL device.");
                gl.AttachShader(program, shader.Handle);
            }

            gl.LinkProgram(program);
            gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
            if (linked == 0)
                throw new GraphicsException($"OpenGL shader program link failed:\n{gl.GetProgramInfoLog(program)}");

            for (int i = 0; i < shaders.Length; i++)
                gl.DetachShader(program, ((OpenGLGraphicsShader)shaders[i]).Handle);

            return program;
        }
        catch
        {
            gl.DeleteProgram(program);
            throw;
        }
    }
}
