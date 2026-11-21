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
- `Backends/OpenGL` contains the desktop OpenGL implementation and its Silk.NET context-provider integration.
- `Backends/Direct3D12`, `Backends/Vulkan`, and `Backends/Metal` remain reserved for future implementations.

The OpenGL provider is included in this assembly but is never registered automatically. Applications opt into it by registering an `OpenGLGraphicsBackendProvider` explicitly.

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

The Direct3D 12 and Vulkan provider types in this example are supplied by future backend implementations; the OpenGL provider is included in this assembly. No reflection, assembly scanning, or native API type discovery is performed. When `Backend` is omitted, available providers are tried in registration order. Enumerate adapter information through `GraphicsDeviceFactory.EnumerateAdaptersAsync`, then use the selected provider's opaque `GraphicsAdapterId` in device options. An adapter ID is backend-specific, so adapter selection requires an explicit backend.

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

This project provides backend-neutral graphics descriptors, validation, device/command/pipeline/synchronization contracts, presentation interop, and a desktop OpenGL implementation in the same `Auralis.Graphics` assembly. The OpenGL backend creates and manages OpenGL resources and executes recorded commands through an injected, host-owned context provider. It does not create native windows or contexts. Direct3D 12, Vulkan, and Metal implementations are outside the scope of this project state.


## OpenGL backend

The backend is implemented in `Backends/OpenGL` in the existing `Auralis.Graphics` assembly. It uses the official Silk.NET packages `Silk.NET.OpenGL` and `Silk.NET.OpenGL.Extensions.ARB`, both version **2.23.0**. No OpenTK, alternative GL binding, handwritten P/Invoke entry points, or Silk.NET.Windowing dependency is used. The supported baseline is **desktop OpenGL 4.5 Core Profile**. OpenGL ES contexts are not supported.

### Registration and context integration

The application creates its own context through its platform integration and supplies it using the backend-specific `IOpenGLContextProvider` contract. The backend does not create windows or contexts and does not interpret `GraphicsNativeSurfaceDescription` as a context descriptor.

```csharp
var registry = new GraphicsBackendProviderRegistry();
registry.Register(new OpenGLGraphicsBackendProvider(contextProvider));

using IGraphicsDevice device = await GraphicsDeviceFactory.CreateAsync(
    registry,
    new GraphicsDeviceOptions
    {
        Backend = GraphicsBackend.OpenGL,
        RequiredQueues = GraphicsQueueCapabilities.Graphics |
                         GraphicsQueueCapabilities.Copy
    });

IGraphicsCommandQueue graphicsQueue = device.GetQueue(GraphicsQueueType.Graphics);
IGraphicsCommandQueue copyQueue = device.GetQueue(GraphicsQueueType.Copy);
```

The context provider supplies a stable context identity, function-pointer resolver, `MakeCurrent`/`ReleaseCurrent`, ownership policy, and optional buffer swapping. It may wrap a host-created WGL, GLX, EGL, or native Apple desktop OpenGL context. Those native APIs and context creation remain the host/platform integration's responsibility. A headless/offscreen context is valid; no drawable is required for device creation or offscreen resource/command work.

The hot `WithContext` paths use inline `MakeCurrent`/`ReleaseCurrent` try/finally scopes, avoiding per-operation context-lease allocations; the `Action<GL>` overload no longer creates an intermediate `Func` wrapper. The device makes the supplied context current before loading Silk.NET's `GL` dispatch. It checks the actual version and Core Profile before constructing the Silk.NET extension wrappers. A context lease calls `ReleaseCurrent` only after `MakeCurrent` returns successfully, and releases the context even when the guarded GL operation throws. The same provider is used to reactivate the context for each native operation and submission; context access is serialized by the device. A dispatch object is never shared with an unrelated context.

Context-object ownership and GL-state ownership are separate:
- `OpenGLContextOwnership.Borrowed`: Auralis never disposes the host context. The provider is not disposed by the device.
- `OpenGLContextOwnership.Owned`: the provider has transferred its lifetime to the device; device disposal calls the provider's `Dispose`.
- `OpenGLContextStateOwnership.Exclusive` is required. The initial backend does not save/restore all GL state. The host must not issue unrelated GL commands on the context while it is attached to Auralis. `Shared` state ownership is rejected instead of silently corrupting host state.

The context provider owns the meaning of its context identity and must guarantee that its resolver and activation methods refer to that same context. A drawable is optional. For presentation, the provider must additionally verify that a `GraphicsPresentationTargetSnapshot` identifies the drawable belonging to this exact context, expose the presentation modes it can honor, apply the selected mode, and swap that drawable's buffers. The backend rejects a target that the provider does not recognize.

### Implemented functionality

| Area | Current implementation |
| --- | --- |
| Device/provider | Explicit registration, 4.5 Core validation, vendor/renderer/version/driver strings, queried limits and capabilities |
| Formats | Silk.NET internal-format queries and per-format operation/sample-count capability snapshots; unsupported formats remain unsupported |
| Buffers | Immutable DSA storage, direct `MapNamedBufferRange`/`UnmapNamedBuffer` views over driver memory, explicit per-access storage permissions, invalidated views after unmap/disposal, and buffer copy |
| Textures | Immutable 1D/2D/3D/cube storage, arrays and multisample 2D storage; texture views via `ARB_texture_view` |
| Samplers | Sampler objects with filtering, addressing, compare, LOD, border color, and extension-gated anisotropy |
| Shaders | GLSL source compilation and extension-gated SPIR-V ingestion/specialization through Silk.NET; driver compile/specialization logs surfaced; requested SPIR-V entry point and specialization constants are passed to the driver |
| Descriptors | Uniform/storage buffers, sampled/storage textures, samplers, and combined texture/sampler sets; explicit deterministic binding-point mapping |
| Graphics/compute pipelines | Linked monolithic graphics or compute program; DSA vertex-array object for conventional graphics pipelines; supported geometry/tessellation stages; fixed raster/depth/stencil/blend state |
| Rendering/compute commands | Deferred render-target binding, viewport/scissor, typed clears, vertex/index binding, direct/indexed/indirect draws, direct/indirect compute dispatch |
| Copy/barriers | Deferred buffer and whole-texture copies plus destination-access-based OpenGL memory barriers |
| Synchronization | GL sync objects, allocation-efficient FIFO fence tracking, CPU wait/polling, and non-blocking GPU-side waits for already-created fence values |
| Presentation | Acquired images are offscreen color textures; present blits to the provider-verified default drawable and swaps buffers |
| Queues | Logical graphics/compute/copy queues sharing one serialized context command stream; no physical parallel queues are claimed |

The backend reports OpenGL implementation strings in `GraphicsDeviceInfo`. The injected context is already selected, so system-wide physical adapter enumeration is unavailable: `EnumerateAdaptersAsync` returns an empty list rather than fabricating adapter IDs, PCI identifiers, memory sizes, or adapter types. Unknown limits remain zero. Format support is derived from the active context's internal-format operation queries and advertised extensions; it is not inferred from the enum alone.

### Binding-point mapping

OpenGL has no Vulkan-style descriptor-set object. Auralis maps common sets and bindings to OpenGL binding points deterministically:
1. Process descriptor sets in ascending set index.
2. Process bindings within each set in ascending binding number, independent of the order used to construct the common description.
3. Maintain independent slot sequences for uniform-buffer bindings, storage-buffer bindings, texture units (sampled textures, samplers, and combined texture/sampler entries), and image units (storage textures).
4. A descriptor array consumes consecutive slots in its namespace.

GLSL must use the resulting numeric binding in its `layout(binding = N)` declaration. This backend does not rewrite GLSL source to translate a `set` qualifier. For example, UBO binding points are assigned independently from texture units; the same integer may therefore occur in two different namespaces. Partially-bound, update-after-bind, and variable-count descriptor flags remain unsupported and are rejected. A binding set must be fully populated before it is captured into a command list; missing resource slots are not implicitly cleared or treated as partially bound.

### Command execution and queue model

OpenGL does not expose native command buffers. Auralis uses CPU-side typed deferred command records: recording methods validate and retain arguments without invoking GL; submission activates the provider's context and executes commands in order. Submission command-list reference arrays are rented and returned only after the associated GL completion sync has signaled. Queue submission associates a GL sync object with the submitted lists. A list becomes reusable only after the submission is observed complete. Referenced native resources are not physically deleted while submissions are pending.

The graphics, compute, and copy queue objects are logical roles over one context's ordered command stream. They are serialized and do not promise independent graphics, compute, or transfer hardware queues. Compute pipeline creation, direct dispatch, and indirect dispatch are implemented when the required OpenGL 4.3+ compute functionality is present. The backend advertises a logical compute queue in that case, but does not advertise separate compute/transfer queue features.

The fence abstraction is implemented with ordered GL sync objects stored in a reusable FIFO queue; monotonic values make a tree structure unnecessary. CPU waits use client waits; queue waits enqueue a GL server-side wait and do not block the CPU. A queue wait requires the requested fence value to have already been signaled/submitted, because OpenGL cannot enqueue a wait on a future sync object that does not yet exist. The implementation provides monotonic timeline-style values on top of individual GL sync objects; it does not claim the native Vulkan timeline-semaphore feature.

Resource barriers are translated into GL memory-barrier bits based on destination access. Texture layouts in the common barrier description remain semantic hints, not native OpenGL image layouts. The backend uses broad `AllBarrierBits` only for generic memory read/write intents where a narrower consumer cannot be identified.

### Explicit limitations

- No OpenGL ES support.
- No native context/window creation or platform integration is included.
- The backend does not expose the native default framebuffer as an `IGraphicsTexture`. Swapchain images are Auralis-owned offscreen textures; presentation blits one to framebuffer 0 and then calls the provider's swap operation.
- The injected provider/platform integration must correctly identify the exact drawable, support and apply the requested present mode, and implement buffer swapping. No WGL/GLX/EGL/native Apple context or window creation code is included.
- The current swapchain presents a color-only offscreen image ring; it does not expose default framebuffer depth/stencil attachments or native swapchain image handles.
- No mesh/task shader pipeline support: optional mesh-shader extensions are not sufficient without the complete stage, pipeline, validation, and draw path.
- No separate native compute/transfer queue. Compute work is serialized on the same context stream as graphics and copies.
- Descriptor indexing, partially-bound, update-after-bind, and variable-count descriptors are unsupported. Partially-bound descriptors require deterministic zero-unbinding and descriptor snapshot semantics that are not implemented.
- Independent blend enable and color-write masks use optional `GL_EXT_draw_buffers2` entry points; per-attachment blend factors/equations use the official Silk.NET ARB draw-buffers-blend wrapper. Independent state is reported only when indexed-state entry points are available.
- Wide lines are reported only when the driver-reported `GL_ALIASED_LINE_WIDTH_RANGE` includes a non-unit width; pipeline widths outside that range are rejected.
- Depth-bounds testing is optional and requires `GL_EXT_depth_bounds_test`.
- Depth-bias clamping is optional and requires `GL_ARB_polygon_offset_clamp`; non-zero clamps remain rejected without it.
- SPIR-V ingestion is optional and requires `GL_ARB_gl_spirv`; GLSL source compilation remains available independently.
- Debug output uses `GL_KHR_debug` (core in modern desktop OpenGL); it is diagnostic output, not GPU-based validation.
- Sparse resources, timestamp-query reporting, and multi-viewport are not advertised: they require complete resource residency/query-result/common-command contracts, not just extension detection.
- Shared host GL-state preservation is not implemented; exclusive GL-state ownership is required.
- Physical adapter IDs, PCI IDs, dedicated memory size, and system-wide adapter enumeration are unknown through the injected-context contract.

### Tests and verification

The ordinary graphics test suite includes hardware-independent tests for provider registration/availability, presentation drawable/present-mode validation, texture targets, sampler translations, descriptor slot allocation, pipeline enum/vertex-format mappings, compute dispatch limit validation, resource barriers, render-target validation, mapped-memory lifetime, SPIR-V validation, debug-option validation, line-width validation, blend-state validation, and optional pipeline-feature validation. GitHub Actions **Graphics validation run #141** completed successfully for the current PR head (`3eaaa7839329deec9e5a2d6cd854c963fc8d55d5`). This is a CI result; it is not an actual-driver run.

**Actual-driver baseline (maintainer-verified before this optimization pass):** the OpenGL smoke test, device creation, shader compilation, GPU resource allocation, offscreen drawing, GPU resource copying, fence completion, presentation, swapchain resizing, and graphics-resource/device disposal have all successfully passed against an actual OpenGL driver. These are established results and are not described as untested. This documentation update does not claim that the current editing environment reran those tests. The backend has no mandatory windowing package; the host supplies the OpenGL 4.5 Core context provider.

### Allocation and performance notes

- Buffer mappings use `glMapNamedBufferRange`/`glUnmapNamedBuffer` and an invalidatable `MemoryManager<byte>` over the native mapping; the normal path no longer uses a per-map staging array and DSA readback/writeback copies.
- Command recording reuses command-list-owned `List<T>` storage for commands, barrier resource references, render-target views, and binding snapshots. Snapshot values are copied into command-list storage so later descriptor-set mutation does not rewrite recorded values.
- Submission rents command-list reference arrays from `ArrayPool<T>` and retains them until the completion sync is observed. Pending entries are value types held in a reusable list.
- Fence sync points use a FIFO `Queue<T>` because values are monotonic; completed syncs are deleted from the head without a temporary cleanup collection.
- Context activation no longer allocates an `OpenGLContextLease` per `WithContext` call, and the `Action<GL>` overload no longer routes through an intermediate `Func<GL, T>`. Callback-based call sites can still allocate delegates/closures; zero-allocation context execution has not been established.
- No before/after allocation benchmark results are currently recorded in the repository. Do not infer allocation numbers from the structural changes above. Warmed allocation measurements for command recording, submission, fences, mapping, and context acquisition remain follow-up work.
