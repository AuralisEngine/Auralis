using System.Buffers;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Pipelines;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Assigns deterministic OpenGL binding points. Sets are processed in declared order and
/// bindings within each set in ascending binding order. Each GL binding namespace has its own
/// monotonically increasing slot sequence.
/// </summary>
internal static class OpenGLDescriptorBindingAllocator
{
    /// <summary>Assigns deterministic GL binding points to the declared descriptor sets and bindings.</summary>
    /// <param name="layouts">Layouts in pipeline set order; bindings within each layout are sorted by binding number.</param>
    /// <param name="maxUniformBufferBindings">Maximum uniform-buffer binding points reported by the context.</param>
    /// <param name="maxStorageBufferBindings">Maximum shader-storage-buffer binding points.</param>
    /// <param name="maxTextureUnits">Maximum combined texture-unit slots available to sampled textures and samplers.</param>
    /// <param name="maxImageUnits">Maximum image-unit slots available to storage textures.</param>
    /// <returns>One mapping per binding entry, with a monotonically allocated slot range inside its GL namespace.</returns>
    /// <exception cref="GraphicsValidationException">A descriptor set uses more slots in a namespace than the context exposes.</exception>
    /// <exception cref="OverflowException">The number of mapping entries or slot arithmetic exceeds the supported integer range.</exception>
    internal static OpenGLDescriptorMapping[] Allocate(
        ReadOnlySpan<IGraphicsBindingLayout> layouts,
        uint maxUniformBufferBindings,
        uint maxStorageBufferBindings,
        uint maxTextureUnits,
        uint maxImageUnits)
    {
        uint uniformSlot = 0;
        uint storageSlot = 0;
        uint textureUnit = 0;
        uint imageUnit = 0;

        int mappingCount = 0;
        int scratchLength = 0;
        for (int setIndex = 0; setIndex < layouts.Length; setIndex++)
        {
            OpenGLGraphicsBindingLayout layout = (OpenGLGraphicsBindingLayout)layouts[setIndex];
            ReadOnlySpan<GraphicsBindingLayoutEntry> entries = layout.Description.Entries;
            mappingCount = checked(mappingCount + entries.Length);
            scratchLength = Math.Max(scratchLength, entries.Length);
        }

        OpenGLDescriptorMapping[] mappings = new OpenGLDescriptorMapping[mappingCount];
        Span<GraphicsBindingLayoutEntry> stackScratch = stackalloc GraphicsBindingLayoutEntry[32];
        GraphicsBindingLayoutEntry[]? rentedScratch = null;
        Span<GraphicsBindingLayoutEntry> scratch = stackScratch;
        if (scratchLength > stackScratch.Length)
        {
            rentedScratch = ArrayPool<GraphicsBindingLayoutEntry>.Shared.Rent(scratchLength);
            scratch = rentedScratch;
        }

        try
        {
            int mappingIndex = 0;
            for (uint setIndex = 0; setIndex < layouts.Length; setIndex++)
            {
                OpenGLGraphicsBindingLayout layout = (OpenGLGraphicsBindingLayout)layouts[(int)setIndex];
                ReadOnlySpan<GraphicsBindingLayoutEntry> entries = layout.Description.Entries;
                Span<GraphicsBindingLayoutEntry> ordered = scratch[..entries.Length];
                entries.CopyTo(ordered);
                SortByBinding(ordered);

                for (int i = 0; i < ordered.Length; i++)
                {
                    GraphicsBindingLayoutEntry entry = ordered[i];
                    OpenGLDescriptorNamespace descriptorNamespace = GetNamespace(entry.Type);
                    uint nextSlot = descriptorNamespace switch
                    {
                        OpenGLDescriptorNamespace.UniformBuffer => uniformSlot,
                        OpenGLDescriptorNamespace.StorageBuffer => storageSlot,
                        OpenGLDescriptorNamespace.TextureUnit => textureUnit,
                        _ => imageUnit
                    };

                    uint maximum = descriptorNamespace switch
                    {
                        OpenGLDescriptorNamespace.UniformBuffer => maxUniformBufferBindings,
                        OpenGLDescriptorNamespace.StorageBuffer => maxStorageBufferBindings,
                        OpenGLDescriptorNamespace.TextureUnit => maxTextureUnits,
                        _ => maxImageUnits
                    };

                    if (nextSlot > maximum || entry.Count > maximum - nextSlot)
                        throw new GraphicsValidationException(
                            $"Descriptor set {setIndex}, binding {entry.Binding} exceeds the OpenGL {descriptorNamespace} binding-point limit ({maximum}).");

                    mappings[mappingIndex++] = new OpenGLDescriptorMapping(setIndex, entry, nextSlot);
                    uint nextValue = checked(nextSlot + entry.Count);
                    switch (descriptorNamespace)
                    {
                        case OpenGLDescriptorNamespace.UniformBuffer: uniformSlot = nextValue; break;
                        case OpenGLDescriptorNamespace.StorageBuffer: storageSlot = nextValue; break;
                        case OpenGLDescriptorNamespace.TextureUnit: textureUnit = nextValue; break;
                        case OpenGLDescriptorNamespace.ImageUnit: imageUnit = nextValue; break;
                    }
                }
            }

            return mappings;
        }
        finally
        {
            if (rentedScratch is not null)
                ArrayPool<GraphicsBindingLayoutEntry>.Shared.Return(rentedScratch);
        }
    }

    private static void SortByBinding(Span<GraphicsBindingLayoutEntry> entries)
    {
        for (int i = 1; i < entries.Length; i++)
        {
            GraphicsBindingLayoutEntry value = entries[i];
            int index = i;
            while (index > 0 && entries[index - 1].Binding > value.Binding)
            {
                entries[index] = entries[index - 1];
                index--;
            }
            entries[index] = value;
        }
    }

    private static OpenGLDescriptorNamespace GetNamespace(GraphicsDescriptorType type) => type switch
    {
        GraphicsDescriptorType.UniformBuffer => OpenGLDescriptorNamespace.UniformBuffer,
        GraphicsDescriptorType.StorageBuffer => OpenGLDescriptorNamespace.StorageBuffer,
        GraphicsDescriptorType.SampledTexture or GraphicsDescriptorType.Sampler or
            GraphicsDescriptorType.CombinedTextureSampler => OpenGLDescriptorNamespace.TextureUnit,
        GraphicsDescriptorType.StorageTexture => OpenGLDescriptorNamespace.ImageUnit,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
