using Auralis.Graphics.Commands;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Pipelines;

/// <summary>Runtime descriptor count for a variable-count binding in a layout.</summary>
public readonly record struct GraphicsVariableDescriptorCount(uint Binding, uint Count)
{
    public void Validate()
    {
        if (Count == 0)
            throw new ArgumentOutOfRangeException(nameof(Count));
    }
}

/// <summary>
/// Optional device extension for descriptor indexing and bindless binding sets.
/// The supplied counts must match exactly the variable-count bindings in the layout.
/// </summary>
public interface IGraphicsDescriptorIndexingDevice
{
    IGraphicsBindingSet CreateBindingSet(
        IGraphicsBindingLayout layout,
        ReadOnlySpan<GraphicsVariableDescriptorCount> variableCounts);
}

/// <summary>Optional binding-set extension for descriptors that combine a sampled image and sampler.</summary>
public interface IGraphicsCombinedBindingSet : IGraphicsBindingSet
{
    void SetCombinedTextureSampler(
        uint binding,
        uint arrayElement,
        IGraphicsTextureView textureView,
        IGraphicsSampler sampler);
}

/// <summary>
/// One dynamic buffer offset applied when a binding set is bound. Ordering follows the layout's
/// binding number and array element order for dynamic descriptors.
/// </summary>
public readonly record struct GraphicsDynamicBufferOffset(
    uint Binding,
    uint ArrayElement,
    ulong Offset);

/// <summary>Optional command-list extension for binding dynamic uniform/storage buffer offsets.</summary>
public interface IGraphicsDynamicBindingCommandList : IGraphicsCommandList
{
    void SetBindingSet(
        uint setIndex,
        IGraphicsBindingSet bindingSet,
        ReadOnlySpan<GraphicsDynamicBufferOffset> dynamicOffsets);
}
