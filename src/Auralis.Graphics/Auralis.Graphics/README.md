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

This project currently provides common descriptors, validation, provider/device contracts, command/pipeline/synchronization interfaces, and presentation interop contracts. It does **not** create native devices, allocate GPU memory, record native commands, compile shaders, create native windows/surfaces, or implement a Direct3D 12, Vulkan, or Metal backend. Those responsibilities belong to future backend and platform integration work.


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

The device makes the supplied context current before loading Silk.NET's `GL` dispatch. It checks the actual version and Core Profile before constructing the Silk.NET extension wrappers. The same provider is used to reactivate the context for each native operation and submission; context access is serialized by the device. A dispatch object is never shared with an unrelated context.

Context-object ownership and GL-state ownership are separate:
- `OpenGLContextOwnership.Borrowed`: Auralis never disposes the host context. The provider is not disposed by the device.
- `OpenGLContextOwnership.Owned`: the provider has transferred its lifetime to the device; device disposal calls the provider's `Dispose`.
- `OpenGLContextStateOwnership.Exclusive` is required. The initial backend does not save/restore all GL state. The host must not issue unrelated GL commands on the context while it is attached to Auralis. `Shared` state ownership is rejected instead of silently corrupting host state.

The context provider owns the meaning of its context identity and must guarantee that its resolver and activation methods refer to that same context. A drawable is optional and `SupportsPresentation` is only a context-provider property; the current backend does not yet implement presentation surfaces or swapchains.

### Implemented functionality

| Area | Current implementation |
| --- | --- |
| Device/provider | Explicit registration, 4.5 Core validation, vendor/renderer/version/driver strings, queried limits and capabilities |
| Formats | Silk.NET internal-format queries and per-format operation/sample-count capability snapshots; unsupported formats remain unsupported |
| Buffers | Buffer allocation, usage validation, copy, staging-backed Map/Unmap, invalidation of mapped memory after Unmap |
| Textures | Immutable 1D/2D/3D/cube storage, arrays and multisample 2D storage; texture views via `ARB_texture_view` |
| Samplers | Sampler objects with filtering, addressing, compare, LOD, border color, and extension-gated anisotropy |
| Shaders | GLSL source compile through Silk.NET; compile log surfaced; `main` entry point |
| Descriptors | Uniform/storage buffers, sampled/storage textures, samplers, and combined texture/sampler sets; explicit deterministic binding-point mapping |
| Graphics/compute pipelines | Linked monolithic graphics or compute program; DSA vertex-array object for conventional graphics pipelines; supported geometry/tessellation stages; fixed raster/depth/stencil/blend state |
| Rendering/compute commands | Deferred render-target binding, viewport/scissor, typed clears, vertex/index binding, direct/indexed/indirect draws, direct/indirect compute dispatch |
| Copy/barriers | Deferred buffer and whole-texture copies plus destination-access-based OpenGL memory barriers |
| Synchronization | GL sync objects, CPU wait/polling, and GPU-side wait for an already-created fence value |
| Queues | Logical graphics/compute/copy queues sharing one serialized context command stream; no physical parallel queues are claimed |

The backend reports OpenGL implementation strings in `GraphicsDeviceInfo`. The injected context is already selected, so system-wide physical adapter enumeration is unavailable: `EnumerateAdaptersAsync` returns an empty list rather than fabricating adapter IDs, PCI identifiers, memory sizes, or adapter types. Unknown limits remain zero. Format support is derived from the active context's internal-format operation queries and advertised extensions; it is not inferred from the enum alone.

### Binding-point mapping

OpenGL has no Vulkan-style descriptor-set object. Auralis maps common sets and bindings to OpenGL binding points deterministically:
1. Process descriptor sets in ascending set index.
2. Process bindings within each set in ascending binding number, independent of the order used to construct the common description.
3. Maintain independent slot sequences for uniform-buffer bindings, storage-buffer bindings, texture units (sampled textures, samplers, and combined texture/sampler entries), and image units (storage textures).
4. A descriptor array consumes consecutive slots in its namespace.

GLSL must use the resulting numeric binding in its `layout(binding = N)` declaration. This backend does not rewrite GLSL source to translate a `set` qualifier. For example, UBO binding points are assigned independently from texture units; the same integer may therefore occur in two different namespaces. Unsupported descriptor flags (partially bound, update-after-bind, variable count) are rejected. A binding set must be fully populated before it is captured into a command list.

### Command execution and queue model

OpenGL does not expose native command buffers. Auralis uses CPU-side typed deferred command records: recording methods validate and retain arguments without invoking GL; submission activates the provider's context and executes commands in order. Queue submission associates a GL sync object with the submitted lists. A list becomes reusable only after the submission is observed complete. Referenced native resources are not physically deleted while submissions are pending.

The graphics, compute, and copy queue objects are logical roles over one context's ordered command stream. They are serialized and do not promise independent graphics, compute, or transfer hardware queues. Compute pipeline creation, direct dispatch, and indirect dispatch are implemented when the required OpenGL 4.3+ compute functionality is present. The backend advertises a logical compute queue in that case, but does not advertise separate compute/transfer queue features.

The fence abstraction is implemented with ordered GL sync objects. CPU waits use client waits; queue waits enqueue a GL server-side wait and do not block the CPU. A queue wait requires the requested fence value to have already been signaled/submitted, because OpenGL cannot enqueue a wait on a future sync object that does not yet exist. The implementation provides monotonic timeline-style values on top of individual GL sync objects; it does not claim the native Vulkan timeline-semaphore feature.

Resource barriers are translated into GL memory-barrier bits based on destination access. Texture layouts in the common barrier description remain semantic hints, not native OpenGL image layouts. The backend uses broad `AllBarrierBits` only for generic memory read/write intents where a narrower consumer cannot be identified.

### Explicit limitations

- No OpenGL ES support.
- No native context/window creation or platform integration is included.
- No presentation surface, swapchain, default-framebuffer acquired-image abstraction, resize/recreation, or swap-buffer execution is implemented yet. Even when a context provider reports a drawable, callers must not assume a usable Auralis presentation surface exists.
- No mesh/task shader pipeline support.
- No separate native compute/transfer queue. Compute work is serialized on the same context stream as graphics and copies.
- Descriptor indexing and partially-bound/update-after-bind/variable-count descriptors are unsupported.
- The current blend implementation supports per-color-attachment blend factors/equations, but blend enable and color-write mask must be identical for all color attachments in a pipeline; differing values are rejected.
- Wide lines, depth bounds, sparse resources, timestamp queries, multi-viewport, and mesh/task features are not advertised until their execution paths are implemented.
- The GLSL source is compiled directly by the driver; SPIR-V ingestion is not implemented.
- Shared host GL-state preservation is not implemented; exclusive GL-state ownership is required.
- Physical adapter IDs, PCI IDs, dedicated memory size, and system-wide adapter enumeration are unknown through the injected-context contract.

### Tests and verification

The ordinary graphics test suite includes hardware-independent tests for provider registration/availability, texture targets, sampler translations, descriptor slot allocation, pipeline enum/vertex-format mappings, compute dispatch limit validation, resource barriers, render-target validation, and mapped-memory lifetime. CI builds the graphics project and runs this test suite without requiring a GPU or display.

No real-context hardware smoke test has been run in this environment. A host/platform integration should gate such a test on an available desktop OpenGL 4.5 Core context and exercise device creation, shader compilation, resource allocation, an offscreen draw/copy, fence completion, and disposal. The backend currently has no mandatory windowing package; a test host may supply any suitable context provider.
