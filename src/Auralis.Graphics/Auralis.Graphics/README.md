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

The capability snapshot exposes device-level validation for buffers, textures, texture views, and vertex input layouts. Reported non-zero dimension, array-layer, buffer-size, and vertex-input limits are enforced; zero means the backend did not report a limit. Texture-view validation checks the effective view format against the device's reported format support. Surface presentation remains a separate queue/surface query and is not inferred from format support.

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

Texture views may select one mip/layer from a 2D array texture or one cube face as a 2D view. Format reinterpretation is opt-in through GraphicsResourceCreationOptions.MutableFormat and is restricted by the common layer to explicit linear/sRGB compatibility pairs (RGBA8, BGRA8, BC1, BC3, BC7, and ASTC 4x4). The device capability validator checks that the effective view format supports the parent texture's declared usages. Native backends may impose additional format-view restrictions and must validate the corresponding native mutable-format requirements; this flag does not promise arbitrary format casts.

GraphicsTextureUsage.Present marks textures intended for a presentation surface. Its support is validated through the surface/queue compatibility contract rather than a per-format GraphicsFormatSupport bit.

`IGraphicsResource` objects are owned by one device and are disposed deterministically. The common `GraphicsDeviceObject` base class provides idempotent disposal and no finalizer. A backend must defer physical destruction of resources that are still referenced by submitted GPU work. Disposal releases the application's reference; it must not invalidate work already accepted by the GPU.

CPU mapping is explicit. Memory returned from `IGraphicsBuffer.Map` remains valid only until `Unmap`. Backends are responsible for any required cache flush or invalidation and may reject mapping when the chosen access intent is not CPU-visible.

Shader descriptions carry an artifact-format identifier and opaque bytes. DXIL, SPIR-V, Metal libraries, source artifacts, and application-defined formats are not assumed to be interchangeable. Shader compilation is outside this library.

## Commands and synchronization

A command list is created from a queue, then moves through `Initial → Recording → Executable → Pending`. The backend must enforce the legal transition rules and report completion before a pending list is reset or reused. `GraphicsCommandListLifecycle` provides a small, allocation-free state tracker for implementations and hardware-free validation. Command recording is caller-thread-owned; separate command lists may be recorded concurrently. Queue submission validates that lists belong to the same device and a compatible queue.

Graphics, compute, and copy queues are requested through `IGraphicsDevice.GetQueue` and are represented by `IGraphicsCommandQueue`. `IGraphicsQueue` is the shared device-owned queue identity (`Device` and `Index`). Presentation candidates are obtained through `IGraphicsDevice.GetPresentationQueue` and may implement only `IGraphicsQueue`; they do not promise command-list creation, submission, or queue waits. Always query `IGraphicsPresentationSurface.SupportsPresentation` for the actual target before passing a candidate to `CreateSwapChain`. A backend may return a command-capable queue as a presentation candidate when appropriate. `IGraphicsCommandList` defines render, compute, copy, and indirect operations; implementations reject operations illegal for the queue's command role or current recording state.

`GraphicsResourceBarrier` describes source/destination execution stages and access intents. GraphicsPipelineStage.AllGraphics includes indirect drawing, vertex input, all declared graphics shader stages, depth/stencil, and color attachment output; each stage is also individually selectable. The barrier validator uses an explicit compatibility table to check access flags against resource kind, declared buffer/texture usage, and source/destination stages. Host buffer access is also checked against the buffer's CPU memory-access intent.

Texture barriers specify semantic layouts. Layout transitions are checked against declared texture usage: sampled, color/depth-stencil attachment, copy-source/destination, and presentation layouts require their corresponding usage flags. General has no additional usage requirement. These are deliberately not Direct3D 12 resource states; backends translate them into their own hazard, layout, visibility, and synchronization model.

Barriers may target an entire resource or a precise range: GraphicsBufferRange selects a byte interval, while GraphicsTextureSubresourceRange selects aspects, mip levels, and array layers. A null range means the entire resource. Range bounds are validated against the resource description, including format-aware color/depth/stencil aspect validation. Barriers currently accept buffers and underlying textures only; texture views, samplers, and shaders are rejected. To synchronize a view's subresources, specify its parent texture and the corresponding subresource range. A backend may conservatively widen a valid range when required by its native API, but must preserve the requested dependency. Fences expose timeline-style CPU waits/signals and queue-side GPU waits. Backend-specific extension interfaces may be added later when a native capability cannot be represented faithfully.

## Pipelines and descriptors

Binding layouts are immutable descriptions with explicit binding indices, descriptor types, counts, shader stages, and optional flags. A variable-count binding is required to have the highest binding number; input order is otherwise unrestricted. Binding sets hold resource references and are bound by set index.

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

- `IGraphicsMemoryDevice` exposes `IGraphicsMemoryAllocator`, memory heaps/types, property requirements, alignment, mapping, and opaque allocation lifetime. Memory properties describe what a backend reports; they do not imply a Vulkan-style memory-type model on D3D12 or a particular Metal storage mode. Resource binding/placement and aliasing remain backend responsibilities and must be exposed by a future explicit resource-memory extension rather than inferred from an allocation handle.
- `IGraphicsQueryDevice` creates timestamp, occlusion, and pipeline-statistics pools. Query reset/read operations must not race GPU writes. Timestamp values are device ticks; consumers must use device timestamp-period metadata from a future limits extension before converting them to time.
- `IGraphicsRayTracingDevice`, `IGraphicsRayTracingPipelineDevice`, and `IGraphicsRayTracingCommandList` represent acceleration-structure size queries, opaque structures, build/update commands, shader-table regions, and ray dispatch. These are optional extensions and are usable only when the device reports the relevant ray-tracing feature.
- `IGraphicsMeshCommandList` represents mesh dispatch independently from the conventional draw path.
- `IGraphicsRenderingCommandList` adds dynamic rendering scopes, attachment load/store/resolve descriptions, viewports, scissors, and dynamic blend/stencil values without requiring render-pass objects.
- `IGraphicsCommandDiagnostics` represents debug labels and markers without affecting execution dependencies.

The adapter's `SupportedFeatures` describes what the selected physical adapter reports before device creation. `GraphicsDeviceOptions.RequiredFeatures` requests features that must be enabled; `GraphicsDeviceCapabilities.Features` is the resulting logical-device feature set. Backends must reject unavailable required features and must not silently enable unsupported features. Optional extension interfaces are not a substitute for capability checks.

## Contract boundaries and known follow-up work

These contracts establish the semantic vocabulary, but they do not yet claim complete parity for every advanced native facility. In particular, explicit resource-to-memory binding/placed-resource aliasing, sparse tile mapping, multi-GPU device groups, variable-rate shading attachment/state descriptions, and complete shader-group/SBT construction metadata need dedicated follow-up contracts before backend work begins. They are intentionally not approximated with generic flags or native handles here.

A future D3D12 backend can map the common device/queue, resource, dynamic-rendering, fence, descriptor, and pipeline concepts onto its native objects; Vulkan can map the same contracts to physical/logical devices, queue families, image layouts, synchronization2 dependencies, descriptor sets, and acceleration structures. Metal can implement the common subset and report unsupported optional capabilities, while its encoder and storage semantics remain backend-specific. None of these mappings is implemented by this project.

No GPU execution test is implied by these contracts. Tests should exercise immutable-description validation, feature negotiation rules, and lifecycle rules with ordinary managed test doubles only.
