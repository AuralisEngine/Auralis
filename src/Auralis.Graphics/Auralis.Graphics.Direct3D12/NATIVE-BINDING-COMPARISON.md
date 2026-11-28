# Auralis Direct3D 12 Native Binding Comparison

**Document type:** Engineering decision record and source-level comparison  
**Research date:** 2026-11-28  
**Repository:** [AuralisEngine/Auralis](https://github.com/AuralisEngine/Auralis)  
**Pull request audited:** [#15 — Establish Direct3D 12 backend infrastructure](https://github.com/AuralisEngine/Auralis/pull/15)  
**Related roadmap:** [Issue #14 — Direct3D 12 backend implementation](https://github.com/AuralisEngine/Auralis/issues/14)  
**Decision:** Silk.NET is the required and exclusive low-level Direct3D 12 / DXGI binding for Auralis.

---

## Decision summary

Auralis uses the low-level generated API surface in **Silk.NET 2.23.0**:

- `Silk.NET.Direct3D12` **2.23.0**
- `Silk.NET.DXGI` **2.23.0**

Both references are direct dependencies of `Auralis.Graphics.Direct3D12`. The backend-neutral `Auralis.Graphics` contracts and the future higher-level `Auralis.Rendering` layer must not depend on Silk.NET types.

The decision is based on architectural consistency and control of the native boundary—not a claim that Silk.NET is the newest SDK binding, universally faster, allocation-free, or complete for every future Windows SDK feature. Silk.NET's low-level generated interface structs and function-pointer calls allow Auralis to own its COM/resource lifetime policy and keep native API types inside the backend. The actual `ComPtr<T>` behavior in Silk.NET 2.23.0 also has ownership hazards that Auralis must account for; selecting the library does not transfer responsibility for correctness to the binding.

The other evaluated projects are compared as technologies, not proposed as project dependencies or fallback choices. They have legitimate strengths, but they are not part of Auralis's native interop policy.

## Evaluation basis and evidence standard

The comparison separates four distinct questions:

1. **Declaration coverage:** does the evaluated binding source expose a native interface, method, structure, or enum?
2. **Binding usability:** does the library provide a direct low-level declaration, a generated helper, or a higher-level managed wrapper?
3. **Runtime support:** does the installed Windows version, DXGI/D3D12 runtime, driver, adapter, and device support the requested interface or feature?
4. **Auralis implementation:** does PR #15 or a later backend task actually call and correctly implement that API?

Evidence for one question does not prove the others. A generated `ID3D12GraphicsCommandList7` type does not mean an Auralis command list exists, and it does not mean the active adapter supports every feature reachable through that interface.

**Source confidence labels used below**

- **Pinned-source verified:** inspected in the exact release tag/source corresponding to the package version named.
- **Package verified:** established from the version-specific NuGet package metadata or included package documentation.
- **Repository snapshot verified:** found in the official repository snapshot inspected on the research date, but not tied to an immutable source tag for the exact package binary.
- **Not established:** the available evidence does not justify a version-specific statement. This is not proof that the API is absent.

Silk.NET's 2.23.0 source was inspected at its immutable `v2.23.0` tag. The package version for each alternative was selected from its published NuGet release history as of the research date. A source/package-version limitation is called out where an immutable matching source tag could not be established.

This is an engineering comparison, not legal advice.

## Candidates and evaluated releases

| Binding | Evaluated release | Versioned package scope | Framework/package evidence | Licence evidence |
|---|---|---|---|---|
| **Silk.NET** | **2.23.0** | `Silk.NET.Direct3D12` and `Silk.NET.DXGI` | NuGet package assets include .NET Standard 2.0 and 2.1, .NET Core 3.1 and .NET 5.0. NuGet computes compatibility with `net10.0`; these are not dedicated .NET 10 assets. | MIT in package metadata and the repository licence. |
| TerraFX | **10.0.28000** | `TerraFX.Interop.Windows`, an aggregate Windows/DirectX interop package whose documented headers include D3D12 and DXGI. | Package contains `net10.0` and `net11.0` assets. NuGet reports no package dependencies for these assets. | MIT in package README and repository licence. |
| Vortice | **3.8.3** | `Vortice.Direct3D12` and `Vortice.DXGI` | Both packages expose .NET 8, .NET 9 and .NET 10 assets. The D3D12 package depends on `Vortice.DXGI` and `SharpGen.Runtime`. | MIT in package metadata and repository licence. |
| SharpDX | **4.2.0** | `SharpDX.Direct3D12` and `SharpDX.DXGI` | The D3D12 package lists .NET Standard 1.1 and .NET Framework assets; it has no dedicated .NET 10 asset. Its last stable package release is from 2018. | MIT in package metadata and repository licence. |

Version-specific package references:

- [Silk.NET.Direct3D12 2.23.0](https://www.nuget.org/packages/Silk.NET.Direct3D12/2.23.0) · [Silk.NET.DXGI 2.23.0](https://www.nuget.org/packages/Silk.NET.DXGI/2.23.0)
- [TerraFX.Interop.Windows 10.0.28000](https://www.nuget.org/packages/TerraFX.Interop.Windows/10.0.28000)
- [Vortice.Direct3D12 3.8.3](https://www.nuget.org/packages/Vortice.Direct3D12/3.8.3) · [Vortice.DXGI 3.8.3](https://www.nuget.org/packages/Vortice.DXGI/3.8.3)
- [SharpDX.Direct3D12 4.2.0](https://www.nuget.org/packages/SharpDX.Direct3D12/4.2.0) · [SharpDX.DXGI 4.2.0](https://www.nuget.org/packages/SharpDX.DXGI/4.2.0)

The package version is the comparison unit. Where repository source was inspected from a moving default branch rather than a matching immutable release tag, the text says so and does not promote that snapshot into proof of exact package-binary coverage.

## Current PR #15 audit

### Audited revision and scope

The live PR was inspected on **2026-11-28**.

| Item | Observed value |
|---|---|
| Pull request | [#15](https://github.com/AuralisEngine/Auralis/pull/15) |
| State | Open |
| Draft | Yes |
| Base | `master` |
| Base commit | `b1232da50b635ad6ea120372a658e341a4da769d` |
| Head branch | `feature/d3d12-01-backend-foundation` |
| Audited head commit | `ebf45cff11a4afed14a939bacce6bd487f8ee135` |
| Commits in PR | 19 |
| Files changed by PR | 10 |

The current PR diff was inspected as a whole, the changed-file list was enumerated, and the backend project, implementation sources, tests, workflow, solution registration and README were fetched from the live head branch. The commit endpoint for the PR was also reviewed to trace the binding decision.

The ten files in the audited PR are:

1. `.github/workflows/d3d12-validation.yml`
2. `Auralis.slnx`
3. `Tests/Auralis.Graphics.Direct3D12.Tests/Auralis.Graphics.Direct3D12.Tests.csproj`
4. `Tests/Auralis.Graphics.Direct3D12.Tests/D3D12HResultTests.cs`
5. `Tests/Auralis.Graphics.Direct3D12.Tests/D3D12RuntimeAvailabilityTests.cs`
6. `Tests/Auralis.Graphics.Direct3D12.Tests/SilkNetBindingIntegrationTests.cs`
7. `src/Auralis.Graphics/Auralis.Graphics.Direct3D12/Auralis.Graphics.Direct3D12.csproj`
8. `src/Auralis.Graphics/Auralis.Graphics.Direct3D12/Interop/D3D12NativeException.cs`
9. `src/Auralis.Graphics/Auralis.Graphics.Direct3D12/Runtime/D3D12RuntimeAvailability.cs`
10. `src/Auralis.Graphics/Auralis.Graphics.Direct3D12/README.md`

The project configuration, implementation files, test sources, and build workflow were inspected directly. The PR does not change the common graphics contract project or the higher-level rendering project.

## What PR #15 Actually Uses

This section describes actual source usage in the audited head—not the larger API surface that happens to exist in the referenced package.

### Direct package references and assembly boundary

The current `Auralis.Graphics.Direct3D12.csproj`:

- Targets `net10.0`.
- Enables nullable reference types and unsafe compilation.
- References `Auralis.Graphics.csproj`.
- Directly references `Silk.NET.Direct3D12` version `2.23.0`.
- Directly references `Silk.NET.DXGI` version `2.23.0`.
- Exposes internals to the dedicated backend test assembly.

There is no second Direct3D 12/DXGI binding package reference in this project. The Windows validation workflow inspects the restored dependency graph and checks an allow-list of Silk.NET assets, including `Silk.NET.Core/2.23.0` and `Silk.NET.Maths/2.23.0` alongside the two direct packages.

### Silk.NET symbols referenced by current code

The current Silk.NET compile-time integration test contains two type references:

- `Silk.NET.Direct3D12.ID3D12Device`
- `Silk.NET.DXGI.IDXGIFactory4`

The test verifies that those types resolve from the expected assemblies. It does not construct either interface, invoke any method, load a D3D12 device, create a DXGI factory, enumerate an adapter, or query an optional feature.

**Those two types are the only Silk.NET API types used by the current implementation/test code.** There are no actual Silk.NET native method invocations in PR #15.

### Native runtime availability probe

`D3D12RuntimeAvailability` is implemented with .NET's `System.Runtime.InteropServices.NativeLibrary` API:

- It returns unavailable on non-Windows operating systems.
- On Windows it attempts to load `dxgi.dll`, then `d3d12.dll`, with `DllImportSearchPath.System32`.
- It frees each successfully loaded temporary module handle.
- It separates the operating-system/library decision into a pure `Evaluate` function for host-independent tests.

This is a **system-library loader probe**. It is not a call to DXGI's factory-creation entry point and it does not prove that a factory, adapter, D3D12 device, feature level, or optional GPU feature can be used.

### HRESULT and error handling

`D3D12NativeException` and `D3D12HResult` are handwritten backend utilities:

- HRESULT values are represented as signed 32-bit `int` values.
- Values below zero are treated as failures; all non-negative values, including `S_FALSE`, are accepted as success.
- The exception preserves the operation name, signed HRESULT, unsigned bit pattern, and eight-digit hexadecimal value.
- The outer exception's `HResult` and the inner `COMException.HResult` are set to the original failure value.
- The exception factory rejects a blank operation name and rejects non-failure HRESULT values.

The helper has not yet been exercised by a real D3D12 or DXGI call in this PR. The tests feed it representative integer HRESULT values directly. Its comments describe the expected shape of HRESULT values returned by the generated bindings; they are not evidence that a native call path is already wired up.

### COM and GPU-object lifetime in the current PR

PR #15 creates **no COM object**. It therefore does not yet implement or exercise:

- owned or borrowed COM interface references;
- `AddRef`, `Release`, or `QueryInterface` call sites;
- device, queue, resource, heap, fence, descriptor, pipeline, command-list or swapchain disposal;
- fence-based deferred destruction;
- GPU resource lifetime across submitted work;
- native object cleanup on partial initialization or device loss.

The README records intended lifetime rules for later tasks, but a written rule is not a lifetime implementation. In particular, the current PR has no native COM owner wrapper to assess for idempotence, reference transfer, or thread safety.

### Tests and CI actually present

The dedicated backend tests verify:

- non-negative HRESULT success handling, including `S_FALSE`;
- selected negative HRESULT examples and preservation of their signed/unsigned representations;
- operation context and the inner `COMException` status;
- rejection of success status values by the native-exception factory;
- non-Windows, missing-DXGI, missing-D3D12 and loader-available branches;
- current-host loader-probe consistency;
- compile-time resolution of `ID3D12Device` and `IDXGIFactory4`.

The compile-time binding checks do not create native interfaces. The tests are hardware-independent and do not measure or validate GPU behavior.

The Windows workflow uses the .NET 10 SDK, restores the solution, audits the backend's transitive packages, builds the backend and the two graphics test projects in Release, then runs the dedicated backend tests and the common graphics contract tests. The PR description records a successful Windows run on its rewritten head: 16 dedicated backend tests and 223 common graphics contract tests passed. It also records that the entire solution build/test was not run because an unrelated asset-pipeline method remains a baseline build blocker. No GPU integration test is claimed.

### Explicit non-implementation list

PR #15 is the D3D12-01 backend foundation task. It does **not** implement:

- DXGI factory creation or adapter enumeration;
- WARP selection, adapter descriptions or LUID-based selection;
- D3D12 device creation, feature-level negotiation or capabilities;
- command queues, command allocators, command lists or fences;
- resource creation, heap allocation, residency, resource-state tracking or barriers;
- descriptor heaps, root signatures or pipeline state creation;
- command recording or command submission;
- swapchains, presentation or display/HDR configuration;
- DXR, mesh shaders, VRS, tiled resources or multi-adapter execution.

These are later tasks in [issue #14](https://github.com/AuralisEngine/Auralis/issues/14). Binding declarations discussed in this comparison must not be mistaken for implementation of those tasks.

## API coverage and native fidelity

### Coverage and interpretation matrix

The table compares API shape and evidence, not numerical scores. The entries distinguish an API family that is part of a project's documented scope from a specific symbol verified in source.

| Dimension | Silk.NET 2.23.0 | TerraFX.Interop.Windows 10.0.28000 | Vortice.Windows 3.8.3 | SharpDX 4.2.0 |
|---|---|---|---|---|
| D3D12 device / queue / command-list / resource core | **Pinned-source verified.** Generated interfaces include device, command queue, fence, resource, descriptor heap and graphics command-list families. | **Package verified.** D3D12 headers are explicitly in the package's documented header set; low-level generated native declarations. | **Package verified.** Dedicated D3D12 package; repository snapshot has managed D3D12 interface wrappers. Exact source-to-3.8.3 package mapping was not established. | **Package/source verified.** D3D12 package and legacy Device / command-list wrapper sources exist. |
| Device creation and feature queries | The D3D12 binding project includes D3D12 entry-point declarations; device interface has feature-query and resource/queue creation methods. Not used by PR #15. | D3D12 C API declarations are in scope; exact packaged function exports must be checked at the specific call site. | Repository source exposes D3D12 creation helpers and typed feature-query wrappers. Exact 3.8.3 binary surface not symbol-audited here. | Device wrapper source contains device creation and generic feature-query helpers. |
| DXGI factory / adapter / WARP | **Pinned-source verified.** Factory7 includes adapter enumeration, WARP enumeration, GPU-preference enumeration and adapter-change notification members. | **Package verified.** DXGI headers are documented in package scope; exact generated declarations depend on included headers. | **Repository snapshot verified.** Factory and adapter wrappers, including Factory7, are present in the inspected repository tree. Exact package-version mapping not established. | **Package/source verified.** Factory wrappers through Factory5 are present in the inspected source tree. |
| Adapter descriptions / LUID / memory budget | Adapter4 and later description types are generated; Adapter4 exposes description and video-memory information methods. | Native DXGI structures are generated from SDK headers; exact version depends on included header set. | Adapter wrapper surface and D3D12 adapter LUID/feature-query helpers are in the inspected repository source. | Legacy Adapter wrapper and descriptions are present; modern adapter revision parity is not established. |
| Heaps, descriptors, root signatures, pipeline states | **Pinned-source verified as declarations.** Device creation methods and descriptor, root-signature and pipeline interface/structure families are generated. | Native declarations follow the C headers available in the package; caller owns structure and pointer correctness. | D3D12 wrapper and structure set is broad; exact version-specific symbol matrix for 3.8.3 was not completed. | Historical wrapper surface includes heaps, descriptors, root signatures and pipeline types. |
| Classic resource barriers | **Pinned-source verified.** Command-list ResourceBarrier overloads and classic barrier structures are present. | Native declarations follow the corresponding SDK headers. | Barrier and command-list types are present in the inspected source snapshot. | Legacy ResourceBarrier/transition/aliasing/UAV types are present in source. |
| Enhanced Barriers | **Pinned-source verified.** `ID3D12GraphicsCommandList7` exposes `Barrier`; BarrierGroup, GlobalBarrier, TextureBarrier and BufferBarrier types are present. | SDK-aligned D3D12 headers include the native API family where the corresponding header declarations are available; exact package-level generated members should be checked by the feature implementation. | The inspected repository snapshot contains `BarrierGroup` and global barrier types; exact 3.8.3 package member coverage is not established here. | No corresponding newer interface family was established in the 4.2.0 source inventory. |
| DXR / acceleration structures | **Pinned-source verified.** Command-list declarations include acceleration-structure build and ray dispatch; D3D12 raytracing structures are generated. | D3D12 headers are in package scope; exact emitted members depend on package header selection. | Repository snapshot contains raytracing build/dispatch descriptions and advanced D3D12 declarations; exact 3.8.3 mapping not established. | Historical DXR types and Device5 source exist; do not infer later DXR interface parity. |
| Mesh shaders | **Pinned-source verified.** Generated command-list API includes `DispatchMesh`. | Header-based low-level surface; exact API member must be confirmed in the installed package declarations. | Mesh-shader API is part of modern D3D12 surface, but the exact 3.8.3 package symbol was not independently pinned in this audit. | No modern mesh-shader command-list interface was established in the inspected source inventory. |
| Variable Rate Shading | **Pinned-source verified.** Generated command-list API includes `RSSetShadingRate` and `RSSetShadingRateImage`. | Header-based low-level surface; exact declaration depends on included D3D12 headers. | Exact VRS member coverage in the 3.8.3 package was not established in this audit. | Exact VRS interface coverage was not established in the 4.2.0 source inventory. |
| Tiled resources / sparse mapping | **Pinned-source verified.** Device includes reserved-resource creation; command queue includes `UpdateTileMappings`; tile coordinate/range/region declarations exist. | D3D12 header set includes native D3D12 declarations; exact package symbols must be verified against the requested header set. | Exact sparse/tile API coverage in the 3.8.3 package was not symbol-audited. | Some legacy resource/heap support exists; complete modern sparse-residency support was not established. |
| Device-removed diagnostics | **Pinned-source verified.** Device-removed extended-data interfaces and settings declarations exist in the package source. | Native interfaces depend on included SDK headers. | Repository includes DRED and debug interface types; exact 3.8.3 package mapping not established. | Historical debug/device diagnostics exist; later DRED revisions are not established. |
| Modern device / command-list revisions | Generated source includes `ID3D12Device14` and `ID3D12GraphicsCommandList10` declarations. | Current repository source includes later interface revisions; exact package/header mapping remains tied to the SDK version and header selection. | Inspected current repository snapshot includes Device11 and CommandList7 source files; exact mapping to package 3.8.3 is not established. | The inspected release-era source reaches Device5; later interface revisions were not found in that inventory. |
| DXGI factory / adapter / output / swapchain revisions | **Pinned-source verified.** Factory7, Adapter4, Output6 and SwapChain4 types are generated. | Package documents DXGI headers; versioned interface availability is SDK/header dependent. | Repository snapshot includes Factory7, Output revisions and SwapChain4; exact 3.8.3 package mapping not established. | The inspected source reaches Factory5 and SwapChain2; newer revisions were not established. |
| HDR / color space / frame latency | **Pinned-source verified.** SwapChain4 declares color-space support, color-space setting and HDR metadata; SwapChain2 declares maximum-frame-latency control. | Native declarations are available when those DXGI headers are included; runtime requirements remain separate. | Current repository source contains modern swapchain wrappers; exact 3.8.3 symbol confirmation not completed. | Legacy swapchain source includes earlier presentation controls; modern HDR/color-space parity not established. |
| Multi-adapter / node support | D3D12 node masks, multi-node fields and DXGI adapter/device interfaces are represented; no engine-level multi-GPU execution model is provided by the binding. | Native declarations expose header-level API; engine-level semantics remain caller-owned. | D3D12 feature helpers expose cross-node and node-related structures in inspected repository source; exact 3.8.3 mapping is unverified. | Historical node-mask and cross-adapter types exist; modern multi-adapter behavior is not established. |

### Silk.NET 2.23.0 D3D12 surface established from the pinned source

The source at the exact `v2.23.0` tag establishes more than the two compile-time types used in the current PR. The generated D3D12 project contains families for:

- `ID3D12Device` and versioned device interfaces through `ID3D12Device14`;
- `ID3D12CommandQueue`, `ID3D12CommandAllocator`, `ID3D12Fence` and versioned forms;
- `ID3D12GraphicsCommandList` and generated revisions through `ID3D12GraphicsCommandList10`;
- resources, heaps, descriptor heaps, root signatures, pipeline state and related native structures;
- device-removed extended-data interfaces and settings;
- classic resource barriers and enhanced barrier group / buffer / texture / global barrier structures;
- DXR acceleration-structure descriptions, build inputs, post-build information and ray-dispatch descriptions;
- tile coordinates, tile regions and tile-range declarations.

The pinned source for `ID3D12GraphicsCommandList7` explicitly declares:

- `BuildRaytracingAccelerationStructure`;
- `DispatchRays`;
- `RSSetShadingRate`;
- `RSSetShadingRateImage`;
- `DispatchMesh`;
- `Barrier` for enhanced barrier groups.

The base device source exposes resource and heap creation members, including `CreateCommittedResource`, `CreateReservedResource`, and `CreateHeap`. The command queue source contains `UpdateTileMappings`. These are declaration-level observations from the exact Silk.NET release source, not Auralis implementation claims.

### Silk.NET 2.23.0 DXGI surface established from the pinned source

The generated DXGI source contains:

- factory interfaces through `IDXGIFactory7`;
- adapter interfaces through `IDXGIAdapter4`;
- output interfaces through `IDXGIOutput6`;
- swapchain interfaces through `IDXGISwapChain4`.

For example, the factory source contains `EnumAdapters1`, `EnumWarpAdapter`, `EnumAdapterByGpuPreference`, and `RegisterAdaptersChangedEvent`. Adapter4 exposes description and video-memory-query methods. SwapChain4 exposes color-space support checks, color-space setting, and HDR metadata; SwapChain2 exposes maximum frame-latency control. These are source declarations, not a report that Auralis has implemented those calls.

The fact that a declaration exists still leaves several separate requirements: the correct IID must be queried, the native interface must be supported by the installed runtime, the device/driver must support the feature, and the backend must perform the associated feature query, lifetime management and synchronization.

### What package presence cannot prove

For all four candidates, a managed binding can only expose an API contract. It cannot make an older Windows version implement a newer interface, grant hardware support for ray tracing or VRS, or guarantee that the installed driver handles a feature correctly. It also does not prove every helper overload or every API revision is present in the versioned package.

Auralis must treat a binding declaration as one input to a feature implementation—not as capability detection or evidence of device support.

## Binding architecture and COM ownership

### Silk.NET: generated interface structs and explicit COM pointer helper

The pinned generated `ID3D12Device` source represents the COM interface as an `unsafe partial struct` containing a `void** LpVtbl`. COM calls are emitted as direct function-pointer invocations. The generated source uses forms such as:

```csharp
((delegate* unmanaged[Stdcall]<ID3D12Device*, Guid*, void**, int>)
    @this->LpVtbl[0])(@this, riid, ppvObject);
```

The interface declares `QueryInterface`, `AddRef`, and `Release` as explicit methods. Native HRESULT-returning members surface as signed `int` results. Generated overloads use raw pointers, `ref` / `ref readonly`, unmanaged generic arguments, and helper overloads that return `ComPtr<T>` for COM out parameters.

The raw interface struct is not an owning managed object. A raw pointer/interface value by itself does not carry an automatic release policy. Silk.NET's generic `ComPtr<T>` is a value-type helper that implements `IDisposable` and calls COM reference-counting methods.

**Important source-level ownership caveat in Silk.NET 2.23.0:** `ComPtr<T>`'s constructor from a raw pointer calls `AddRef`; its explicit copy constructor also calls `AddRef`; `Detach` clears the pointer without releasing it. However, the implementation of `Release()` calls the underlying COM `Release` without clearing `Handle`, and `Dispose()` delegates to `Release()` without clearing the field. It is therefore unsafe to assume that calling `Dispose` repeatedly on the same non-detached instance is idempotent. A normal bitwise assignment/copy of the struct also does not invoke its explicit copy constructor and therefore does not automatically perform an additional COM `AddRef`.

That is a concrete ownership hazard, not a reason to replace the selected binding. Auralis's ownership layer must impose a single-release discipline, control copies/transfers, and clear or invalidate ownership state after releasing. A high-level Auralis owner must not expose unrestricted mutable copies of an owning `ComPtr<T>`.

### TerraFX: SDK-style declarations and a richer value-type ComPtr

The inspected TerraFX declarations use unsafe structs with an `lpVtbl` pointer, native method names, `HRESULT` return values and `delegate* unmanaged[MemberFunction]` calls. The implementation is close to the C COM ABI and uses native pointer parameters directly.

Its `ComPtr<T>` is also a value type, but it provides a more extensive COM smart-pointer API: pointer constructors and explicit copies increment references; `As<U>` and `AsIID` use `QueryInterface`; `Attach` adopts a pointer without adding a reference; `Detach` transfers it out; `CopyTo` increments before copying; `Reset` releases and clears; and `Dispose` clears the pointer before releasing. The implementation is explicitly intended to model ownership transfers and deterministic release.

The same value-type warning still applies: ordinary struct assignment does not execute a custom reference-incrementing copy constructor. Code that stores or copies `ComPtr<T>` values must therefore account explicitly for reference ownership; an ordinary struct assignment does not acquire a new COM reference. Raw interface structs still do not magically distinguish borrowed from owned references.

### Vortice: generated bindings with managed COM wrappers

Vortice uses SharpGen-based generation and provides managed wrapper classes for DirectX interfaces, with native pointers exposed through its COM runtime abstractions. Its D3D12 source includes typed helper methods such as feature-support wrappers, and COM interface-returning helper methods produce managed interface wrapper instances.

This model gives C# callers more convenient object-oriented APIs than raw pointer/vtable declarations. The cost model and ownership model consequently include wrapper objects and helper paths. A returned wrapper must still be treated as an owned COM reference according to the method contract and deterministically disposed. The library's `QueryInterface` / pointer-to-wrapper helpers must be understood at every ownership boundary.

The D3D12 package depends on `SharpGen.Runtime` and the DXGI package. The runtime project describes its purpose as supporting SharpGen-generated interop and COM interfaces. The additional wrapper layer is not inherently too slow; it is a different trade-off from exposing raw generated interfaces and letting the engine own all wrappers.

The exact implementation details of every Vortice helper must be checked against the package version used. The inspected default-branch source is a repository snapshot, not an immutable tag proven to reproduce the entire 3.8.3 NuGet package.

### SharpDX: class wrappers, COM object model and deterministic disposal

SharpDX exposes DirectX APIs through managed wrapper classes. The inspected D3D12 source includes a `Device` class with constructors that create a D3D12 device, helper methods that return command-queue wrappers, and feature-support methods. Its common `ComObject` model exposes the native pointer, supports typed `QueryInterface<T>`, and releases COM references through the wrapper's disposal path.

The wrapper approach can make native objects convenient to use from ordinary C#. It also creates a managed wrapper object for COM object returns; source code uses reflective type construction in pointer-to-wrapper helpers. The common disposal base has a finalization path. The COM layer's default behavior is not to rely on the finalizer to release native references; explicit `Dispose` remains the normal release path, with finalizer behavior configurable for diagnostics/release. In other words, the existence of a finalizer does not make missing deterministic disposal safe.

For a backend designed around its own explicit resource-lifetime system, this is more managed object machinery than is necessary for a raw call boundary. The decisive maintenance issue is separate: the upstream project states it stopped development and maintenance in March 2019 and its repository is archived.

### Ownership rules Auralis must enforce regardless of binding

COM reference counting is not GPU completion tracking. An object may still be in use by submitted work after the CPU has stopped referencing it. An engine must retain native ownership until all relevant queue/fence completion requirements have passed.

Auralis's later native owner types must enforce:

1. One identifiable release responsibility for each owned COM reference.
2. Explicit distinction between an owned pointer, borrowed pointer and transferred pointer.
3. Correct ownership for `QueryInterface` results and COM creation/out parameters.
4. No release of borrowed pointers unless a reference was separately acquired.
5. Idempotent engine-level disposal, including exception paths and repeated teardown calls.
6. No finalizers in the performance-sensitive native ownership path.
7. No use-after-release, duplicate release or leaked partially constructed native objects.
8. GPU deferred destruction based on the correct queue completion/fence state—not solely on COM reference count.
9. Carefully controlled copying of value-type COM smart pointers.
10. Clear device-loss behavior when normal completion waits can no longer be relied upon.

No library automatically establishes Auralis's GPU lifetime model. The backend is responsible for correctness.

## .NET integration and dependency implications

| Topic | Silk.NET 2.23.0 | TerraFX 10.0.28000 | Vortice 3.8.3 | SharpDX 4.2.0 |
|---|---|---|---|---|
| Current Auralis target | NuGet computes compatibility with `net10.0`; actual package assets are older TFMs. | Contains a direct `net10.0` package asset. | Contains `net10.0` asset. | No dedicated `net10.0` package asset; NuGet compatibility from older assets is not modern runtime certification. |
| Managed model | Generated unmanaged COM interface structs and `ComPtr<T>` struct helper. | Blittable-oriented native structs, pointers and `ComPtr<T>`. | Managed COM wrapper classes plus generated/native methods and SharpGen runtime. | Managed DirectX wrapper classes and COM base classes. |
| Unsafe / pointer API | Explicit unsafe code, function pointers, pointer and ref overloads. | Explicit unsafe pointers and function pointers; close-to-native signature model. | Unsafe code exists in wrapper implementations; common call sites can use typed wrappers and overloads. | Many APIs use wrapper objects, structs, `IntPtr` and generated interop helpers; unsafe internals exist. |
| Span / caller-owned memory | Native declarations permit caller-controlled pointer/ref data. Span ergonomics are method-specific; no universal Span guarantee is claimed. | Blittable structures and pointer APIs permit caller-controlled storage; a Span layer is not inherent. | Higher-level helpers can provide ergonomic .NET overloads; any conversion/marshalling cost must be inspected per method. | Historical array/managed helper APIs are available; any marshaling and allocation is method-specific. |
| HRESULT representation | HRESULT signatures surface as signed `int`; callers can preserve the original value. | Native HRESULT type and ABI representation are explicit. | Uses SharpGen result/exception helpers in managed wrapper APIs; caller must preserve diagnostics at boundaries. | Uses `Result` / `SharpDXException` wrapper patterns. |
| NativeAOT / trimming | Newer-framework compatibility is established; D3D12-specific NativeAOT/trimming guarantee is not established by the package TFM alone. | Project explicitly lists blittability, trimming safety and AOT compatibility as design goals. Verify actual app configuration and used APIs. | Modern target frameworks are present; do not infer universal trim/AOT safety for all wrapper and COM paths. | No current NativeAOT/trimming certification was found; reflective wrapper creation is a trim consideration. |
| Transitive dependencies | The current backend restores Silk.NET.Core and Silk.NET.Maths along with the direct D3D12/DXGI packages. | NuGet reports no package dependencies for the evaluated package assets. | D3D12 depends on `SharpGen.Runtime` and `Vortice.DXGI`. | D3D12 depends on SharpDX core and DXGI packages. |
| Multi-targeting implications | Broad legacy-compatible assets, but no dedicated net10 binary. | Direct fit for a net10 Windows interop project. | Direct net10 asset and net8/net9 assets. | Legacy TFM set; framework compatibility does not prove modern deployment support. |

### NativeAOT and trimming qualification

A framework compatibility badge is not an AOT certification. The binding must be tested under the exact deployment mode and API paths that the engine intends to ship.

TerraFX makes explicit design claims around blittability, trimming and AOT. That is a stated project goal; an Auralis deployment still requires testing. Silk.NET's package can be consumed by a .NET 10 target according to NuGet's compatibility calculation, but the Direct3D12 and DXGI packages do not ship a dedicated net10 asset. A general Silk.NET release note about fixing a NativeAOT-related marshalling issue elsewhere in Silk.NET is not proof that every D3D12/DXGI path is NativeAOT-ready. Vortice's newer target assets and SharpGen-based source generation are not a blanket guarantee for all trim/AOT paths either. SharpDX's package age and reflective pointer-to-wrapper construction mean its modern trimming/AOT behavior is not established.

### Windows and architecture support

Direct3D 12 is a Windows native API. A package that targets a portable managed TFM does not make a D3D12 backend portable to Linux or macOS. Auralis must separately check Windows version, process architecture, native runtime availability, driver and adapter support. The comparison did not establish a complete certified x86/x64/ARM64 deployment matrix for every package release; no such claim is made here.

## Performance and allocation analysis

### What source structure does establish

The source models differ in ways that affect how Auralis can structure calls:

- **Silk.NET:** generated COM interface structs call function pointers from the interface vtable. This avoids requiring a heap-allocated managed wrapper object for every raw interface call. Auralis can use caller-owned pointers/refs and the generic `ComPtr<T>` value type. String convenience overloads, marshalling helpers, object creation, and other higher-level helpers still need individual inspection.
- **TerraFX:** generated interfaces use unmanaged function pointers and blittable-oriented signatures. The basic raw call path is low-level and can avoid per-call object wrappers. Its smart pointer is a struct, not a zero-cost proof of correct ownership; constructor, explicit-copy, and transfer semantics must be respected.
- **Vortice:** higher-level typed interface wrappers and convenience helpers improve ergonomics. Returned COM interface wrapper instances have managed object cost; helpers may also perform conversions or temporary storage work. The cost is method-specific, and no universal penalty is inferred.
- **SharpDX:** wrapper classes and pointer-to-wrapper construction introduce managed object and helper work at object boundaries. The existence of wrappers does not prove a particular native call is slow, but it does add an object model Auralis would have to own and dispose alongside the engine's own native resource abstraction.

These are architectural/source observations, not comparative benchmark results.

### What is not established

No reliable benchmark comparing these four evaluated releases under equivalent D3D12 workloads was found for this decision. No evidence supports a numerical native-call latency ranking, a claim that one library is universally faster, or a claim that one package guarantees zero allocation. Managed binding overhead is also not GPU execution performance.

There are no D3D12 hot paths in PR #15 to benchmark. The current PR has no adapter enumeration, device, resource, command list, descriptor, barrier, fence or frame submission implementation. It is therefore too early to draw conclusions from Auralis runtime performance.

### Auralis benchmark requirements

When the corresponding backend operations exist, benchmark the actual Auralis call sites with equivalent workloads:

- command-list recording throughput and concurrent recording;
- descriptor writes and descriptor-table construction;
- resource / heap creation and destruction;
- classic and enhanced barrier recording;
- interface queries and COM reference ownership operations;
- command queue submission, fence signaling and wait paths;
- transient and persistent allocation behavior around native calls.

Use warmed Release builds and record the runtime, .NET version, CPU, OS, adapter, driver, and deployment mode. Report elapsed CPU time separately from allocation counts and GPU timings. Run JIT and NativeAOT measurements only where both are intended configurations. Keep the native operation and ownership semantics identical between tests.

Auralis's performance rule is to avoid unnecessary wrapper allocations in high-frequency paths and retain caller control of storage and lifetime. It must not sacrifice correct ownership or GPU synchronization to remove an allocation that has not been measured.

## Maintenance, ecosystem and licensing

### Maintenance snapshot

| Project | Observable release / maintenance evidence at the research date | Interpretation |
|---|---|---|
| Silk.NET | NuGet published 2.23.0 on 2026-01-23. The official release notes identify Windows SDK 10.0.26100 and Direct3D 12 Agility SDK 1.618.5 updates, and state that 2.x updates continue while 3.0 is being developed. | Recent versioned package and an active release line. This does not guarantee future cadence or every desired D3D12 symbol. |
| TerraFX | NuGet published 10.0.28000 on 2026-11-16. The package README identifies Windows SDK 10.0.28000.1721 and DirectX Agility SDK 1.620.2 and identifies .NET Foundation support. | Very recent SDK-aligned package. Header updates are a strength; the package's documented header set is not every Windows header by default. |
| Vortice | NuGet package 3.8.3 was updated 2026-03-04. The official changelog reports 3.8.3 as the stable line and records 3.8.x updates during 2026; the repository had commits as recent as 2026-10-05. | Active modern DirectX binding ecosystem with current framework targets. Exact source-to-package symbol parity still matters. |
| SharpDX | NuGet 4.2.0 was last updated 2018-08-24. The official repository says development and maintenance stopped on 2019-03-29 and was archived read-only on 2019-03-30. | Mature historical wrapper ecosystem, but no upstream maintenance path for current API evolution. |

These facts describe releases and repository activity that were observable on 2026-11-28; they are not forecasts of future maintenance.

### Licence and redistribution observations

The evaluated project/package licence evidence is MIT:

- Silk.NET: [repository LICENSE.md](https://github.com/dotnet/Silk.NET/blob/v2.23.0/LICENSE.md); the NuGet pages identify MIT.
- TerraFX: [repository LICENSE.md](https://github.com/terrafx/terrafx.interop.windows/blob/main/LICENSE.md); the package README identifies MIT.
- Vortice: [repository LICENSE](https://github.com/amerkoleci/Vortice.Windows/blob/main/LICENSE); package metadata identifies MIT.
- SharpDX: [repository LICENSE](https://github.com/sharpdx/SharpDX/blob/master/LICENSE); package metadata identifies MIT.

The Vortice package's `SharpGen.Runtime` dependency has a separate repository and MIT licence evidence: [SharpGen.Runtime.COM LICENSE](https://github.com/SharpGenTools/SharpGen.Runtime.COM/blob/master/LICENSE.txt). The current Auralis Windows workflow also emits a restored dependency graph for the selected packages. A complete legal review of all transitive packages, generated code headers, notices, and redistribution requirements is outside this engineering comparison. Auralis should retain its normal software bill of materials and third-party notice review.

MIT licensing generally permits use and redistribution subject to its notice/permission conditions, but package-level labels do not replace review of the exact shipped dependency graph.

## Auralis-specific evaluation

Auralis has a deliberate three-layer boundary:

- `Auralis.Graphics` defines backend-neutral graphics contracts and must not expose native API or binding types.
- `Auralis.Graphics.Direct3D12` owns the low-level D3D12/DXGI implementation, native calls, unsafe boundary, HRESULT diagnostics, COM ownership and GPU resource lifetime.
- `Auralis.Rendering` is a distinct higher-level layer for render graphs, renderer scheduling, materials, frame orchestration and scene rendering. It must not become a second native binding layer.

The selected low-level generated model fits this boundary because Auralis can keep the native interface vocabulary in the backend and own the engine-side objects and policies around it. The binding does not dictate Auralis's public graphics abstraction, render graph design, allocator, deferred destruction model, queue scheduler or diagnostics architecture.

The concrete trade-offs for Auralis are:

1. **Native control:** the generated interface structs and pointer signatures expose native details directly. This matches a backend whose responsibility is to translate its own graphics contracts into D3D12 calls.
2. **Assembly isolation:** the package references are direct dependencies of the backend project, not the common contracts.
3. **Modern API declarations:** the pinned source includes declarations for several advanced D3D12 and DXGI interfaces required by the roadmap. Each actual feature still requires device/runtime support checks and an implementation.
4. **Ownership responsibility:** the binding leaves lifetime decisions close to the engine, but its `ComPtr<T>` disposal/copy semantics must be wrapped or carefully disciplined by Auralis.
5. **Performance control:** raw interface calls avoid mandating a managed wrapper per call, while leaving measurement, storage, dispatch and synchronization policy to Auralis.
6. **No false completeness claim:** PR #15 is infrastructure only. API coverage in a binding must not be reported as implemented backend capability.

The evaluated alternatives have real strengths in their own design goals: an SDK-header-oriented declaration approach, managed wrapper ergonomics, or a historical wrapper ecosystem. Auralis's choice is not a universal judgement that those projects are technically inferior. It is a project architecture decision to use one native binding family and keep its native type and ownership model consistent across the backend.

## Binding Decision for Auralis

**Silk.NET is the selected, required and exclusive native binding for all Auralis low-level Direct3D 12 and DXGI implementation.**

The current pinned packages are:

- `Silk.NET.Direct3D12` **2.23.0**
- `Silk.NET.DXGI` **2.23.0**

The decision has the following binding-policy consequences:

- Both package references remain confined to `Auralis.Graphics.Direct3D12`.
- Common `Auralis.Graphics` contracts must remain backend-neutral and must not expose Silk.NET or native Windows types.
- `Auralis.Rendering` remains separate from native backend implementation.
- Native object ownership, COM reference counting, deterministic disposal, GPU-use lifetime and deferred destruction remain Auralis responsibilities.
- No finalizers are introduced in the performance-sensitive native ownership path.
- Hot paths must avoid unnecessary managed wrappers, allocations, reflection and hidden runtime behavior; actual costs must be measured.
- No second native binding may be introduced without an explicit architectural decision by the project owner.
- A missing binding member must be documented by the **exact native interface, method, structure, enum or entry point**, the exact Silk.NET package version inspected, the official source file checked, and the feature implementation that requires it.
- An absent convenience overload is not proof that the underlying native method is missing. Inspect the generated interface and valid pointer/ref overloads before recording a gap.
- A missing member does not authorize silently introducing a second library or a substitute handwritten COM/vtable implementation.
- No claim of performance superiority or allocation-free operation is accepted without reproducible measurements from actual Auralis call sites.

The package is selected because its low-level generated COM binding style gives Auralis direct control of its native boundary and allows the engine to impose one ownership and abstraction policy. The decision is not based on an assertion that it has the newest SDK headers or that every potential future D3D12/DXGI symbol has already been audited.

## Known limits of this comparison and future verification

The following points are intentionally not represented as completed implementation:

- The PR #15 source currently does not invoke D3D12/DXGI methods or create native COM objects.
- The Silk.NET API inventory above records declarations that were checked in the pinned source; it is not an exhaustive audit of every declaration or every overload in the two packages.
- The package declarations do not prove Windows version, adapter, driver or GPU support.
- The full corresponding package source was not proven by an immutable matching source tag for Vortice 3.8.3 in this audit; its repository snapshot findings are labelled accordingly.
- TerraFX's package documents the SDK header baseline and broad DirectX header scope, but the package says not all headers are included by default. Its repository source snapshot is not treated as proof that every generated interface is present in the package binary.
- The SharpDX source inventory confirms a historical D3D12/DXGI wrapper surface, but the archived project's lack of maintenance means later interface revisions should not be inferred from old type names.
- No comparative benchmark was run or found.
- No D3D12-specific NativeAOT/trimming deployment test was performed as part of PR #15.
- No legal audit beyond package/repository licence evidence and the observed transitive dependency relationship was performed.

For each future implementation task, record the exact Silk.NET symbols used and validate them in the pinned package. For advanced features, also record the relevant OS/runtime minimum, device feature query, driver/adapter requirements, synchronization and lifetime behavior, and the tests that demonstrate the actual backend support.

Revisiting the binding policy requires an explicit project-level architectural decision. This record does not authorize an implementation agent to substitute another binding or create a parallel native interop path.

## References and source evidence

All references below were consulted for this comparison on **2026-11-28**. Source paths are included where they directly support implementation-model or API-surface claims.

### Auralis source of truth

- [PR #15 — audited pull request and current diff](https://github.com/AuralisEngine/Auralis/pull/15)
- [PR #15 audited head commit](https://github.com/AuralisEngine/Auralis/commit/ebf45cff11a4afed14a939bacce6bd487f8ee135)
- [Umbrella issue #14 — D3D12 roadmap](https://github.com/AuralisEngine/Auralis/issues/14)
- [Backend project file at audited head](https://github.com/AuralisEngine/Auralis/blob/ebf45cff11a4afed14a939bacce6bd487f8ee135/src/Auralis.Graphics/Auralis.Graphics.Direct3D12/Auralis.Graphics.Direct3D12.csproj)
- [HRESULT implementation at audited head](https://github.com/AuralisEngine/Auralis/blob/ebf45cff11a4afed14a939bacce6bd487f8ee135/src/Auralis.Graphics/Auralis.Graphics.Direct3D12/Interop/D3D12NativeException.cs)
- [Runtime availability implementation at audited head](https://github.com/AuralisEngine/Auralis/blob/ebf45cff11a4afed14a939bacce6bd487f8ee135/src/Auralis.Graphics/Auralis.Graphics.Direct3D12/Runtime/D3D12RuntimeAvailability.cs)
- [Compile-time binding test at audited head](https://github.com/AuralisEngine/Auralis/blob/ebf45cff11a4afed14a939bacce6bd487f8ee135/Tests/Auralis.Graphics.Direct3D12.Tests/SilkNetBindingIntegrationTests.cs)

### Silk.NET 2.23.0 pinned package and source

- [Silk.NET.Direct3D12 2.23.0 NuGet metadata](https://www.nuget.org/packages/Silk.NET.Direct3D12/2.23.0)
- [Silk.NET.DXGI 2.23.0 NuGet metadata](https://www.nuget.org/packages/Silk.NET.DXGI/2.23.0)
- [Silk.NET v2.23.0 release notes](https://github.com/dotnet/Silk.NET/releases/tag/v2.23.0)
- [Silk.NET licence at v2.23.0](https://github.com/dotnet/Silk.NET/blob/v2.23.0/LICENSE.md)
- [D3D12 generated ID3D12Device](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Microsoft/Silk.NET.Direct3D12/Structs/ID3D12Device.gen.cs)
- [D3D12 generated ID3D12GraphicsCommandList7](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Microsoft/Silk.NET.Direct3D12/Structs/ID3D12GraphicsCommandList7.gen.cs)
- [D3D12 generated ID3D12GraphicsCommandList10](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Microsoft/Silk.NET.Direct3D12/Structs/ID3D12GraphicsCommandList10.gen.cs)
- [D3D12 generated ID3D12CommandQueue](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Microsoft/Silk.NET.Direct3D12/Structs/ID3D12CommandQueue.gen.cs)
- [DXGI generated IDXGIFactory7](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Microsoft/Silk.NET.DXGI/Structs/IDXGIFactory7.gen.cs)
- [DXGI generated IDXGIAdapter4](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Microsoft/Silk.NET.DXGI/Structs/IDXGIAdapter4.gen.cs)
- [DXGI generated IDXGISwapChain4](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Microsoft/Silk.NET.DXGI/Structs/IDXGISwapChain4.gen.cs)
- [Silk.NET Core ComPtr<T> implementation](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Core/Silk.NET.Core/Native/ComPtr%601.cs)
- [Silk.NET D3D12 project target frameworks](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Microsoft/Silk.NET.Direct3D12/Silk.NET.Direct3D12.csproj)

### TerraFX package and source

- [TerraFX.Interop.Windows 10.0.28000 NuGet package and package README](https://www.nuget.org/packages/TerraFX.Interop.Windows/10.0.28000)
- [TerraFX official repository](https://github.com/terrafx/terrafx.interop.windows)
- [TerraFX repository licence](https://github.com/terrafx/terrafx.interop.windows/blob/main/LICENSE.md)
- [TerraFX ComPtr<T> source](https://github.com/terrafx/terrafx.interop.windows/blob/main/sources/Interop/Windows/ComPtr%601.cs)
- [TerraFX ID3D12Device source](https://github.com/terrafx/terrafx.interop.windows/blob/main/sources/Interop/Windows/DirectX/d3d12/d3d12/ID3D12Device.cs)
- [TerraFX ID3D12GraphicsCommandList source](https://github.com/terrafx/terrafx.interop.windows/blob/main/sources/Interop/Windows/DirectX/d3d12/d3d12/ID3D12GraphicsCommandList.cs)

### Vortice package and source

- [Vortice.Direct3D12 3.8.3 NuGet package](https://www.nuget.org/packages/Vortice.Direct3D12/3.8.3)
- [Vortice.DXGI 3.8.3 NuGet package](https://www.nuget.org/packages/Vortice.DXGI/3.8.3)
- [Vortice.Windows README](https://github.com/amerkoleci/Vortice.Windows/blob/main/README.md)
- [Vortice.Windows changelog](https://github.com/amerkoleci/Vortice.Windows/blob/main/CHANGELOG.md)
- [Vortice repository licence](https://github.com/amerkoleci/Vortice.Windows/blob/main/LICENSE)
- [Vortice D3D12 device wrapper source](https://github.com/amerkoleci/Vortice.Windows/blob/main/src/Vortice.Direct3D12/ID3D12Device.cs)
- [Vortice D3D12 command-list source](https://github.com/amerkoleci/Vortice.Windows/blob/main/src/Vortice.Direct3D12/ID3D12GraphicsCommandList.cs)
- [Vortice DXGI factory7 source](https://github.com/amerkoleci/Vortice.Windows/blob/main/src/Vortice.DXGI/IDXGIFactory7.cs)
- [Vortice D3D12 package project and generation configuration](https://github.com/amerkoleci/Vortice.Windows/blob/main/src/Vortice.Direct3D12/Vortice.Direct3D12.csproj)
- [SharpGenTools generator/runtime repository](https://github.com/SharpGenTools/SharpGenTools)
- [SharpGen COM runtime source](https://github.com/SharpGenTools/SharpGen.Runtime.COM)
- [SharpGen COM runtime ComObject source](https://github.com/SharpGenTools/SharpGen.Runtime.COM/blob/master/ComObject.cs)
- [SharpGen COM runtime licence](https://github.com/SharpGenTools/SharpGen.Runtime.COM/blob/master/LICENSE.txt)

**Vortice source-version qualification:** the package version and target frameworks are established from NuGet. The repository source links above point to the official repository default branch inspected during this research, not to an immutable source tag independently matched to the complete 3.8.3 package binary. The repository/changelog describes the 3.8.3 stable line, but this document deliberately labels exact symbol claims at that package version as unverified where the source/package mapping was not proven.

### SharpDX package and source

- [SharpDX.Direct3D12 4.2.0 NuGet package](https://www.nuget.org/packages/SharpDX.Direct3D12/4.2.0)
- [SharpDX.DXGI 4.2.0 NuGet package](https://www.nuget.org/packages/SharpDX.DXGI/4.2.0)
- [SharpDX archived repository and retirement statement](https://github.com/sharpdx/SharpDX)
- [SharpDX release history](https://github.com/sharpdx/SharpDX/releases)
- [SharpDX repository licence](https://github.com/sharpdx/SharpDX/blob/master/LICENSE)
- [SharpDX D3D12 Device source](https://github.com/sharpdx/SharpDX/blob/master/Source/SharpDX.Direct3D12/Device.cs)
- [SharpDX D3D12 Device5 source](https://github.com/sharpdx/SharpDX/blob/master/Source/SharpDX.Direct3D12/Device5.cs)
- [SharpDX DXGI Factory4 source](https://github.com/sharpdx/SharpDX/blob/master/Source/SharpDX.DXGI/Factory4.cs)
- [SharpDX COM object source](https://github.com/sharpdx/SharpDX/blob/master/Source/SharpDX/ComObject.cs)
- [SharpDX CppObject pointer-wrapper source](https://github.com/sharpdx/SharpDX/blob/master/Source/SharpDX/CppObject.cs)
- [SharpDX documented COM disposal model](https://github.com/sharpdx/sharpdx.github.io/blob/master/wiki/usage.md)

### Native API and COM semantics

- [Microsoft Direct3D 12 programming guide](https://learn.microsoft.com/windows/win32/direct3d12/directx-12-programming-guide)
- [Microsoft Direct3D 12 reference](https://learn.microsoft.com/windows/win32/direct3d12/direct3d-12-reference)
- [Microsoft DXGI reference](https://learn.microsoft.com/windows/win32/direct3ddxgi/d3d10-graphics-reference-dxgi)
- [Microsoft COM rules for managing reference counts](https://learn.microsoft.com/windows/win32/com/rules-for-managing-reference-counts)
- [Microsoft COM rules for implementing QueryInterface](https://learn.microsoft.com/windows/win32/com/rules-for-implementing-queryinterface)
- [Microsoft Direct3D 12 Enhanced Barriers overview](https://learn.microsoft.com/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist7-barrier)
- [Microsoft D3D12 Enhanced Barriers support query (`D3D12_FEATURE_DATA_D3D12_OPTIONS12`)](https://learn.microsoft.com/windows/win32/api/d3d12/ns-d3d12-d3d12_feature_data_d3d12_options12)
- [Microsoft Direct3D 12 raytracing](https://learn.microsoft.com/windows/win32/direct3d12/direct3d-12-raytracing)
- [Microsoft DXGI flip-model presentation](https://learn.microsoft.com/windows/win32/direct3ddxgi/dxgi-flip-model)
