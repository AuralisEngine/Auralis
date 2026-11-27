# Auralis.Graphics

`Auralis.Graphics` is the platform-independent graphics contract library. It targets .NET 10 and has no dependency on a native graphics binding, windowing system, rendering layer, ECS, or editor.

## Project organization

The implementation uses the directories already declared by `Auralis.Graphics.csproj`:

- `Abstractions`: backend identifiers, common exceptions, and device-object lifetime.
- `Devices`: provider registration, device creation options, adapter information, and device capabilities.
- `Resources`: buffer, texture/view, sampler, shader, and format descriptions/contracts.
- `Commands`: command-list lifecycle and command queue/recording contracts.
- `Synchronization`: timeline-style fences and backend-neutral resource dependencies.
- `Pipelines`: shader-stage, vertex-input, descriptor-layout, and pipeline descriptions.
- `Presentation`: native target descriptors, presentation surfaces, image acquisition, and swapchains.
- `Backends/Direct3D12`, `Backends/Vulkan`, and `Backends/Metal` remain the locations for future implementations. No backend implementation is included here.

The existing `Backends/OpenGL` folder is left untouched; OpenGL is not advertised as a supported backend by this common API.

## Backend registration and device creation

Backend implementations provide `IGraphicsBackendProvider` and register explicitly:

```csharp
var providers = new GraphicsBackendProviderRegistry();
providers.Register(new MyDirect3D12Provider());
providers.Register(new MyVulkanProvider());

using IGraphicsDevice device = await GraphicsDeviceFactory.CreateAsync(
    providers,
    new GraphicsDeviceOptions
    {
        Backend = GraphicsBackend.Vulkan,
        RequiredQueues = GraphicsQueueCapabilities.Graphics |
                         GraphicsQueueCapabilities.Compute
    });
```

The provider types in this example are supplied by future backend assemblies, not by this common library. No reflection, assembly scanning, or native API type discovery is performed. When `Backend` is omitted, available providers are tried in registration order. Enumerate adapter information through `GraphicsDeviceFactory.EnumerateAdaptersAsync`, then use the selected provider's opaque `GraphicsAdapterId` in device options. An adapter ID is backend-specific, so adapter selection requires an explicit backend.

Device creation is independent of presentation. A device may be created and used for compute, resource uploads, or offscreen work without creating a surface or swapchain.

## Capabilities

`GraphicsDeviceCapabilities` contains the actual feature set, queue capabilities, reported limits, and per-format support reported by the selected backend. The common layer does not infer support from `GraphicsBackend` and does not provide fabricated defaults. Core operations represented by the common API form the baseline contract; `GraphicsFeature` describes optional capabilities that may be requested at device creation and queried later.

Texture format support and sample counts are queried from the capability snapshot. Format support is distinct from texture usage flags: a texture description expresses intended use, while the device decides whether that format/usage combination is valid.

The capability snapshot exposes device-level validation for buffers, textures, texture views, and vertex input layouts. It also reports independently enabled descriptor-indexing features (partially bound, update-unused-while-pending, variable descriptor count, and per-descriptor-type update-after-bind) separately from the umbrella descriptor-indexing feature. The same fine-grained value type is used at three distinct points: `GraphicsAdapterInfo.SupportedDescriptorIndexing` describes physical support, `GraphicsDeviceOptions.RequiredDescriptorIndexing` requests features to enable, and `GraphicsDeviceCapabilities.DescriptorIndexing` reports the logical-device features actually enabled. Providers negotiate the request against the selected physical adapter; the factory validates the returned enabled set and disposes a device that does not meet the request. Reported non-zero dimension, array-layer, buffer-size, and vertex-input limits are enforced; zero means the backend did not report a limit. Texture-view validation checks the effective view format against the device's reported format support. Surface presentation remains a separate queue/surface query and is not inferred from format support.

## Backend translation guidance

The common interfaces intentionally describe semantic operations rather than exposing one API's object model. Future providers should translate them as follows, while preserving each native API's actual constraints:

| Common concept | Direct3D 12 | Vulkan | Metal |
| --- | --- | --- | --- |
| Device and adapter | DXGI adapter + D3D12 device | physical device + logical device | `MTLDevice` |
| Logical queues | direct/compute/copy command queues; roles may share a queue | queues selected from compatible queue families; roles may share a queue | `MTLCommandQueue`; use command-buffer/encoder semantics for work classes |
| Command list | command allocator + graphics command list | command pool + command buffer | command buffer with render/compute/blit encoders |
| Resource dependencies | transition, UAV, and aliasing barriers | pipeline barriers, access masks, stages, and image layouts | encoder/resource-use ordering and hazard-tracking mechanisms |
| Binding layout and sets | root signatures and descriptor heaps/tables | descriptor set layouts and descriptor sets | resource slots and argument buffers |
| Presentation | DXGI swapchain and Win32 target data | platform surface plus swapchain and present-capable queue | drawable/layer presentation, commonly through a `CAMetalLayer` |
| Shader artifacts | DXIL | SPIR-V | Metal library or supported source compilation path |

This table is guidance, not a claim that the native concepts have identical semantics. In particular:

- A logical graphics, compute, or copy queue does not guarantee a physically separate native queue. Vulkan queue-family compatibility and Metal queue behavior differ from D3D12 queue types. A backend may return the same queue for multiple logical roles when that matches its native model.
- `GraphicsResourceBarrier` is intentionally not a universal D3D12 resource-state enum. Vulkan requires explicit execution/access/layout dependencies in more cases; Metal may express hazards through resource tracking and encoder boundaries. Backends must preserve the required visibility and ordering and may expose optional extension interfaces for native-only synchronization.
- `GraphicsMemoryAccess` and `GraphicsMemoryPreference` express intent, not a promise about heaps or storage modes. D3D12 heap placement, Vulkan memory-type selection, and Metal storage modes need backend-specific selection.
- Descriptor flags such as partially-bound, update-after-bind, and variable-count are not universally supported. Providers must validate them against actual device capabilities rather than emulate them silently.
- Shader format identifiers are opaque. DXIL, SPIR-V, Metal libraries, and source text must not be treated as interchangeable bytecode.
- Presentation support is surface-specific. A queue that can present to one native target may not present to another; surface creation must validate the chosen queue and native handle combination.

## Resources and lifetime

Descriptors expose logical intent rather than native heap or resource-state details. For example, `GraphicsMemoryPreference` is a placement hint; it does not promise that a resource occupies a particular physical heap.

Texture views may select one mip/layer from a 2D array texture or one cube face as a 2D view. Format reinterpretation is opt-in through GraphicsResourceCreationOptions.MutableFormat and is restricted by the common layer to explicit device-reported view compatibility through `GraphicsFormatCapabilities.ViewCompatibleFormats`. The device capability validator checks that both the resource format and effective view format support the requested usage. Native backends must report the exact mutable-format compatibility classes exposed by their API/device; this flag does not promise arbitrary format casts.

GraphicsTextureUsage.Present marks textures intended for a presentation surface. Its support is validated through the surface/queue compatibility contract rather than a per-format GraphicsFormatSupport bit.

`IGraphicsResource` objects are owned by one device and are disposed deterministically. The common `GraphicsDeviceObject` base class provides idempotent disposal and no finalizer. A backend must defer physical destruction of resources that are still referenced by submitted GPU work. Disposal releases the application's reference; it must not invalidate work already accepted by the GPU.

CPU mapping is explicit. Memory returned from `IGraphicsBuffer.Map` remains valid only until `Unmap`. Each map window is limited to Int32.MaxValue bytes because `Memory<byte>` uses an Int32 length; larger resources are mapped through multiple windows. Backends are responsible for any required cache flush or invalidation and may reject mapping when the chosen access intent is not CPU-visible.

Shader descriptions carry an artifact-format identifier and opaque bytes. DXIL, SPIR-V, Metal libraries, source artifacts, and application-defined formats are not assumed to be interchangeable. Shader compilation is outside this library.

## Commands and synchronization

A command list is created from a queue, then moves through `Initial → Recording → Executable → Pending`. The backend must enforce the legal transition rules and report completion before a pending list is reset or reused. `GraphicsCommandListLifecycle` provides a small, allocation-free state tracker for implementations and hardware-free validation. Command recording is caller-thread-owned; separate command lists may be recorded concurrently. Queue submission validates that lists belong to the same device and a compatible queue.

Graphics, compute, and copy queues are requested through `IGraphicsDevice.GetQueue` and are represented by `IGraphicsCommandQueue`. `IGraphicsQueue` is the shared device-owned queue identity (`Device` and `Index`). Presentation candidates are obtained through `IGraphicsDevice.GetPresentationQueue` and may implement only `IGraphicsQueue`; they do not promise command-list creation, submission, or queue waits. Always query `IGraphicsPresentationSurface.SupportsPresentation` for the actual target before passing a candidate to `CreateSwapChain`. A backend may return a command-capable queue as a presentation candidate when appropriate. `IGraphicsCommandList` defines render, compute, copy, and indirect operations; implementations reject operations illegal for the queue's command role or current recording state.

`GraphicsResourceBarrier` describes source/destination execution stages and access intents. GraphicsPipelineStage.AllGraphics includes indirect drawing, vertex input, all declared graphics shader stages, depth/stencil, and color attachment output; each stage is also individually selectable. The barrier validator uses an explicit compatibility table to check access flags against resource kind, declared buffer/texture usage, and source/destination stages. Host buffer access is also checked against the buffer's CPU memory-access intent.

Texture barriers specify semantic layouts. Layout transitions are checked against declared texture usage: sampled, color/depth-stencil attachment, copy-source/destination, and presentation layouts require their corresponding usage flags. General has no additional usage requirement. These are deliberately not Direct3D 12 resource states; backends translate them into their own hazard, layout, visibility, and synchronization model.

Barriers may target buffers, underlying textures, or acceleration structures. GraphicsBufferRange selects a buffer byte interval; GraphicsTextureSubresourceRange selects texture aspects, mip levels, and array layers. A null range means the entire buffer or texture. Range bounds are validated against the resource description, including format-aware color/depth/stencil aspect validation. Acceleration structures are a distinct resource kind: they use `AccelerationStructureRead`/`AccelerationStructureWrite` access and `RayTracingShader`, `AccelerationStructureBuild`, and `AccelerationStructureCopy` stages; AS barriers cannot specify byte ranges, texture subresource ranges, or texture layouts. A build-to-trace dependency is expressed as AS-write at `AccelerationStructureBuild` followed by AS-read at `RayTracingShader`. Texture views, samplers, and shaders are rejected as direct barrier resources. Shading-rate images use the dedicated `ShadingRateAttachment` texture usage/layout and `ShadingRateAttachmentRead` access at the `FragmentShadingRate` stage. To synchronize a view's subresources, specify its parent texture and the corresponding subresource range. A backend may conservatively widen a valid range when required by its native API, but must preserve the requested dependency. Fences expose timeline-style CPU waits/signals, asynchronous CPU waits, and queue-side GPU waits. Queue-family ownership transfer uses explicit release/acquire phases. A release scope specifies the source stage/access and uses `None` for the ignored destination stage/access; an acquire scope uses `None` for the ignored source stage/access and specifies the destination stage/access. The release is recorded on the source queue and acquire on the destination queue; the application orders them with a source signal and destination queue wait. These semantics avoid placeholder TOP/BOTTOM stages.

## Pipelines and descriptors

Binding layouts are immutable descriptions with explicit binding indices, descriptor types, counts, shader stages, and optional flags. A variable-count binding is required to have the highest binding number; input order is otherwise unrestricted. Variable-count and update-after-bind are forbidden for dynamic uniform/storage buffer descriptors, and any layout containing an update-after-bind binding cannot also contain dynamic uniform/storage descriptors. `GraphicsBindingLayoutDescription.Validate()` checks structural compatibility; `Validate(GraphicsDeviceCapabilities)` additionally checks the logical device's enabled descriptor-indexing features and per-descriptor-type update-after-bind support. Physical adapter support must be enabled when creating the device. Binding sets hold resource references and are bound by set index.

Render pipeline descriptions accept a shader-stage set rather than requiring a fixed vertex/fragment pair. Conventional pipelines require a vertex shader and may include geometry, fragment, and a paired tessellation-control/evaluation stage set. Tessellation uses PatchList topology and an explicit patch-control-point count. Mesh pipelines require a mesh shader, may include a task shader and fragment shader, and cannot include vertex input, vertex, geometry, or tessellation stages. Optional geometry, tessellation, mesh, and task stages are checked against their corresponding independent device features; a task shader additionally requires a mesh shader stage. Compute pipeline validation requires GraphicsFeature.ComputeShaders.

Pipeline validation also checks vertex attribute formats and vertex-input limits against device capabilities. Enabling blending requires the target format to report GraphicsFormatSupport.Blend. Compute pipelines remain independent of graphics fixed-function state.

This API intentionally omits render graphs, materials, render passes, and renderer scheduling. Unsupported optional pipeline features must be reported by a backend rather than silently approximated.

## Native presentation interoperability

A host or platform integration implements `IGraphicsPresentationTarget` and returns a snapshot containing:

- A platform identifier.
- A collection of typed opaque handles (not one universal pointer).
- Per-handle ownership (`Borrowed` or `OwnedByGraphics`).
- The drawable extent, revision, and suspended state.

For example, a Win32 target can supply an HWND and HINSTANCE; X11 can supply a Display and Window; Wayland can supply a display and surface; Android can supply an ANativeWindow; Apple platforms can supply a Metal layer. These types are represented only by opaque `nint` values and handle-kind identifiers in this project. Native handle extraction and conversion belong to platform integrations.

The application remains responsible for the lifetime of every borrowed native object and of the `IGraphicsPresentationTarget` itself. The graphics surface borrows the target and must not destroy borrowed handles. Handles marked `OwnedByGraphics` transfer release responsibility to the graphics surface/backend.

Surface creation occurs after device creation. Device-level GraphicsQueueCapabilities.Present is only a coarse indication that the device may expose presentation queues; it does not prove that a specific queue can present to a specific target. Request command-capable queues through IGraphicsDevice.GetQueue and presentation candidates through IGraphicsDevice.GetPresentationQueue. Query IGraphicsPresentationSurface.SupportsPresentation(queue) for the actual target, then pass the selected compatible IGraphicsQueue explicitly to CreateSwapChain(queue, description). A backend may return a command-capable queue or a dedicated presentation-only queue; the latter implements IGraphicsQueue but not IGraphicsCommandQueue. The swapchain exposes the queue it uses for presentation. This keeps headless/offscreen device creation independent of presentation and accommodates surface-specific queue-family support such as Vulkan.

Resizing recreates the swapchain image set and invalidates outstanding acquired-image values. When a target is suspended (for example, a minimized window), the application should skip acquisition/presentation until a non-zero extent is reported. When native surface identity changes, create a new surface from the updated target snapshot and dispose the old surface after its work is complete.

## Threading

Provider registration is a startup operation and the registry is not intended for concurrent mutation. Device/resource objects are not universally thread-safe; callers must synchronize mutations and disposal unless an implementation explicitly documents otherwise. Independent command lists may be recorded concurrently. Queue submission and CPU/GPU synchronization are backend-defined but must follow the command-list lifecycle contract.

## Implementation status

This project currently provides common descriptors, validation, provider/device contracts, command/pipeline/synchronization interfaces, and presentation interop contracts. It does **not** create native devices, allocate GPU memory, record native commands, compile shaders, create native windows/surfaces, or implement a Direct3D 12, Vulkan, or Metal backend. Those responsibilities belong to future backend and platform integration work.


## Extended contracts: memory, queries, and advanced GPU work

The core device contract intentionally keeps optional functionality discoverable through capability-specific interfaces rather than forcing every device to implement unsupported features:

- `IGraphicsMemoryDevice` exposes `IGraphicsMemoryAllocator`, memory heaps/types, property requirements, alignment, mapping, and opaque allocation lifetime. Memory properties describe what a backend reports; they do not imply a Vulkan-style memory-type model on D3D12 or a particular Metal storage mode. IGraphicsResourceMemoryDevice exposes backend-reported placement requirements and explicit binding; IGraphicsAliasingCommandList expresses aliasing dependencies.
- `IGraphicsQueryDevice` creates timestamp, occlusion, pipeline-statistics, and acceleration-structure-compacted-size pools. `IGraphicsQueryCommandList` records query operations independently of dynamic rendering; timestamp queries may use graphics, compute, or copy queues when supported. Query reset/read operations must not race GPU writes. `IGraphicsTimestampPropertiesDevice` reports queue-specific timestamp frequency and valid counter bits; `GraphicsTimestampProperties` handles counter wraparound when converting elapsed ticks.
- `IGraphicsRayTracingDevice`, `IGraphicsRayTracingPipelineDevice`, `IGraphicsRayTracingCommandList`, and `IGraphicsRayTracingShaderTableDevice` represent acceleration-structure size queries, BLAS geometry and TLAS instance build/update commands, compacted-size queries and explicit compact copies, shader-group handles, validated shader-binding-table regions, and ray dispatch. Triangle vertex formats are backend-reported through `GraphicsFormatSupport.AccelerationStructureVertex`; the common geometry description includes 16-bit two-/three-/four-component float and normalized positions plus 32-bit two-/three-/four-component float positions. Four-component 16-bit formats consume only XYZ position components and permit a six-byte stride. Each backend advertises only formats it can actually consume. Triangle hit groups may omit closest-hit and any-hit shaders independently, including both; procedural hit groups require an intersection shader while closest-hit and any-hit remain optional. Ray-tracing pipeline validation checks live same-device shader/layout resources, shader-stage agreement, group references, ray-generation presence, and device-reported recursion/payload/hit-attribute limits. `GraphicsShaderBindingTableWriter` packs group handles and application local data into caller-provided storage using aligned record strides. Shader table buffers explicitly declare `GraphicsBufferUsage.ShaderBindingTable` and expose GPU addresses. Managed TLAS instance spans are convenient but may require backend staging; high-frequency rebuilds can instead use `GraphicsRayTracingInstanceBuffer`, which references caller-owned GPU storage containing canonical 64-byte `GraphicsRayTracingInstanceData` records. Update builds preserve the source TLAS instance count and creation flags. BLAS updates preserve geometry count, per-geometry primitive counts, geometry types and flags, and triangle vertex/index formats. The same acceleration-structure object may be supplied as both source and destination for an in-place update; the caller remains responsible for ordering prior accesses and the update with queue submissions and barriers. `GraphicsAccelerationStructureSizes` reports the AS storage alignment and the scratch-device-address alignment separately; shared build validation checks the selected build/update scratch size, GPU address, alignment, and buffer bounds. Implementations should call the shared `GraphicsRayTracingCommandValidation` helpers before recording build, compacted-size query, and copy operations.
- `IGraphicsMeshCommandList` represents mesh dispatch independently from the conventional draw path.
- `IGraphicsRenderingCommandList` adds dynamic rendering scopes, attachment load/store/resolve descriptions, viewports, scissors, and dynamic blend/stencil values without requiring render-pass objects. The common `GraphicsViewport` contract currently requires positive width and height; negative-height Y inversion is not exposed until it can be modeled through explicit backend capability negotiation.
- `IGraphicsCommandDiagnostics` represents debug labels and markers without affecting execution dependencies.

The adapter's `SupportedFeatures` describes what the selected physical adapter reports before device creation. `GraphicsDeviceOptions.RequiredFeatures` requests features that must be enabled; `GraphicsDeviceCapabilities.Features` is the resulting logical-device feature set. Backends must reject unavailable required features and must not silently enable unsupported features. Optional extension interfaces are not a substitute for capability checks.

## Advanced capability coverage and remaining boundaries

The common API now has optional extension surfaces for explicit resource placement and aliasing, sparse buffer/texture mapping including texture mip tails, variable-rate shading state and shading-rate images, multi-device groups and peer-memory capability queries, queue-specific timestamp metadata, shader-group handles and shader-binding-table record packing, and typed device-loss status. These are API definitions only: providers must expose the matching feature and extension interfaces only when they can implement the documented semantics.

The multi-device API is explicit. A provider may create a group from selected adapter IDs; applications select participating devices with a validated mask and query peer-memory access per source/destination pair. The common API does not assume linked-adapter topology, implicit replication, or automatic cross-device resource migration.

Variable-rate shading exposes rate capabilities, combiners, and an optional shading-rate image. Foveation policy and generation of the shading-rate image remain application/rendering-layer responsibilities rather than being embedded in this low-level API.

Sparse resources report backend tile dimensions, page sizes, mip-tail requirements, and explicit bind/unbind operations ordered on a queue that explicitly implements `IGraphicsSparseBindingQueue`. The shared validator checks that this exact queue belongs to the sparse device and that `GraphicsFeature.SparseResources` is enabled; not every queue on a device is presumed sparse-capable. Sparse image requirements and regions identify exactly one Color, Depth, or Stencil aspect per requirement/binding; separate depth and stencil requirements may report different block layouts. Opaque metadata mip tails use the optional `IGraphicsSparseTextureMetadataDevice` contract because metadata is not a texel aspect. Tile extents are backend-reported granularities, not power-of-two API constants; Vulkan explicitly reports non-standard sparse block dimensions when applicable. 1D sparse-residency textures are excluded. Multisample sparse images are structurally representable, but each backend must reject sample counts for which its adapter does not report sparse-residency support. In particular, Vulkan sparse image residency is single-sample: a Vulkan backend must reject every sparse texture with SampleCount greater than one even if the common descriptor passes structural validation. D3D12 tiled-resource support is queried separately and must not be inferred from the common descriptor. The common API does not invent a universal tile shape or silently emulate residency.

Device loss is observable through IGraphicsDeviceHealth; once lost, a device is not assumed to recover in place. Implementations should transition from Operational to Lost, provide a diagnostic reason, reject subsequent GPU operations with GraphicsDeviceLostException, and eventually report Disposed when released. Applications recreate the device and its resources explicitly.

A future D3D12 backend can map these concepts onto its native device, queue, resource, tiled-resource, shading-rate, device-group, fence, and ray-tracing facilities; Vulkan can map them to physical/logical devices, queue families, sparse binding, fragment shading rate, synchronization2 dependencies, descriptor sets, and acceleration structures. Metal can implement the common subset and report unsupported optional capabilities, while its encoder, storage, and feature semantics remain backend-specific. None of these mappings is implemented by this project.

No GPU execution test is implied by these contracts. Tests exercise managed validation and metadata only; actual sparse residency, VRS, multi-device execution, ray tracing, and device-loss behavior require backend-specific integration tests.


## Explicit resource placement and descriptor indexing

Resources may opt into explicit placement with `GraphicsResourceCreationOptions.RequireExplicitMemoryBinding`. Such a resource is created without backing memory and must be bound before any command uses it. `IGraphicsResourceMemoryDevice` reports the resource's size, alignment, compatible memory-type mask, and dedicated-allocation requirements; `BindMemory` validates that the allocation belongs to the same device, satisfies alignment and bounds, and uses a compatible memory type. The allocation must remain alive for the resource's lifetime and for all in-flight GPU references.

`AllowAliasing` is an explicit creation opt-in. Aliased placements require the optional `IGraphicsAliasingCommandList` extension and an aliasing dependency between the previous and next resource use. The caller is responsible for ensuring the resources actually overlap in memory and for ordering all prior accesses before the aliasing dependency. Dedicated allocations cannot be combined with aliasing or explicit placement in the current descriptions.

Descriptor layouts support dynamic uniform/storage buffer descriptors, variable descriptor counts, partially-bound arrays, update-after-bind, and update-unused-while-pending flags. `IGraphicsDescriptorIndexingDevice` allocates a binding set with runtime counts for every variable-count binding; the layout description validates the counts against declared maxima. `IGraphicsCombinedBindingSet` supplies the combined texture/sampler update path, and `IGraphicsDynamicBindingCommandList` supplies per-bind dynamic offsets. These extensions must be used only when the device reports descriptor-indexing support and the relevant limits permit the requested layout.

`GraphicsResourceCreationOptions.SparseResidency` marks buffers and textures that use sparse backing; it cannot be combined with dedicated allocation, aliasing, or full explicit memory binding. `IGraphicsSparseResourceDevice` reports buffer page sizes, texture tile dimensions and mip-tail requirements, and binds/unbinds ranges, tiles and mip tails with explicit queue/fence ordering.


Queue ownership is represented explicitly by `GraphicsQueueOwnershipTransfer` on a resource barrier. Source and destination queue families must be different. A release barrier is recorded on the source queue and an acquire barrier on the destination queue; `GraphicsResourceBarrier.Validate(recordingQueue)` verifies the family/index pair. The ownership description does not perform synchronization by itself: applications signal completion on the source queue and enqueue a wait on the destination queue. Backends without queue-family ownership expose one family identity and use their native synchronization model.

Dynamic descriptor offsets are supplied in ascending binding-number order, then ascending array-element order, matching explicit API requirements independently of the order in which layout entries were declared. `GraphicsBindingLayoutDescription.ValidateDynamicOffsets` checks completeness and ordering without allocating temporary collections. The overload receiving `GraphicsDeviceLimits` also validates the reported uniform/storage dynamic-offset alignments (zero means unreported); effective buffer bounds are validated at bind time because base descriptor offsets and ranges are not part of the layout description.
\n\n## New advanced GPU extensions\n\n- **Sparse resources:** `IGraphicsSparseResourceDevice` reports sparse buffer page sizes, texture tile dimensions and mip-tail requirements, and binds/unbinds sparse buffer ranges, texture tiles and mip tails on a selected queue. Mapping allocations are checked for device ownership and bounds; queue ordering can be expressed with timeline-fence waits/signals.\n- **Variable-rate shading:** `IGraphicsVariableRateShadingDevice` reports supported rates and attachment tile dimensions. `IGraphicsShadingRateCommandList` sets per-draw rates/combiners and binds an optional shading-rate image. The application owns image contents and foveation policy.\n- **Multi-GPU:** `IGraphicsDeviceGroupProvider` creates an explicit device group, `IGraphicsDeviceGroup` exposes member devices and directional peer-memory access, and `IGraphicsDeviceGroupCommandQueue` submits work to a validated device mask. No implicit cross-device migration or replication is promised.\n- **Timestamp precision:** `IGraphicsTimestampPropertiesDevice` reports frequency and valid counter bits per queue; elapsed-time helpers correctly account for counter wraparound.\n- **Shader binding tables:** `IGraphicsRayTracingShaderTableDevice` reports handle/alignment limits and retrieves opaque group handles. `GraphicsShaderBindingTableWriter` packs group handles and application local data into caller-provided storage with aligned strides; `GraphicsShaderBindingTable` validates region buffer usage, GPU address alignment, stride, and bounds, then converts the regions into the existing ray-dispatch description.\n- **Device loss:** `IGraphicsDeviceHealth` exposes operational/lost/disposed state and a diagnostic reason. `GraphicsDeviceLostException` provides a typed failure for operations after loss.\n

### Format aspects, depth/stencil views, and multiview
`GraphicsFormatInfo.GetAspects` is the single source of truth for color, depth, and stencil aspects. Texture views can select a subset of the aspects exposed by both the resource format and effective view format; `None` selects all aspects in the view format. Rendering attachments validate the selected aspects and device-reported view-format compatibility. Depth tests, depth writes, and depth-bounds tests require a depth aspect; stencil tests require a stencil aspect. Color targets reject depth/stencil formats and depth/stencil targets reject color-only formats.

Multiview is represented by `GraphicsFeature.Multiview`, separate from `GraphicsFeature.MultiViewport`. A nonzero view mask requires enabled multiview support, a reported `MaxMultiviewViewCount`, and enough selected attachment layers. A zero view mask remains a single-view path and does not require multiview.


Acceleration-structure synchronization distinguishes three roles: geometry/instance input buffers use `AccelerationStructureBuildInputRead` at the `AccelerationStructureBuild` stage; build scratch buffers use `AccelerationStructureRead` and `AccelerationStructureWrite` at that stage and declare `AccelerationStructureScratch` usage; acceleration-structure storage itself uses those AS read/write intents on the AS resource. The AS read/write access intents are not valid for arbitrary buffers or textures. This reflects the Vulkan synchronization2 distinction between shader-read build inputs and dedicated acceleration-structure access flags for scratch and AS storage; other backends must translate the resource role into their native state/dependency model.
