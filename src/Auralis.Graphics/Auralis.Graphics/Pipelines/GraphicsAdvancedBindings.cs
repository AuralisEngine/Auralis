using Auralis.Graphics.Commands;
using Auralis.Graphics.Resources;

namespace Auralis.Graphics.Pipelines;

/// <summary>
/// Runtime descriptor count for a variable-count binding. Zero is structurally valid and represents
/// an empty variable descriptor array; device support is checked against enabled descriptor-indexing capabilities.
/// </summary>
public readonly record struct GraphicsVariableDescriptorCount(uint Binding, uint Count);

/// <summary>
/// Optional device extension for descriptor indexing and bindless binding sets. Supplied counts must match exactly
/// the variable-count bindings and not exceed each declared maximum. Implementations must use the device-aware
/// ValidateVariableCounts overload before allocation; zero requires the ZeroVariableDescriptorCount capability.
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

/// <summary>
/// Optional command-list extension for binding dynamic uniform/storage buffer offsets. Implementations
/// should validate offset order and use the device-limits overload of
/// GraphicsBindingLayoutDescription.ValidateDynamicOffsets before recording; effective buffer bounds
/// still require the binding set's base offsets and ranges and must be checked at bind time.
/// </summary>
public interface IGraphicsDynamicBindingCommandList : IGraphicsCommandList
{
    void SetBindingSet(
        uint setIndex,
        IGraphicsBindingSet bindingSet,
        ReadOnlySpan<GraphicsDynamicBufferOffset> dynamicOffsets);
}
