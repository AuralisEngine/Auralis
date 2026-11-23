using System.Buffers;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>One managed descriptor value captured into a command-list snapshot for later GLES binding.</summary>
internal readonly record struct OpenGLESBindingValue(
    IGraphicsBuffer? Buffer,
    ulong Offset,
    ulong Range,
    IGraphicsTextureView? Texture,
    IGraphicsSampler? Sampler);

/// <summary>Maps a descriptor set and binding to a native GLES slot.</summary>
internal readonly record struct OpenGLESDescriptorMapping(
    uint SetIndex,
    uint Binding,
    GraphicsDescriptorType Type,
    uint BaseSlot);

/// <summary>Validated GLES binding layout with precomputed offsets into flattened descriptor-value storage.</summary>
/// <remarks>Only partially-bound descriptor flags are implemented; other binding flags are rejected.</remarks>
internal sealed class OpenGLESGraphicsBindingLayout : GraphicsDeviceObject, IGraphicsBindingLayout
{
    private readonly int[] _offsets;
    private readonly int _valueCount;

    internal OpenGLESGraphicsBindingLayout(IGraphicsDevice device, GraphicsBindingLayoutDescription description)
        : base(device)
    {
        ArgumentNullException.ThrowIfNull(description);
        description.Validate();

        Description = description;
        ReadOnlySpan<GraphicsBindingLayoutEntry> entries = Description.Entries;
        _offsets = new int[entries.Length];
        int count = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            GraphicsBindingFlags unsupported = entries[i].Flags &
                ~(GraphicsBindingFlags.PartiallyBound);
            if (unsupported != GraphicsBindingFlags.None)
                throw new GraphicsValidationException(
                    $"OpenGL ES does not implement descriptor flags: {unsupported}.");
            _offsets[i] = count;
            count = checked(count + (int)entries[i].Count);
        }

        _valueCount = count;
    }

    public GraphicsBindingLayoutDescription Description { get; }

    internal ReadOnlySpan<GraphicsBindingLayoutEntry> Entries => Description.Entries;

    internal int ValueCount => _valueCount;

    internal bool TryGetEntry(uint binding, out GraphicsBindingLayoutEntry entry, out int offset)
    {
        ReadOnlySpan<GraphicsBindingLayoutEntry> entries = Description.Entries;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Binding == binding)
            {
                entry = entries[i];
                offset = _offsets[i];
                return true;
            }
        }

        entry = default;
        offset = -1;
        return false;
    }

    protected override void DisposeCore() { }
}

/// <summary>Combines GLES binding layouts and assigns deterministic native slots to descriptor elements.</summary>
/// <remarks>Uniform buffers, storage buffers, sampled textures/samplers, and storage images use independent slot ranges validated against queried GLES limits.</remarks>
internal sealed class OpenGLESGraphicsPipelineLayout : GraphicsDeviceObject, IGraphicsPipelineLayout
{
    private readonly IGraphicsBindingLayout[] _bindingLayouts;
    private readonly OpenGLESDescriptorMapping[] _mappings;

    internal OpenGLESGraphicsPipelineLayout(
        OpenGLESGraphicsDevice device,
        ReadOnlySpan<IGraphicsBindingLayout> bindingLayouts) : base(device)
    {
        _bindingLayouts = bindingLayouts.ToArray();
        for (int i = 0; i < _bindingLayouts.Length; i++)
        {
            if (_bindingLayouts[i] is not OpenGLESGraphicsBindingLayout ||
                !ReferenceEquals(_bindingLayouts[i].Device, device) ||
                _bindingLayouts[i].IsDisposed)
                throw new GraphicsValidationException(
                    "Every pipeline binding layout must belong to this OpenGL ES device.");
        }

        int mappingCount = 0;
        int scratchLength = 0;
        for (int i = 0; i < _bindingLayouts.Length; i++)
        {
            ReadOnlySpan<GraphicsBindingLayoutEntry> entries =
                ((OpenGLESGraphicsBindingLayout)_bindingLayouts[i]).Entries;
            mappingCount = checked(mappingCount + entries.Length);
            scratchLength = Math.Max(scratchLength, entries.Length);
        }

        _mappings = new OpenGLESDescriptorMapping[mappingCount];
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
            uint uniformSlot = 0;
            uint storageSlot = 0;
            uint textureSlot = 0;
            uint imageSlot = 0;
            int mappingIndex = 0;
            for (uint set = 0; set < (uint)_bindingLayouts.Length; set++)
            {
                OpenGLESGraphicsBindingLayout layout = (OpenGLESGraphicsBindingLayout)_bindingLayouts[(int)set];
                ReadOnlySpan<GraphicsBindingLayoutEntry> entries = layout.Entries;
                Span<GraphicsBindingLayoutEntry> ordered = scratch[..entries.Length];
                entries.CopyTo(ordered);
                SortByBinding(ordered);

                for (int i = 0; i < ordered.Length; i++)
                {
                    GraphicsBindingLayoutEntry entry = ordered[i];
                    ref uint nextSlot = ref GetNextSlot(entry.Type, ref uniformSlot, ref storageSlot, ref textureSlot, ref imageSlot);
                    uint baseSlot = nextSlot;
                    nextSlot = checked(nextSlot + entry.Count);
                    _mappings[mappingIndex++] =
                        new OpenGLESDescriptorMapping(set, entry.Binding, entry.Type, baseSlot);
                }
            }

            if (uniformSlot > device.MaxUniformBufferBindings ||
                storageSlot > device.MaxStorageBufferBindings ||
                textureSlot > device.MaxTextureUnits ||
                imageSlot > device.MaxImageUnits)
                throw new GraphicsValidationException("The pipeline layout exceeds the OpenGL ES descriptor binding limits.");
        }
        finally
        {
            if (rentedScratch is not null)
                ArrayPool<GraphicsBindingLayoutEntry>.Shared.Return(rentedScratch);
        }
        BindingLayouts = _bindingLayouts;
    }

    public ReadOnlyMemory<IGraphicsBindingLayout> BindingLayouts { get; }

    internal ReadOnlySpan<OpenGLESDescriptorMapping> Mappings => _mappings;

    internal bool TryGetMapping(
        uint set,
        uint binding,
        out OpenGLESDescriptorMapping mapping)
    {
        for (int i = 0; i < _mappings.Length; i++)
        {
            if (_mappings[i].SetIndex == set && _mappings[i].Binding == binding)
            {
                mapping = _mappings[i];
                return true;
            }
        }

        mapping = default;
        return false;
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

    private static ref uint GetNextSlot(
        GraphicsDescriptorType type,
        ref uint uniform,
        ref uint storage,
        ref uint texture,
        ref uint image)
    {
        switch (type)
        {
            case GraphicsDescriptorType.UniformBuffer: return ref uniform;
            case GraphicsDescriptorType.StorageBuffer: return ref storage;
            case GraphicsDescriptorType.SampledTexture:
            case GraphicsDescriptorType.Sampler:
            case GraphicsDescriptorType.CombinedTextureSampler: return ref texture;
            case GraphicsDescriptorType.StorageTexture: return ref image;
            default: throw new GraphicsValidationException($"Unsupported GLES descriptor type: {type}.");
        }
    }

    protected override void DisposeCore() { }
}

/// <summary>Thread-safe mutable GLES descriptor set whose values are copied into command-list snapshots.</summary>
/// <remarks>The set stores managed resource references. Recording checks that they remain live and retains them for recorded and submitted command execution.</remarks>
internal sealed class OpenGLESGraphicsBindingSet : GraphicsDeviceObject, IGraphicsBindingSet
{
    private readonly OpenGLESGraphicsBindingLayout _layout;
    private readonly OpenGLESBindingValue[] _values;
    private readonly Lock _gate = new();

    internal OpenGLESGraphicsBindingSet(OpenGLESGraphicsBindingLayout layout) : base(layout.Device)
    {
        _layout = layout;
        Layout = layout;
        _values = new OpenGLESBindingValue[layout.ValueCount];
    }

    public IGraphicsBindingLayout Layout { get; }

    internal int ValueCount => _values.Length;

    /// <summary>Sets a uniform- or storage-buffer descriptor element.</summary>
    /// <param name="binding">Layout binding number.</param>
    /// <param name="arrayElement">Zero-based element within the binding array.</param>
    /// <param name="buffer">Same-device buffer with the usage required by the descriptor kind.</param>
    /// <param name="offset">Byte offset aligned to the queried uniform/storage buffer offset alignment.</param>
    /// <param name="range">Byte range, or <see cref="ulong.MaxValue" /> to use the remaining buffer bytes.</param>
    /// <exception cref="GraphicsValidationException">The binding kind, buffer usage, ownership, or alignment is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The element, offset, or range is outside the declared binding or buffer.</exception>
    public void SetBuffer(
        uint binding,
        uint arrayElement,
        IGraphicsBuffer buffer,
        ulong offset = 0,
        ulong range = ulong.MaxValue)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        EnsureOwned(buffer);

        if (!_layout.TryGetEntry(binding, out GraphicsBindingLayoutEntry entry, out int offsetIndex))
            throw new GraphicsValidationException($"Binding {binding} is not declared.");
        if (entry.Type is not (GraphicsDescriptorType.UniformBuffer or GraphicsDescriptorType.StorageBuffer))
            throw new GraphicsValidationException($"Binding {binding} is not a buffer descriptor.");
        if (arrayElement >= entry.Count)
            throw new ArgumentOutOfRangeException(nameof(arrayElement));

        GraphicsBufferUsage required = entry.Type == GraphicsDescriptorType.UniformBuffer
            ? GraphicsBufferUsage.Constant
            : GraphicsBufferUsage.Storage;
        if ((buffer.Description.Usage & required) == 0)
            throw new GraphicsValidationException($"The buffer does not declare {required} usage.");

        if (offset > buffer.Description.SizeInBytes)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (range == ulong.MaxValue)
            range = buffer.Description.SizeInBytes - offset;
        if (range == 0 || range > buffer.Description.SizeInBytes - offset)
            throw new ArgumentOutOfRangeException(nameof(range));

        uint alignment = entry.Type == GraphicsDescriptorType.UniformBuffer
            ? ((OpenGLESGraphicsDevice)Device).UniformBufferOffsetAlignment
            : ((OpenGLESGraphicsDevice)Device).StorageBufferOffsetAlignment;
        if (alignment > 1 && offset % alignment != 0)
            throw new GraphicsValidationException($"Buffer offset does not satisfy GLES alignment {alignment}.");

        lock (_gate)
            _values[offsetIndex + checked((int)arrayElement)] = new OpenGLESBindingValue(buffer, offset, range, null, null);
    }

    /// <summary>Sets a sampled, storage, or combined-texture descriptor element.</summary>
    /// <param name="binding">Layout binding number.</param>
    /// <param name="arrayElement">Zero-based element within the binding array.</param>
    /// <param name="textureView">Same-device texture view with usage compatible with the descriptor.</param>
    /// <exception cref="GraphicsValidationException">The view is incompatible, including a framebuffer-only alias that cannot be sampled or bound as an image.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The array element is outside the declared binding.</exception>
    public void SetTexture(uint binding, uint arrayElement, IGraphicsTextureView textureView)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(textureView);
        EnsureOwned(textureView);
        if (textureView is OpenGLESGraphicsTextureView nativeView &&
            nativeView.IsFramebufferSubresourceAlias)
        {
            throw new GraphicsValidationException(
                "This GLES subresource alias is framebuffer-attachment-only. " +
                "Sampling/storage-image use requires GL_EXT_texture_view or GL_OES_texture_view.");
        }

        if (!_layout.TryGetEntry(binding, out GraphicsBindingLayoutEntry entry, out int offset))
            throw new GraphicsValidationException($"Binding {binding} is not declared.");
        if (entry.Type is not (GraphicsDescriptorType.SampledTexture or GraphicsDescriptorType.StorageTexture or GraphicsDescriptorType.CombinedTextureSampler))
            throw new GraphicsValidationException($"Binding {binding} is not a texture descriptor.");
        if (arrayElement >= entry.Count)
            throw new ArgumentOutOfRangeException(nameof(arrayElement));

        GraphicsTextureUsage required = entry.Type == GraphicsDescriptorType.StorageTexture
            ? GraphicsTextureUsage.Storage
            : GraphicsTextureUsage.Sampled;
        if ((textureView.Texture.Description.Usage & required) == 0)
            throw new GraphicsValidationException($"The texture does not declare {required} usage.");

        lock (_gate)
        {
            int index = offset + checked((int)arrayElement);
            OpenGLESBindingValue previous = _values[index];
            _values[index] = previous with { Texture = textureView };
        }
    }

    /// <summary>Sets a sampler or combined-texture-sampler descriptor element.</summary>
    /// <param name="binding">Layout binding number.</param>
    /// <param name="arrayElement">Zero-based element within the binding array.</param>
    /// <param name="sampler">Live sampler created by the same device.</param>
    /// <exception cref="GraphicsValidationException">The binding kind or sampler device ownership is incompatible.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The array element is outside the declared binding.</exception>
    public void SetSampler(uint binding, uint arrayElement, IGraphicsSampler sampler)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sampler);
        EnsureOwned(sampler);

        if (!_layout.TryGetEntry(binding, out GraphicsBindingLayoutEntry entry, out int offset))
            throw new GraphicsValidationException($"Binding {binding} is not declared.");
        if (entry.Type is not (GraphicsDescriptorType.Sampler or GraphicsDescriptorType.CombinedTextureSampler))
            throw new GraphicsValidationException($"Binding {binding} is not a sampler descriptor.");
        if (arrayElement >= entry.Count)
            throw new ArgumentOutOfRangeException(nameof(arrayElement));

        lock (_gate)
        {
            int index = offset + checked((int)arrayElement);
            OpenGLESBindingValue previous = _values[index];
            _values[index] = previous with { Sampler = sampler };
        }
    }

    /// <summary>Validates that captured resource references are live and appends values to command-list-owned storage.</summary>
    /// <param name="destination">Reusable destination list owned by the command recorder.</param>
    /// <returns>Starting element offset of the appended snapshot.</returns>
    /// <exception cref="ObjectDisposedException">A resource in the binding set has been disposed.</exception>
    internal int SnapshotTo(List<OpenGLESBindingValue> destination)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            int start = destination.Count;
            for (int i = 0; i < _values.Length; i++)
            {
                if (_values[i].Buffer is { IsDisposed: true } ||
                    _values[i].Texture is { IsDisposed: true } ||
                    _values[i].Sampler is { IsDisposed: true })
                    throw new ObjectDisposedException("A GLES binding set contains a disposed resource.");
            }
            destination.AddRange(_values);
            return start;
        }
    }

    private void EnsureOwned(IGraphicsDeviceObject resource)
    {
        if (resource.IsDisposed || resource.Device.IsDisposed)
            throw new ObjectDisposedException(resource.GetType().Name);
        if (!ReferenceEquals(resource.Device, Device))
            throw new GraphicsValidationException("Binding resources must belong to this GLES device.");
    }

    protected override void DisposeCore()
    {
        lock (_gate)
            Array.Clear(_values);
    }
}
