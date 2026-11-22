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
- `Backends/OpenGLES` contains the separate OpenGL ES 3.1+ implementation and its Silk.NET context-provider integration.
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

CPU mapping is explicit and transient, not a persistent allocation. `Read` and `ReadWrite` preserve existing contents; `Write` is discard/write-only, so the previous contents of the mapped range are undefined and callers must initialize every byte they rely on. Use `ReadWrite` or another read-preserving strategy for partial updates that need old bytes. A `MemoryHandle` pin must be released before `Unmap` or resource disposal can finish, and previously acquired `Span<byte>` values must not be used after `Unmap` or disposal. Backends perform required cache maintenance and may reject mapping when the chosen access intent is not CPU-visible.

Shader descriptions carry an artifact-format identifier and opaque bytes. DXIL, SPIR-V, Metal libraries, source artifacts, and application-defined formats are not assumed to be interchangeable. Shader compilation is outside this library.

## Commands and synchronization

A command list is created from a queue, then moves through `Initial → Recording → Executable → Pending`. The backend must enforce the legal transition rules and report completion before a pending list is reset or reused. `GraphicsCommandListLifecycle` provides a small, allocation-free state tracker for implementations and hardware-free validation. Command recording is caller-thread-owned; separate command lists may be recorded concurrently. Queue submission validates that lists belong to the same device and a compatible queue.

Graphics, compute, and copy queues are requested through `IGraphicsDevice.GetQueue` and are represented by `IGraphicsCommandQueue`. `IGraphicsQueue` is the shared device-owned queue identity (`Device` and `Index`). Presentation candidates are obtained through `IGraphicsDevice.GetPresentationQueue` and may implement only `IGraphicsQueue`; they do not promise command-list creation, submission, or queue waits. Always query `IGraphicsPresentationSurface.SupportsPresentation` for the actual target before passing a candidate to `CreateSwapChain`. A backend may return a command-capable queue as a presentation candidate when appropriate. `IGraphicsCommandList` defines render, compute, copy, and indirect operations; implementations reject operations illegal for the queue's command role or current recording state.

`GraphicsResourceBarrier` describes source/destination execution stages and access intents. GraphicsPipelineStage.AllGraphics includes indirect drawing, vertex input, all declared graphics shader stages, depth/stencil, and color attachment output; each stage is also individually selectable. The barrier validator uses an explicit compatibility table to check access flags against resource kind, declared buffer/texture usage, and source/destination stages. Host buffer access is also checked against the buffer's CPU memory-access intent.

Texture barriers specify semantic layouts. Layout transitions are checked against declared texture usage: sampled, color/depth-stencil attachment, copy-source/destination, and presentation layouts require their corresponding usage flags. General has no additional usage requirement. These are deliberately not Direct3D 12 resource states; backends translate them into their own hazard, layout, visibility, and synchronization model.

Barriers may target an entire resource or a precise range: GraphicsBufferRange selects a byte interval, while GraphicsTextureSubresourceRange selects aspects, mip levels, and array layers. A null range means the entire resource. Range bounds are validated against the resource description, including format-aware color/depth/stencil aspect validation. Barriers currently accept buffers and underlying textures only; texture views, samplers, and shaders are rejected. To synchronize a view's subresources, specify its parent texture and the corresponding subresource range. A backend may conservatively widen a valid range when required by its native API, but must preserve the requested dependency. Fences expose timeline-style CPU waits/signals and queue-side GPU waits. CPU `Signal(value)` is monotonic and cannot overtake an earlier pending GPU signal: an attempted CPU signal greater than a pending GPU signal is rejected without deleting or superseding that GPU sync. GPU completion values advance only after the corresponding native sync reports completion. Callers must not use a CPU signal as a shortcut to claim that still-pending GPU work has completed. Backend-specific extension interfaces may be added later when a native capability cannot be represented faithfully.

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

This project provides backend-neutral graphics descriptors, validation, device/command/pipeline/synchronization contracts, and presentation interop. The same `Auralis.Graphics` assembly contains two Silk.NET implementations: desktop OpenGL 4.5 Core and OpenGL ES 3.1+. Both execute recorded commands through injected, host-owned context providers and create no native windows or contexts. Direct3D 12, Vulkan, and Metal implementations remain outside the current project state.


## OpenGL backend

The desktop backend is implemented in `Backends/OpenGL` and targets **OpenGL 4.5 Core Profile**. The separate `Backends/OpenGLES` backend targets **OpenGL ES 3.1+**. Both use the existing graphics contracts and Silk.NET 2.23.0 bindings; neither adds OpenTK, handwritten P/Invoke entry points, or Silk.NET.Windowing dependencies.

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

The hot `WithContext` paths use inline activation/release scopes, avoiding per-operation context-lease allocations; the `Action<GL>` overload also avoids an intermediate `Func` wrapper. Per-device, per-thread activation depth makes nested calls reentrant: only the outermost acquisition calls `MakeCurrent`, and only the final release calls `ReleaseCurrent`. The device makes the supplied context current before loading Silk.NET's `GL` dispatch and checks the actual version/Profile before constructing extension wrappers. Activation and release are balanced through `try/finally`, and device operations remain serialized by the context gate. A dispatch object is never shared with an unrelated context.

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
| Graphics/compute pipelines | Linked monolithic graphics or compute program; DSA vertex-array object for conventional pipelines; geometry/tessellation stages; conditional EXT or NV mesh/task stages with their distinct GLSL dialects and dispatch contracts; fixed raster/depth/stencil/blend state |
| Rendering/compute commands | Deferred render-target binding, viewport/scissor, typed clears, vertex/index binding, direct/indexed/indirect draws, direct/indirect mesh-task draws, and direct/indirect compute dispatch |
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

GLSL must use the resulting numeric binding in its `layout(binding = N)` declaration. This backend does not rewrite GLSL source to translate a `set` qualifier. For example, UBO binding points are assigned independently from texture units; the same integer may therefore occur in two different namespaces. `PartiallyBound` is supported for fixed-count bindings: empty array elements are permitted in the recorded snapshot and are explicitly unbound at execution (UBO/SSBO binding base, texture unit, sampler, or image unit as appropriate). Partially-bound bindings do not permit a half-populated combined texture/sampler element; each element must be wholly populated or wholly empty. Shaders must not access array elements left unpopulated by the application. `UpdateAfterBind` and `VariableCount` remain unsupported and are rejected. A descriptor set snapshot is still immutable with respect to later host updates.

### Command execution and queue model

OpenGL does not expose native command buffers. Auralis uses CPU-side typed deferred command records: recording methods validate and retain arguments without invoking GL; submission activates the provider's context and executes commands in order. Each command list keeps one atomic native reference for every distinct GL resource referenced by its recorded commands or captured descriptor snapshots. Public resource disposal closes the owner reference and rejects new retains, but an already-recorded list can still use the retained native handle. Reset and disposal of an unsubmitted list release those references; a pending list cannot be reset, and disposing it waits for GPU completion. Submission command-list reference arrays are rented and returned only after the associated GL completion sync has signaled. The executable-to-pending transition is serialized with command-list disposal under the device context gate. Submission failure after issuing GL work finishes that work before returning lists to an executable state and releasing submission ownership. Device disposal finishes and drains outstanding submissions, releases registered native objects while the context is still current, and only then tears down GL dispatch/context ownership.

The graphics, compute, and copy queue objects are logical roles over one context's ordered command stream. They are serialized and do not promise independent graphics, compute, or transfer hardware queues. Compute pipeline creation, direct dispatch, and indirect dispatch are implemented when the required OpenGL 4.3+ compute functionality is present. The backend advertises a logical compute queue in that case, but does not advertise separate compute/transfer queue features.

The fence abstraction is implemented with ordered GL sync objects stored in a reusable FIFO queue. CPU `Wait` uses native client waits and a deadline-bounded attempt to acquire the device context gate; gate contention, native polling, and cancellation are included in the timeout contract. Queue `Wait` enqueues a GL server-side wait without blocking the CPU, but requires a corresponding synchronization object to have already been created by a GPU submission or CPU signal. GL cannot enqueue a wait for an unknown future sync object. `CompletedValue` performs native polling under the context gate and may block behind other device operations; it is not a cached/nonblocking property. These are timeline-style values over individual GL sync objects, not native Vulkan timeline semaphores.

Resource barriers are translated into GL memory-barrier bits based on destination access. Texture layouts in the common barrier description remain semantic hints, not native OpenGL image layouts. The backend uses broad `AllBarrierBits` only for generic memory read/write intents where a narrower consumer cannot be identified.

### Explicit limitations

- No robustness/reset-status recovery or typed device-lost reporting is implemented. The common exception contract has no device-lost exception, and the backend does not currently poll `GL_KHR_robustness`/core reset status. Ordinary GL errors are not reclassified as device loss.
- No native context/window creation or platform integration is included. The separate GLES backend is documented below; this desktop backend remains OpenGL 4.5 Core only.
- The backend does not expose the native default framebuffer as an `IGraphicsTexture`. Swapchain images are Auralis-owned offscreen textures; presentation blits one to framebuffer 0 and then calls the provider's swap operation.
- The injected provider/platform integration must correctly identify the exact drawable, support and apply the requested present mode, and implement buffer swapping. No WGL/GLX/EGL/native Apple context or window creation code is included.
- The current swapchain presents a color-only offscreen image ring; it does not expose default framebuffer depth/stencil attachments or native swapchain image handles.
- Mesh/task shader pipelines are conditionally supported through either `GL_EXT_mesh_shader` or `GL_NV_mesh_shader`. Each GLSL mesh/task shader must enable exactly one matching extension dialect, and a pipeline cannot mix EXT and NV stages. EXT dispatch uses the native three-dimensional API and queried per-axis/total limits. NV direct dispatch uses `glDrawMeshTasksNV(first, count)`, validates the one-dimensional count against `GL_MAX_DRAW_MESH_TASKS_COUNT_NV`, and remains available even when both extensions are present. NV single- and multi-draw indirect dispatch use the native two-field `{ count, first }` command record; EXT indirect dispatch retains the three-dimensional `{ groupCountX, groupCountY, groupCountZ }` payload. Mesh/task SPIR-V is rejected until its EXT-versus-NV execution model can be identified and validated rather than guessed.
- No separate native compute/transfer queue. Compute work is serialized on the same context stream as graphics and copies.
- Descriptor indexing, update-after-bind, and variable-count descriptors are unsupported. `PartiallyBound` is implemented for fixed descriptor counts: missing UBO/SSBO slots are cleared with `glBindBufferBase(..., 0)`, texture/sampler slots with zero texture/sampler bindings, and storage-image slots with a zero image binding. A partially populated combined texture/sampler element is rejected; an element must be either fully populated or entirely empty.
- Independent blend enable and per-attachment color-write masks use optional indexed draw-buffer entry points; the backend uses the resolved extension wrapper only when the driver exposes the required functions. Clear operations temporarily enable writes and restore the pipeline's original attachment-specific masks. Independent state is never emulated through unavailable indexed entry points.
- Wide lines are reported only when the driver-reported `GL_ALIASED_LINE_WIDTH_RANGE` includes a non-unit width; pipeline widths outside that range are rejected.
- Depth-bounds testing is optional and requires `GL_EXT_depth_bounds_test`.
- Depth-bias clamping is optional and requires `GL_ARB_polygon_offset_clamp`; non-zero clamps remain rejected without it.
- SPIR-V ingestion is optional and requires `GL_ARB_gl_spirv`; GLSL source compilation remains available independently.
- EXT mesh/task stages require `GL_EXT_mesh_shader`, its direct/indirect entry points, and non-zero mesh/task per-axis and total work-group limits. NV mesh/task stages require `GL_NV_mesh_shader`, the native direct entry point, and non-zero `GL_MAX_DRAW_MESH_TASKS_COUNT_NV`. GLSL source must explicitly enable the matching extension; the desktop native stage tokens have the same values, but the GLSL built-ins and dispatch semantics do not. A pipeline cannot mix EXT and NV mesh/task stages. EXT pipelines support direct and 3D indirect dispatch (including the native multi-indirect function when present). NV pipelines support direct one-dimensional dispatch and native single/multi-draw indirect dispatch using the NV `{ count, first }` record. The indirect argument size is validated against the active pipeline dialect. Mesh/task SPIR-V is currently rejected because this path does not yet inspect the module execution model to distinguish EXT from NV.
- Debug output uses `GL_KHR_debug` (core in modern desktop OpenGL); it is diagnostic output, not GPU-based validation. Callback output, notification filtering, and command debug groups are implemented. Object labels are applied to buffers, textures, texture views, samplers, shaders, and graphics/compute pipeline program objects (plus graphics vertex arrays).
- Sparse resources, timestamp-query reporting, and multi-viewport are not advertised: they require complete resource residency/query-result/common-command contracts, not just extension detection.
- Robustness and device-loss recovery remain unsupported: this backend does not query `GL_KHR_robustness` or core graphics-reset status, and the common API has no typed device-lost exception/lifecycle contract. Ordinary API errors remain ordinary graphics errors; they are not labeled device loss.
- Shared host GL-state preservation is not implemented; exclusive GL-state ownership is required. Command execution owns framebuffer, copy/indirect-buffer, and scissor state while it runs; temporary bindings are reset to a clean baseline (framebuffer and transient buffer bindings zero, scissor disabled) rather than restoring arbitrary host bindings. A host must not interleave its own GL state mutations with backend command execution. Binding framebuffer zero is only a cleanup baseline; offscreen rendering always binds and validates its own framebuffer and does not depend on a usable default framebuffer.
- Physical adapter IDs, PCI IDs, dedicated memory size, and system-wide adapter enumeration are unknown through the injected-context contract.

### Tests and verification

Run the complete test suite with the same command locally and in GitHub Actions:

```bash
dotnet test Auralis.slnx --configuration Release
```

The solution contains three discoverable xUnit test projects:

- `Tests/Auralis.Graphics.Tests` contains hardware-independent contract, validation, capability-profile, and resource-lifetime tests.
- `Tests/Auralis.Graphics.OpenGL.IntegrationTests` contains desktop OpenGL 4.5 Core driver tests.
- `Tests/Auralis.Graphics.OpenGLES.IntegrationTests` contains OpenGL ES 3.1+ driver tests.

The two integration projects are ordinary .NET test projects. Their fixtures automatically attempt to create a hidden GLFW context and initialize the corresponding Auralis graphics device. They do not require environment variables, special command-line flags, or a separate `dotnet run` invocation.

#### Windows hybrid-GPU selection for desktop OpenGL tests

The GLFW test provider requests an OpenGL 4.5 Core context, but it does not select a physical GPU. On Windows hybrid-graphics systems, the active adapter is chosen by Windows graphics policy for the process that creates the GLFW context. The desktop integration-test output therefore prints the actual OpenGL vendor, renderer, driver/API version, mesh-shader extension enumeration, native mesh entry-point availability, and `Environment.ProcessPath`.

To request the discrete GPU for a hardware-test run:

1. Run a desktop OpenGL integration test and copy the **exact executable path** printed as `process='...'`. This is the process creating the context; depending on the test runner, it may be `testhost.exe` or another test executable. Do not assume that the `dotnet.exe`, IDE, terminal, or launcher path is the correct one.
2. Open **Windows Settings → System → Display → Graphics**. Add that exact executable as a desktop app, select **Options**, choose **High performance**, and save.
3. Fully exit the test process (and restart the test run/IDE if it owns that process), then rerun the integration tests. GPU preferences are applied when the process starts.

On Windows 10 version 2004 (20H1) and later, including Windows 11, Windows' per-application graphics preference takes precedence over the preferred-graphics-processor setting in NVIDIA Control Panel. Configure the executable in Windows Graphics settings rather than relying only on NVIDIA Control Panel. See [NVIDIA's Windows GPU-preference explanation](https://nvidia.custhelp.com/app/answers/detail/a_id/5035).

The reported `GL_RENDERER` and `GL_VENDOR` identify the adapter/driver actually backing the context. A setting change is not proof that the discrete GPU was selected: rerun the test and inspect those values. The backend must continue to use only the extensions advertised by that active context. An NVIDIA GPU elsewhere in the system does not make `GL_NV_mesh_shader` available to an OpenGL context created on Intel or another adapter.

To run only the four desktop mesh-shader integration tests after changing the GPU preference:

```bash
dotnet test Tests/Auralis.Graphics.OpenGL.IntegrationTests/Auralis.Graphics.OpenGL.IntegrationTests.csproj --configuration Release --filter "FullyQualifiedName~MeshShader"
```

When the active adapter genuinely does not advertise a required extension, the corresponding test remains skipped. Neither EXT nor NV mesh-shader support is assumed based on vendor name alone.

When a compatible context cannot be created because the host has no usable display, GLFW native library, driver, or required API version, the affected hardware tests are reported by xUnit as **skipped**, with the reason attached to each test. Optional capabilities are checked independently, so only cases requiring an unavailable extension, format, or limit are skipped. Unexpected initialization errors, test assertion failures, and graphics-driver errors remain **failed** tests; they are not converted into skips. A skipped test is never counted as passed.

The desktop integration cases cover context activation and fence behavior, resource retention and deferred destruction, buffer/texture copies, compute descriptors and dispatch, conventional graphics draws, and capability-specific EXT/NV mesh and task shaders. The GLES cases cover context/fence behavior, staging maps, resource retention, compute and descriptor operations, texture/sampler allocation, offscreen rendering, MRT and typed clears, indirect draws, texture views, native image copies or the framebuffer-blit fallback, and multisample sampling. Capability-specific skip behavior is preserved as individual test results.

GitHub Actions first builds the BenchmarkDotNet project in Release, then runs `dotnet run --project Benchmarks/Auralis.Graphics.OpenGL.Benchmarks/Auralis.Graphics.OpenGL.Benchmarks.csproj --configuration Release -- --list flat` to verify benchmark discovery without invoking hardware setup, and finally runs `dotnet test Auralis.slnx --configuration Release`. A headless CI machine without a usable graphics context still executes the hardware-independent suite and reports the hardware-dependent integration cases as skipped. To validate backend behavior against a real driver, run the same test command on a machine with the corresponding desktop OpenGL and/or GLES context available and inspect the test results.

**Historical CI record:** [Graphics validation run #467](https://github.com/mahdikshk/Auralis/actions/runs/45020934100) predates this test-project conversion. It built both integration runners but executed only the 242 hardware-independent tests; it is not evidence that the current integration test projects have run against a driver. [Graphics validation run #530](https://github.com/mahdikshk/Auralis/actions/runs/45039290059) validates the benchmark build, BenchmarkDotNet discovery, and current solution test suite.

BenchmarkDotNet remains separate from the test suite. The graphics workflow builds the benchmark project in Release and lists its discovered methods, but neither operation executes benchmark methods or creates a graphics context. Run one of the commands in the OpenGL hardware benchmarks section on a machine with a compatible desktop OpenGL 4.5 Core or OpenGL ES 3.1+ driver.

### Allocation and performance notes

- Buffer mappings use `glMapNamedBufferRange`/`glUnmapNamedBuffer` and an invalidatable `MemoryManager<byte>` over the native mapping; the normal path no longer uses a per-map staging array and DSA readback/writeback copies.
- Command recording reuses command-list-owned `List<T>` storage for commands, barrier resource references, render-target views, and binding snapshots. Snapshot values are copied into command-list storage so later descriptor-set mutation does not rewrite recorded values. A deduplicated per-command-list retention set holds one atomic native reference per distinct native resource, and releases those references on reset/disposal after any pending submission has completed. The retention set uses a list plus a reference-identity hash set; no allocation or throughput benchmark has yet quantified its overhead.
- Submission rents command-list reference arrays from `ArrayPool<T>` and retains them until the completion sync is observed. Pending entries are value types held in a reusable list.
- The device tracks only live native resources in a reference-identity `HashSet<T>` protected by the existing context gate. A resource leaves that registry when its native deletion succeeds or ownership is handed to the deferred-deletion queue, avoiding an unbounded history of already-disposed managed wrappers.
- Fence sync points use a FIFO `Queue<T>` because values are monotonic; completed syncs are deleted from the head without a temporary cleanup collection.
- Context activation no longer allocates an `OpenGLContextLease` per `WithContext` call, and the `Action<GL>` overload no longer routes through an intermediate `Func<GL, T>`. Callback-based call sites can still allocate delegates/closures; zero-allocation context execution has not been established.
- A hardware-dependent BenchmarkDotNet project lives in `Benchmarks/Auralis.Graphics.OpenGL.Benchmarks` and contains equivalent desktop OpenGL 4.5 Core and OpenGL ES 3.1+ benchmark classes. It uses `MemoryDiagnoser` and measures empty/copy command recording, submission plus fence wait, completed-fence reads, submit-and-poll, and buffer/texture creation and disposal. See [OpenGL hardware benchmarks](#opengl-hardware-benchmarks) for commands and prerequisites. Results are specific to the selected driver, GPU, CPU, OS, context provider, runtime, and power/clock state.
- The graphics validation workflow builds the benchmark project in Release and verifies its BenchmarkDotNet listing, but it does not execute the benchmark methods. No before/after allocation or throughput results are claimed until the benchmark suite has been run on named hardware; the harness does not itself constitute a measurement.


### Performance measurements and unverified optimizations

The BenchmarkDotNet project is hardware-dependent and reports managed allocations. A successful CI build of that project is not a benchmark run. No comparable before/after measurements have been collected in this continuation because no compatible OpenGL hardware context is available in the validation environment. Context-gate contention, per-resource reclamation activation, FBO creation/reuse, GLES temporary blit FBOs, barrier precision, and command-record copy costs therefore remain measurement targets rather than claimed optimizations. The verified implementation changes in this continuation are correctness-oriented: nested context activation is reference-counted, fence waits bound context-gate acquisition by the remaining timeout, and write-only GLES staging maps no longer activate the GL context.

## OpenGL ES backend

Auralis includes a separate OpenGL ES backend under `Backends/OpenGLES`. It implements the existing `IGraphicsBackendProvider`, `IGraphicsDevice`, resource, pipeline, command, synchronization, and presentation contracts. It does not add a second graphics API and does not change the desktop OpenGL implementation.

### Host context contract

The backend requires a host-created, currentable OpenGL ES context reporting **OpenGL ES 3.1 or newer**. The host implements `IOpenGLESContextProvider` and supplies:

The host supplies exclusive ownership of the GLES context state. Command execution resets its temporary framebuffer, copy/indirect-buffer bindings, and scissor-enable state to a clean baseline rather than preserving arbitrary host GL state.

- a stable context identity and ownership mode;
- exclusive GLES state ownership (the backend does not save and restore arbitrary external GL state);
- `MakeCurrent`, `ReleaseCurrent`, and a context-specific `GetProcAddress`;
- optional drawable compatibility, present-mode, and buffer-swap operations.

The backend does not create EGL contexts, windows, Android surfaces, or platform-specific objects. The same existing registry and device factory select the provider:

```csharp
var registry = new GraphicsBackendProviderRegistry();
registry.Register(new OpenGLESGraphicsBackendProvider(hostGlesContextProvider));

using IGraphicsDevice device = await GraphicsDeviceFactory.CreateAsync(
    registry,
    new GraphicsDeviceOptions
    {
        Backend = GraphicsBackend.OpenGLES,
        RequiredQueues = GraphicsQueueCapabilities.Graphics |
                         GraphicsQueueCapabilities.Compute
    });
```

`hostGlesContextProvider` is an application/platform object implementing `IOpenGLESContextProvider`; it is not supplied by the graphics assembly. A borrowed provider remains owned by the host. An owned provider is disposed by the device, including when device initialization fails.

### GLES capability model

- ES 3.1 is the minimum accepted context. The device reads the actual version, vendor, renderer, extension list, and implementation limits after activating the supplied context.
- Compute shaders, indirect drawing, shader-storage buffers, image load/store, memory barriers, and core 2D multisample texture storage use the ES 3.1 path. Multisample sample-count flags are queried per format with the internal-format query API; the backend does not infer support from power-of-two values.
- ES 3.2 adds core geometry and tessellation stages. Patch control points are applied through the GLES patch-parameter operation and checked against the queried `GL_MAX_PATCH_VERTICES`; devices without tessellation support continue to reject tessellation pipelines.
- The GLES core framebuffer path queries both `GL_MAX_COLOR_ATTACHMENTS` and `GL_MAX_DRAW_BUFFERS`. Multiple ordered color attachments and depth-only framebuffers are supported within those limits. One common blend state applies to every attachment; independent per-attachment blending requires `GL_EXT_draw_buffers_indexed` and its resolved native functions.
- Typed unsigned/signed integer color clears use the matching GLES integer clear operations and require the corresponding integer attachment format. Normalized and floating formats reject integer clears.
- Multi-draw indirect uses `GL_EXT_multi_draw_indirect` when both native entry points resolve. Without that extension, multiple indirect commands are executed as a CPU loop of native single-indirect calls over the GPU-resident argument buffer; the fallback does not read argument data back and does not advertise the native `MultiDrawIndirect` feature.
- Direct base-vertex drawing requires `GL_EXT_draw_elements_base_vertex`. Base-instance drawing requires `GL_EXT_base_instance` and its base-vertex dependency. These features are detected separately and unavailable entry points are never called.
- Cube-map arrays use core GLES 3.2 or `GL_OES_texture_cube_map_array`; the layer-face count must be a multiple of six. GLES has no native 1D texture target, so 1D textures remain unsupported.
- Texture views support identity/full-resource views without an extension. Full subresource and format-reinterpreted texture views require `GL_EXT_texture_view` or `GL_OES_texture_view` and the corresponding function address; native view objects retain their parent texture. Without either extension, the backend supports a deliberately narrower framebuffer-only alias for a same-format single mip of a 2D texture or a single layer/mip of a 2D-array texture. These aliases attach the parent storage directly through `glFramebufferTexture2D`/`glFramebufferTextureLayer`, retain the parent, and can be used for render-target clear/draw/readback, but cannot be sampled or bound as storage images. `GraphicsFeature.TextureViews` remains false when the native extension path is unavailable.
- Texture image copies use `GL_EXT_copy_image` or `GL_OES_copy_image` when available. This path copies each mip and supports the array/3D/cube subresource layout handled by the native copy operation, without requiring color-attachment usage. When neither extension is available, a narrowly scoped framebuffer-blit fallback remains limited to compatible single-sample, single-layer, single-mip RGBA8 2D textures that really are framebuffer attachments.
- Format support is reported separately for sampling, filtering, color/depth attachment, storage, blending, copy use, and per-format sample counts. The mapping includes the normalized, signed-normalized, integer, float, sRGB, and depth formats that this implementation can allocate; extension-dependent float color attachments and float filtering remain gated. An internal format being mappable does not itself advertise every usage.
- Timeline fences use GLES sync objects. Logical graphics, compute, and copy queue roles are serialized over one GLES command stream; they are not physically independent hardware queues.
- Anisotropic filtering is advertised only when `GL_EXT_texture_filter_anisotropic` is present and the driver-reported maximum is queried.
- With `GL_EXT_color_buffer_float`, the R16, RG16, RGBA16, R32, RG32, and RGBA32 floating-point formats receive color-attachment support. Without the corresponding color-buffer extension, these formats are not advertised as color-renderable; sampled/storage support is tracked separately.
- Presentation is advertised only when the injected context provider reports a compatible host drawable. A generic currentable context does not imply presentation support.
- GLES mesh/task shaders use the distinct `GL_EXT_mesh_shader` contract when the extension and all required direct/indirect entry points resolve. ESSL 3.10 or newer must enable `GL_EXT_mesh_shader`; mesh/task work-group count and total-count limits are queried from the driver. Native three-dimensional direct/indirect dispatch is supported. Native multi-draw mesh indirect is optional; without it, multiple native indirect calls are issued using GPU-resident offsets. This does not imply desktop NV semantics. GLES SPIR-V ingestion remains unsupported: desktop `GL_ARB_gl_spirv` is not a GLES ingestion mechanism, and this backend has no verified native GLES SPIR-V path.
- GLES does not claim descriptor indexing, update-after-bind, variable-count descriptors, persistent mapping, sparse residency, timestamp-query results, or indexed multi-viewport. Those features either lack a native implementation here or need common API semantics that the existing contracts do not yet expose.

### Implemented operations

- Buffers use GLES bind-to-edit allocation and transient `MapBufferRange`/unmap mapping through managed staging memory. `Read` and `ReadWrite` snapshot the requested GPU range during `Map`; `Write` allocates staging bytes without acquiring the GL context; `Write` and `ReadWrite` upload the entire mapped range through `BufferSubData` during `Unmap`. The read snapshot is not a live view. Before consuming GPU-produced data, callers must order and complete the producing work (for example, wait for its fence). All mapped memory views are invalid after `Unmap` or disposal; persistent mapping and desktop buffer-storage APIs are not used.
- Immutable texture storage supports GLES 2D, 2D-array, 3D, cube, cube-array (3.2/OES), and eligible 2D multisample textures. The GLES view model supports identity views, extension-backed mip/layer/format subresource views, and the restricted non-sampleable framebuffer-only aliases described above; unsupported target/view combinations are rejected.
- Supported texture formats include the normalized and sRGB formats explicitly mapped by the backend, 8/16/32-bit signed and unsigned integer formats, signed-normalized formats, 16/32-bit floating-point formats, and D16/D24S8 plus ES 3.2 D32F/D32FS8 depth formats. Per-format usages remain restricted by GLES version, extension set, and implementation capabilities. `BGRA8UNorm` and `BGRA8UNormSrgb` are not mapped or advertised; the common format enum therefore has intentionally incomplete GLES parity. BC/ASTC compressed formats are advertised only with their corresponding S3TC/S3TC-sRGB, RGTC, BPTC, or ASTC extension.
- Samplers use GLES sampler objects. Border-clamp modes are rejected; anisotropy is set only when the extension is available.
- Shader creation compiles GLSL ES source with driver diagnostics. The version directive must be on the first physical source line (horizontal whitespace before it is permitted); leading blank lines and comments before it are rejected. Source must declare `#version 300 es`, `#version 310 es`, or `#version 320 es` as appropriate for the stage and context. Mesh/task shaders require an active `GL_EXT_mesh_shader` directive. Desktop GLSL source and SPIR-V are rejected.
- Binding layouts map uniform buffers, storage buffers, sampled textures, samplers, combined texture/sampler pairs, and storage images to GLES binding slots. Unsupported binding flags and incomplete non-partially-bound descriptors are rejected.
- Render pipelines validate attachment count, color/depth formats, and sample counts against the active render targets. Common blend state is supported across MRT attachments; differing per-attachment state/write masks are accepted only with the indexed-blending extension. Clears temporarily enable writes and then restore each attachment's own mask. GLES 3.2 tessellation uses the native patch parameter and the queried patch-vertex maximum.
- Command lists support buffer copies, EXT/OES image copies and a restricted framebuffer-blit fallback, render-target binding, viewport/scissor, typed floating/unsigned/signed color clears, depth/stencil clears, vertex/index binding, direct/instanced drawing, native or emulated multi-command indirect drawing, extension-gated base-vertex/base-instance drawing, compute dispatch/indirect dispatch, descriptor binds, and memory barriers.
- Recorded and submitted command lists retain referenced native resources, including texture-view names and their parent textures, until reset/disposal or observed GPU completion. Device shutdown finishes the GLES stream and drains pending submissions before native resource deletion.
- When a host drawable is supplied, the presentation surface creates backend-owned color images and framebuffer objects, blits the selected image to the host default framebuffer, and delegates the actual buffer swap to the host provider.

### Remaining GLES limitations

- The GLES backend still uses one context command stream. Logical graphics/compute/copy roles do not represent separate physical hardware queues.
- 1D textures remain unsupported because GLES has no native 1D texture target.
- Robustness/reset-status polling and device-loss recovery are not implemented. The common API has no typed device-lost exception/lifecycle contract, so ordinary GLES errors are not misreported as device loss.
- GLES EXT mesh/task support is extension-gated and depends on `GL_EXT_mesh_shader`, the resolved required entry points, and driver-reported limits. Desktop NV's one-dimensional dispatch and 8-byte indirect command are a separate contract and are not reused by GLES.
- SPIR-V ingestion remains unsupported because the backend has no verified native GLES ingestion extension/path.
- Native multi-draw-indirect is optional. Its CPU-loop fallback preserves GPU-resident indirect-buffer semantics but does not claim native multi-draw feature support. The existing integration case exercises multiple array and indexed commands through whichever path the device exposes; this is not yet a live-driver verification of both native and fallback implementations on separate GPUs.
- Independent per-attachment blending and write masks require `GL_EXT_draw_buffers_indexed`; otherwise the same blend configuration applies across all MRT attachments. Clear operations cannot use unavailable indexed entry points.
- Multi-viewport remains unsupported because the common viewport/scissor commands have no viewport index.
- Descriptor indexing, update-after-bind, and variable descriptor counts remain unsupported by this descriptor model. The current descriptor snapshots are intentionally preserved.
- Sparse-resource residency remains unsupported because the common texture/resource API has no page-commit/decommit and residency contract.
- Timestamp-query result retrieval remains unsupported because the common API has no usable query-result lifecycle, availability, unit, or disjoint-result contract.
The OpenGL ES integration cases are part of the standard solution test command above. They automatically attempt to create an OpenGL ES 3.1+ context and are individually reported as skipped when the host or driver cannot provide that context or a case-specific capability. No separate executable or opt-in setting is required.

## OpenGL hardware benchmarks

The BenchmarkDotNet project provides hardware-backed measurements for both implemented OpenGL backends:

- Desktop OpenGL 4.5 Core: `OpenGLBackendBenchmarks`.
- OpenGL ES 3.1 or newer: `OpenGLESBackendBenchmarks`.

Both classes use the same `MemoryDiagnoser` and `ShortRunJob` configuration and equivalent workloads through the common graphics interfaces. The benchmark methods measure:

- `RecordEmptyCommandList`: empty reusable command-list recording (the per-class baseline).
- `RecordCopyCommand`: recording one 4 KiB buffer copy, including normal command-resource retention, without submitting it.
- `SubmitAndWait`: recording and submitting the copy, signaling a timeline fence, then waiting for GPU completion.
- `ReadCompletedFenceValue`: reading a fence value that has already completed.
- `SubmitAndPoll`: submitting the copy and polling the fence until GPU completion, with a bounded timeout.
- `CreateAndDisposeBuffer`: allocating and disposing a 4 KiB GPU-only copy-source/copy-destination buffer.
- `CreateAndDisposeTexture`: allocating and disposing a 256 × 256 RGBA8 texture with sampled, copy-source, and copy-destination usage.

Global setup creates one hidden hardware context per benchmark process, allocates persistent source/destination buffers and reusable command/fence objects, initializes a known source pattern, and validates one real GPU buffer copy through the supported read mapping API. That readback is not repeated in measured iterations. Setup prints the backend, adapter/device/API information, .NET runtime, operating system, and process architecture; BenchmarkDotNet also reports its execution environment.

### Run desktop OpenGL benchmarks

```bash
dotnet run --project Benchmarks/Auralis.Graphics.OpenGL.Benchmarks/Auralis.Graphics.OpenGL.Benchmarks.csproj --configuration Release -- --filter "*OpenGLBackendBenchmarks*"
```

### Run OpenGL ES benchmarks

```bash
dotnet run --project Benchmarks/Auralis.Graphics.OpenGL.Benchmarks/Auralis.Graphics.OpenGL.Benchmarks.csproj --configuration Release -- --filter "*OpenGLESBackendBenchmarks*"
```

The selected backend is initialized automatically; no benchmark-specific environment variables or manual test-runner options are required. The host must have the native GLFW library, a usable platform/display session capable of creating the hidden context, and a compatible graphics driver exposing desktop OpenGL 4.5 Core or OpenGL ES 3.1+, respectively. Missing native GLFW support, a display/context, or the required API/driver causes benchmark setup to fail with a hardware-prerequisite error; missing hardware is not reported as a successful benchmark.

These are measurements of one specific hardware/software environment, not universal comparisons of the APIs or implementations. Results can vary with GPU/CPU model, graphics driver, operating system, runtime, context creation path, power state, clocks, and competing system load. Compare results only with those environmental differences understood. No arbitrary pass/fail performance thresholds are defined.

The regular graphics validation workflow builds this benchmark project in Release, verifies discovery with `--list flat`, and runs the solution test suite. It does not execute the hardware-dependent BenchmarkDotNet methods or require a physical GPU or display on the CI runner.
