using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Independent desktop GL binding-point namespaces used by descriptor allocation.</summary>
internal enum OpenGLDescriptorNamespace : byte
{
    UniformBuffer,
    StorageBuffer,
    TextureUnit,
    ImageUnit
}

/// <summary>One descriptor-layout entry and its offset in the binding set's flattened value array.</summary>
internal readonly record struct OpenGLDescriptorEntry(
    GraphicsBindingLayoutEntry Description,
    int ValueOffset);

/// <summary>Maps one pipeline set/binding declaration to a contiguous range of native GL slots.</summary>
internal readonly record struct OpenGLDescriptorMapping(
    uint SetIndex,
    GraphicsBindingLayoutEntry Description,
    uint BaseSlot);

/// <summary>Validated desktop GL binding layout with flattened descriptor-array storage offsets.</summary>
/// <remarks>Only partially-bound flags are supported; other descriptor binding flags are rejected during construction.</remarks>
internal sealed class OpenGLGraphicsBindingLayout : GraphicsDeviceObject, IGraphicsBindingLayout
{
    private readonly OpenGLDescriptorEntry[] _entries;

    internal OpenGLGraphicsBindingLayout(
        IGraphicsDevice device,
        GraphicsBindingLayoutDescription description) : base(device)
    {
        ArgumentNullException.ThrowIfNull(description);
        description.Validate();

        _entries = new OpenGLDescriptorEntry[description.Entries.Length];
        int valueOffset = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            GraphicsBindingLayoutEntry entry = description.Entries[i];
            GraphicsBindingFlags unsupportedFlags =
                entry.Flags & ~GraphicsBindingFlags.PartiallyBound;
            if (unsupportedFlags != GraphicsBindingFlags.None)
                throw new GraphicsValidationException(
                    $"OpenGL does not implement descriptor binding flags ({unsupportedFlags}).");

            _entries[i] = new OpenGLDescriptorEntry(entry, valueOffset);
            valueOffset = checked(valueOffset + (int)entry.Count);
        }

        ValueCount = valueOffset;
        Description = description;
    }

    public GraphicsBindingLayoutDescription Description { get; }
    internal ReadOnlySpan<OpenGLDescriptorEntry> Entries => _entries;
    internal int ValueCount { get; }

    internal bool TryGetEntry(
        uint binding,
        out OpenGLDescriptorEntry result)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Description.Binding == binding)
            {
                result = _entries[i];
                return true;
            }
        }

        result = default;
        return false;
    }

    protected override void DisposeCore() { }
}

/// <summary>Combines device-owned binding layouts and resolves each descriptor to a deterministic GL binding-point range.</summary>
/// <remarks>Uniform buffers, storage buffers, sampled textures/samplers, and storage images use separate native slot namespaces, each checked against queried context limits.</remarks>
internal sealed class OpenGLGraphicsPipelineLayout : GraphicsDeviceObject, IGraphicsPipelineLayout
{
    private readonly IGraphicsBindingLayout[] _bindingLayouts;
    private readonly OpenGLDescriptorMapping[] _mappings;

    internal OpenGLGraphicsPipelineLayout(
        OpenGLGraphicsDevice device,
        ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts) : base(device)
    {
        _bindingLayouts = bindingLayouts.ToArray();
        for (int i = 0; i < _bindingLayouts.Length; i++)
        {
            if (_bindingLayouts[i] is not OpenGLGraphicsBindingLayout ||
                !ReferenceEquals(_bindingLayouts[i].Device, device) ||
                _bindingLayouts[i].IsDisposed)
                throw new GraphicsValidationException("Every pipeline binding layout must belong to this OpenGL device.");
        }

        _mappings = OpenGLDescriptorBindingAllocator.Allocate(
            _bindingLayouts,
            device.MaxUniformBufferBindings,
            device.MaxStorageBufferBindings,
            device.MaxTextureUnits,
            device.MaxImageUnits);
        BindingLayouts = _bindingLayouts;
    }

    public ReadOnlyMemory<IGraphicsBindingLayout> BindingLayouts { get; }

    internal bool TryGetMapping(
        uint setIndex,
        uint binding,
        out OpenGLDescriptorMapping mapping)
    {
        for (int i = 0; i < _mappings.Length; i++)
        {
            if (_mappings[i].SetIndex == setIndex && _mappings[i].Description.Binding == binding)
            {
                mapping = _mappings[i];
                return true;
            }
        }

        mapping = default;
        return false;
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

    protected override void DisposeCore() { }
}

/// <summary>Mutable descriptor value stored until a command list captures a binding-set snapshot.</summary>
internal struct OpenGLBindingValue
{
    internal IGraphicsBuffer? Buffer;
    internal ulong Offset;
    internal ulong Range;
    internal IGraphicsTextureView? Texture;
    internal IGraphicsSampler? Sampler;
}

/// <summary>Applies a recorded descriptor snapshot to the active GL program using the pipeline layout's assigned slots.</summary>
/// <remarks>Native binding is replayed during command execution, not when the public binding set is edited or snapshotted.</remarks>
internal static class OpenGLBindingSetBinder
{
    /// <summary>Binds each populated or partially-bound descriptor element to its allocated native GL slot.</summary>
    /// <param name="gl">Current desktop OpenGL dispatch object.</param>
    /// <param name="dsa">Direct-state-access wrapper for buffer and texture binding operations.</param>
    /// <param name="imageLoadStoreApi">Image load/store wrapper for storage-texture bindings.</param>
    /// <param name="layout">Binding layout describing descriptor kinds and array counts.</param>
    /// <param name="values">Immutable values captured by the command list.</param>
    /// <param name="pipelineLayout">Pipeline layout that allocated native slots.</param>
    /// <param name="setIndex">Pipeline set index being applied.</param>
    /// <exception cref="GraphicsValidationException">A mapping is missing or a required descriptor is incomplete.</exception>
        GL gl,
        ArbDirectStateAccess dsa,
        ArbShaderImageLoadStore imageLoadStoreApi,
        OpenGLGraphicsBindingLayout layout,
        ReadOnlySpan<OpenGLBindingValue> values,
        OpenGLGraphicsPipelineLayout pipelineLayout,
        uint setIndex)
    {
        ReadOnlySpan<OpenGLDescriptorEntry> entries = layout.Entries;
        for (int i = 0; i < entries.Length; i++)
        {
            OpenGLDescriptorEntry entry = entries[i];
            if (!pipelineLayout.TryGetMapping(setIndex, entry.Description.Binding, out OpenGLDescriptorMapping mapping))
                throw new GraphicsValidationException(
                    $"Pipeline layout does not contain set {setIndex}, binding {entry.Description.Binding}.");

            for (uint element = 0; element < entry.Description.Count; element++)
            {
                OpenGLBindingValue value = values[entry.ValueOffset + checked((int)element)];
                uint slot = checked(mapping.BaseSlot + element);
                bool partiallyBound = (entry.Description.Flags & GraphicsBindingFlags.PartiallyBound) != 0;

                if (IsMissing(value, entry.Description.Type))
                {
                    if (!partiallyBound)
                        throw new GraphicsValidationException(
                            $"Descriptor binding {entry.Description.Binding}[{element}] is not populated.");
                    Unbind(gl, dsa, imageLoadStoreApi, entry.Description.Type, slot);
                    continue;
                }

                switch (entry.Description.Type)
                {
                    case GraphicsDescriptorType.UniformBuffer:
                        gl.BindBufferRange(
                            BufferTargetARB.UniformBuffer,
                            slot,
                            ((OpenGLGraphicsBuffer)value.Buffer!).Handle,
                            checked((nint)value.Offset),
                            checked((nuint)value.Range));
                        break;

                    case GraphicsDescriptorType.StorageBuffer:
                        gl.BindBufferRange(
                            BufferTargetARB.ShaderStorageBuffer,
                            slot,
                            ((OpenGLGraphicsBuffer)value.Buffer!).Handle,
                            checked((nint)value.Offset),
                            checked((nuint)value.Range));
                        break;

                    case GraphicsDescriptorType.SampledTexture:
                        dsa.BindTextureUnit(slot, ((OpenGLGraphicsTextureView)value.Texture!).Handle);
                        gl.BindSampler(slot, 0);
                        break;

                    case GraphicsDescriptorType.StorageTexture:
                        BindStorageTexture(imageLoadStoreApi, slot, (OpenGLGraphicsTextureView)value.Texture!);
                        break;

                    case GraphicsDescriptorType.Sampler:
                        dsa.BindTextureUnit(slot, 0);
                        gl.BindSampler(slot, ((OpenGLGraphicsSampler)value.Sampler!).Handle);
                        break;

                    case GraphicsDescriptorType.CombinedTextureSampler:
                        dsa.BindTextureUnit(slot, ((OpenGLGraphicsTextureView)value.Texture!).Handle);
                        gl.BindSampler(slot, ((OpenGLGraphicsSampler)value.Sampler!).Handle);
                        break;

                    default:
                        throw new GraphicsValidationException($"Unsupported OpenGL descriptor type: {entry.Description.Type}.");
                }
            }
        }
    }

    private static bool IsMissing(OpenGLBindingValue value, GraphicsDescriptorType type) => type switch
    {
        GraphicsDescriptorType.UniformBuffer or GraphicsDescriptorType.StorageBuffer => value.Buffer is null,
        GraphicsDescriptorType.SampledTexture or GraphicsDescriptorType.StorageTexture => value.Texture is null,
        GraphicsDescriptorType.Sampler => value.Sampler is null,
        GraphicsDescriptorType.CombinedTextureSampler => value.Texture is null && value.Sampler is null,
        _ => throw new GraphicsValidationException($"Unsupported OpenGL descriptor type: {type}.")
    };

    private static void Unbind(
        GL gl,
        ArbDirectStateAccess dsa,
        ArbShaderImageLoadStore imageLoadStoreApi,
        GraphicsDescriptorType type,
        uint slot)
    {
        switch (type)
        {
            case GraphicsDescriptorType.UniformBuffer:
                gl.BindBufferBase(BufferTargetARB.UniformBuffer, slot, 0);
                break;

            case GraphicsDescriptorType.StorageBuffer:
                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, slot, 0);
                break;

            case GraphicsDescriptorType.SampledTexture:
            case GraphicsDescriptorType.Sampler:
            case GraphicsDescriptorType.CombinedTextureSampler:
                dsa.BindTextureUnit(slot, 0);
                gl.BindSampler(slot, 0);
                break;

            case GraphicsDescriptorType.StorageTexture:
                imageLoadStoreApi.BindImageTexture(
                    slot,
                    0,
                    0,
                    false,
                    0,
                    BufferAccessARB.ReadWrite,
                    (InternalFormat)SizedInternalFormat.Rgba8);
                break;

            default:
                throw new GraphicsValidationException($"Unsupported OpenGL descriptor type: {type}.");
        }
    }

    private static void BindStorageTexture(ArbShaderImageLoadStore imageLoadStoreApi, uint unit, OpenGLGraphicsTextureView view)
    {
        GraphicsTextureViewDescription description = view.Description;
        GraphicsTextureDescription texture = view.Texture.Description;
        GraphicsFormat format = description.Format == GraphicsFormat.Unknown
            ? texture.Format
            : description.Format;
        if (!OpenGLFormatCapabilities.TryGetInternalFormat(format, out SizedInternalFormat internalFormat, out _))
            throw new GraphicsValidationException($"Storage-image format {format} has no OpenGL mapping.");

        bool layered = description.ViewType is
            GraphicsTextureViewType.Texture1DArray or GraphicsTextureViewType.Texture2DArray or
            GraphicsTextureViewType.Texture2DMultisampleArray or GraphicsTextureViewType.Texture3D or
            GraphicsTextureViewType.Cube or GraphicsTextureViewType.CubeArray;

        imageLoadStoreApi.BindImageTexture(
            unit,
            view.Handle,
            0,
            layered,
            0,
            BufferAccessARB.ReadWrite,
            (InternalFormat)internalFormat);
    }
}

/// <summary>Thread-safe mutable desktop GL descriptor set whose values are copied into command-list snapshots.</summary>
/// <remarks>The set stores managed references; recording captures its current values and the command list acquires native references needed through submission.</remarks>
internal sealed class OpenGLGraphicsBindingSet : GraphicsDeviceObject, IGraphicsBindingSet
{
    private readonly OpenGLGraphicsBindingLayout _layout;
    private readonly OpenGLBindingValue[] _values;
    private readonly Lock _gate = new();

    internal OpenGLGraphicsBindingSet(OpenGLGraphicsBindingLayout layout) : base(layout.Device)
    {
        _layout = layout;
        Layout = layout;
        _values = new OpenGLBindingValue[layout.ValueCount];
    }

    public IGraphicsBindingLayout Layout { get; }

    /// <summary>Sets a uniform- or storage-buffer descriptor element.</summary>
    /// <param name="binding">Layout binding number.</param>
    /// <param name="arrayElement">Zero-based element within the binding array.</param>
    /// <param name="buffer">Same-device buffer with Constant or Storage usage as required by the descriptor kind.</param>
    /// <param name="offset">Byte offset aligned to the applicable uniform/storage buffer limit.</param>
    /// <param name="range">Byte range, or <see cref="ulong.MaxValue" /> to use the remaining bytes.</param>
    /// <exception cref="GraphicsValidationException">The binding kind, device ownership, usage, or offset alignment is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The array element, offset, or range is outside the declared binding or buffer.</exception>
        uint binding,
        uint arrayElement,
        IGraphicsBuffer buffer,
        ulong offset = 0,
        ulong range = ulong.MaxValue)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        EnsureOwned(buffer);

        if (!_layout.TryGetEntry(binding, out OpenGLDescriptorEntry entry))
            throw new GraphicsValidationException($"Binding {binding} is not declared in this descriptor layout.");
        if (entry.Description.Type is not (GraphicsDescriptorType.UniformBuffer or GraphicsDescriptorType.StorageBuffer))
            throw new GraphicsValidationException($"Binding {binding} is not a buffer descriptor.");
        if (arrayElement >= entry.Description.Count)
            throw new ArgumentOutOfRangeException(nameof(arrayElement));

        GraphicsBufferUsage requiredUsage = entry.Description.Type == GraphicsDescriptorType.UniformBuffer
            ? GraphicsBufferUsage.Constant
            : GraphicsBufferUsage.Storage;
        if ((buffer.Description.Usage & requiredUsage) == 0)
            throw new GraphicsValidationException($"The buffer does not declare required usage {requiredUsage}.");

        if (offset > buffer.Description.SizeInBytes)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (range == ulong.MaxValue)
            range = buffer.Description.SizeInBytes - offset;
        if (range == 0 || range > buffer.Description.SizeInBytes - offset)
            throw new ArgumentOutOfRangeException(nameof(range));

        uint alignment = entry.Description.Type == GraphicsDescriptorType.UniformBuffer
            ? ((OpenGLGraphicsDevice)Device).UniformBufferOffsetAlignment
            : ((OpenGLGraphicsDevice)Device).StorageBufferOffsetAlignment;
        if (alignment > 1 && offset % alignment != 0)
            throw new GraphicsValidationException($"Buffer offset {offset} does not satisfy the OpenGL alignment {alignment}.");

        OpenGLBindingValue value = new()
        {
            Buffer = buffer,
            Offset = offset,
            Range = range
        };
        lock (_gate)
            _values[entry.ValueOffset + checked((int)arrayElement)] = value;
    }

    /// <summary>Sets a sampled, storage, or combined-texture descriptor element.</summary>
    /// <param name="binding">Layout binding number.</param>
    /// <param name="arrayElement">Zero-based element within the binding array.</param>
    /// <param name="textureView">Same-device view whose parent texture usage is compatible with the descriptor kind.</param>
    /// <exception cref="GraphicsValidationException">The binding kind, device ownership, or parent texture usage is incompatible.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The array element is outside the declared binding.</exception>
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(textureView);
        EnsureOwned(textureView);

        if (!_layout.TryGetEntry(binding, out OpenGLDescriptorEntry entry))
            throw new GraphicsValidationException($"Binding {binding} is not declared in this descriptor layout.");
        if (entry.Description.Type is not (GraphicsDescriptorType.SampledTexture or
            GraphicsDescriptorType.StorageTexture or GraphicsDescriptorType.CombinedTextureSampler))
            throw new GraphicsValidationException($"Binding {binding} is not a texture descriptor.");
        if (arrayElement >= entry.Description.Count)
            throw new ArgumentOutOfRangeException(nameof(arrayElement));

        GraphicsTextureUsage requiredUsage = entry.Description.Type == GraphicsDescriptorType.StorageTexture
            ? GraphicsTextureUsage.Storage
            : GraphicsTextureUsage.Sampled;
        if ((textureView.Texture.Description.Usage & requiredUsage) == 0)
            throw new GraphicsValidationException($"The texture does not declare required usage {requiredUsage}.");

        OpenGLBindingValue value;
        lock (_gate)
        {
            int index = entry.ValueOffset + checked((int)arrayElement);
            value = _values[index];
            value.Texture = textureView;
            _values[index] = value;
        }
    }

    /// <summary>Sets a sampler or combined-texture-sampler descriptor element.</summary>
    /// <param name="binding">Layout binding number.</param>
    /// <param name="arrayElement">Zero-based element within the binding array.</param>
    /// <param name="sampler">Live sampler created by the same device.</param>
    /// <exception cref="GraphicsValidationException">The binding kind or sampler device ownership is incompatible.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The array element is outside the declared binding.</exception>
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sampler);
        EnsureOwned(sampler);

        if (!_layout.TryGetEntry(binding, out OpenGLDescriptorEntry entry))
            throw new GraphicsValidationException($"Binding {binding} is not declared in this descriptor layout.");
        if (entry.Description.Type is not (GraphicsDescriptorType.Sampler or GraphicsDescriptorType.CombinedTextureSampler))
            throw new GraphicsValidationException($"Binding {binding} is not a sampler descriptor.");
        if (arrayElement >= entry.Description.Count)
            throw new ArgumentOutOfRangeException(nameof(arrayElement));

        OpenGLBindingValue value;
        lock (_gate)
        {
            int index = entry.ValueOffset + checked((int)arrayElement);
            value = _values[index];
            value.Sampler = sampler;
            _values[index] = value;
        }
    }

    internal OpenGLGraphicsBindingLayout OpenGLLayout => _layout;
    internal int ValueCount => _values.Length;

    /// <summary>
    /// Copies a validated immutable snapshot into command-list-owned reusable storage. This avoids
    /// allocating a snapshot object and cloned array for every descriptor-set recording operation.
    /// </summary>
    /// <summary>Validates descriptor completeness and appends a stable value snapshot to command-list-owned storage.</summary>
    /// <param name="destination">Reusable destination list owned by the command recorder.</param>
    /// <returns>Starting element offset of the appended snapshot.</returns>
    /// <exception cref="GraphicsValidationException">A required descriptor is missing or a partially-bound descriptor is only partly populated.</exception>
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            ThrowIfDisposed();
            ReadOnlySpan<OpenGLDescriptorEntry> entries = _layout.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                OpenGLDescriptorEntry entry = entries[i];
                for (int element = 0; element < entry.Description.Count; element++)
                {
                    OpenGLBindingValue value = _values[entry.ValueOffset + element];
                    bool valid = entry.Description.Type switch
                    {
                        GraphicsDescriptorType.UniformBuffer or GraphicsDescriptorType.StorageBuffer =>
                            value.Buffer is not null,
                        GraphicsDescriptorType.SampledTexture or GraphicsDescriptorType.StorageTexture =>
                            value.Texture is not null,
                        GraphicsDescriptorType.Sampler =>
                            value.Sampler is not null,
                        GraphicsDescriptorType.CombinedTextureSampler =>
                            value.Texture is not null && value.Sampler is not null,
                        _ => false
                    };
                    bool completelyEmpty = entry.Description.Type switch
                    {
                        GraphicsDescriptorType.UniformBuffer or GraphicsDescriptorType.StorageBuffer =>
                            value.Buffer is null,
                        GraphicsDescriptorType.SampledTexture or GraphicsDescriptorType.StorageTexture =>
                            value.Texture is null,
                        GraphicsDescriptorType.Sampler =>
                            value.Sampler is null,
                        GraphicsDescriptorType.CombinedTextureSampler =>
                            value.Texture is null && value.Sampler is null,
                        _ => false
                    };

                    if (!valid &&
                        !((entry.Description.Flags & GraphicsBindingFlags.PartiallyBound) != 0 && completelyEmpty))
                    {
                        throw new GraphicsValidationException(
                            $"Descriptor binding {entry.Description.Binding}[{element}] is not populated with a complete descriptor.");
                    }
                }
            }

            int offset = destination.Count;
            destination.AddRange(_values);
            return offset;
        }
    }

    private void EnsureOwned(IGraphicsDeviceObject resource)
    {
        if (resource.IsDisposed || resource.Device.IsDisposed)
            throw new ObjectDisposedException(resource.GetType().Name);
        if (!ReferenceEquals(resource.Device, Device))
            throw new GraphicsValidationException("Descriptor resources must belong to the binding set's device.");
    }

    protected override void DisposeCore()
    {
        lock (_gate)
            Array.Clear(_values);
    }
}
