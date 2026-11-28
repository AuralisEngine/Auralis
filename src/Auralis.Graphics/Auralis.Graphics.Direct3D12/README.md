# Auralis.Graphics.Direct3D12

## Purpose and responsibility

Auralis.Graphics.Direct3D12 is Auralis's Windows Direct3D 12 backend. It implements the low-level GPU-facing contracts defined by the backend-independent Auralis.Graphics assembly. This project owns the Direct3D 12 and DXGI bindings, native calls, unsafe interop boundaries, HRESULT diagnostics, and native object lifetime.

This foundational task establishes dependencies, runtime-loader detection, native error conversion, tests, solution integration, and Windows CI. It does not yet create adapters, logical devices, GPU resources, queues, command lists, pipelines, swapchains, or rendering systems.

## Exclusive native binding

The backend uses only these generated Silk.NET bindings:

- [Silk.NET.Direct3D12 2.23.0](https://www.nuget.org/packages/Silk.NET.Direct3D12/2.23.0) for Direct3D 12.
- [Silk.NET.DXGI 2.23.0](https://www.nuget.org/packages/Silk.NET.DXGI/2.23.0) for DXGI.

Both package references are declared directly in Auralis.Graphics.Direct3D12.csproj. They are not referenced by the common graphics assembly or higher-level rendering project. No alternative Direct3D 12/DXGI binding is part of this backend. Do not combine bindings or replace missing generated entry points with handwritten COM/vtable or native API bindings. If a required symbol is unavailable in the pinned Silk.NET release, document the exact missing API and obtain an explicit architectural decision before proceeding.

Silk.NET 2.23.0 package metadata lists .NET Standard 2.0, .NET Core 3.1, and .NET 5.0 assets; NuGet computes compatibility for newer frameworks, including .NET 10. This is compatible with the backend's net10.0 target, but computed compatibility is not the same as a package-specific statement that every .NET 10 runtime mode, NativeAOT configuration, or trimming scenario has been independently certified. The selected packages identify MIT licensing. The backend consumes the low-level generated namespaces Silk.NET.Direct3D12 and Silk.NET.DXGI. The dedicated test project includes compile-time references to ID3D12Device and IDXGIFactory4 so the intended generated binding assemblies are checked during build without loading native libraries or creating COM objects.

## Assembly boundaries

- **Auralis.Graphics** is the authoritative backend-independent contract assembly. It must not depend on Silk.NET, Direct3D 12, DXGI, Win32 types, or native implementation classes.
- **Auralis.Graphics.Direct3D12** owns Silk.NET references, native API interaction, unsafe code, HRESULT handling, and native Direct3D 12/DXGI object lifetime.
- **Auralis.Rendering** owns higher-level rendering architecture such as render graphs, materials, renderer scheduling, frame orchestration, and scene rendering. This backend does not reference it.
- Backend registration remains explicit and strongly typed. Runtime assembly scanning, reflection-based discovery, and reflection-based registries are not part of this architecture.
- Shader compilation and shader reflection are outside this project. Applications provide compiled shader artifacts; later backend shader creation accepts DXIL and rejects incompatible formats.

## Runtime availability probe

Runtime/D3D12RuntimeAvailability checks whether the process is running on Windows and whether dxgi.dll and d3d12.dll can be loaded using the System32-only native library search flag. Successful temporary module handles are released deterministically.

The probe does not create a factory, enumerate adapters, create a device, open a window, or query feature levels. A successful result means only that both system libraries were loadable at probe time. Adapter discovery, driver/runtime feature checks, and logical-device feature negotiation belong to D3D12-02 and D3D12-03.

The pure Evaluate decision function separates platform/library availability decisions from the host's native loader state and is tested without a GPU.

## Native HRESULT diagnostics

Silk.NET's generated native calls return HRESULT values as signed 32-bit integers where the native API declares HRESULT. Pass those values to D3D12HResult.ThrowIfFailed:

- Negative values are failures.
- Every non-negative value is a successful status, including S_FALSE.
- D3D12NativeException preserves the operation name, signed HRESULT, unsigned bit pattern, and eight-digit hexadecimal representation.
- The exception's HResult property and inner COMException preserve the original native HRESULT.

Do not discard HRESULTs, treat all non-zero values as failures, or replace native failure details with generic messages. Device-removal reason and operation-specific context must be captured by later device/queue call sites when those facilities exist.

## Native lifetime and ownership

This task creates no COM objects. Later implementation must nevertheless follow these rules from its first native object:

- Every owned COM reference has one clearly defined release responsibility.
- Borrowed native pointers are never released as though they were owned.
- AddRef/Release transfers are explicit and documented.
- Disposal is deterministic and idempotent; no finalizers are introduced.
- Native objects referenced by submitted GPU work are retained until the relevant queue completion point has passed.
- Device loss and teardown must not turn uncertain GPU completion into premature native destruction.

The common GraphicsDeviceObject disposal conventions are the lifecycle baseline for future graphics-owned wrappers.

## Performance principles

Correctness and synchronization take priority over micro-optimization. Future hot paths should avoid unnecessary managed allocations, LINQ, boxing, transient collections, and hidden wrapper allocations. Prefer spans, caller-owned memory, reusable storage, and explicit ownership where these improve measured behavior without weakening safety. Do not assume a generated binding call or wrapper is allocation-free; inspect its implementation and measure before making performance claims.

## Tests and Windows CI

The hardware-independent test project is Tests/Auralis.Graphics.Direct3D12.Tests. It covers HRESULT interpretation/diagnostics, pure runtime availability decisions, current-host probe consistency, and compile-time access to the pinned generated Silk.NET binding types. These tests do not create a Direct3D 12 device and do not prove GPU execution.

.github/workflows/d3d12-validation.yml runs on Windows with the .NET 10 SDK. It restores the solution, builds the backend and both graphics test projects in Release, then runs the dedicated backend tests and all common graphics contract tests. Native adapter/device/command/presentation integration tests will be added in the tasks that implement those operations.

## Implementation roadmap

The remaining ordered backend work is tracked in [umbrella issue #14](https://github.com/AuralisEngine/Auralis/issues/14):

- **D3D12-02 / D3D12-03:** DXGI adapter/provider implementation, native device creation, feature negotiation, capabilities, and health.
- **D3D12-04 / D3D12-05:** queues, command allocators/lists, fences, cross-queue synchronization, and deferred destruction.
- **D3D12-06 / D3D12-07 / D3D12-08:** GPU resources, explicit heap placement/memory allocation, and resource-state/barrier translation.
- **D3D12-09 / D3D12-10 / D3D12-11:** descriptors, root signatures, graphics/compute pipelines, command recording, and dynamic rendering.
- **D3D12-12:** Windows presentation targets and DXGI swapchains.
- **D3D12-13 through D3D12-17:** queries/diagnostics, ray tracing, tiled resources, variable-rate shading, device groups, and multiview where semantics and hardware support permit.
- **D3D12-18:** integration hardening, CI expansion, feature/limitation documentation, and hardware validation evidence.

Each task has its own independently reviewable PR. This foundational PR deliberately does not implement those later systems.

## Native Binding Evaluation and Selection

### 1. Evaluation scope and research methodology

**Research date: 2026-11-28.** This is a version-pinned engineering decision record, not a claim that the compared packages are interchangeable or that every symbol in every Windows SDK has been exhaustively audited.

The evaluated products are the Direct3D 12 and DXGI binding packages (or package families) listed below. The Silk.NET version is fixed by the Auralis project configuration. For the alternatives, the selected versions are the latest stable package releases verifiable from their package/version pages on the research date. The TerraFX package is its Windows SDK-aligned aggregate package; Vortice is evaluated as its separate D3D12 and DXGI packages; SharpDX is evaluated at its final 4.2.0 release.

Evidence priority was: package metadata and release history; official project repositories, README/changelog and license files; and API/source declarations where a specific claim could be established. Windows SDK documentation is used to distinguish an interface being represented by a binding from runtime/OS/driver availability. A package's presence, interface type name, or target framework alone does not prove that every method, structure revision, runtime capability, or device feature works on a given machine.

Coverage statements below therefore use three evidence levels:

- **Package/repository established:** the project distributes a D3D12/DXGI binding and documents the relevant API family.
- **Version-specific symbol established:** a particular interface or member has been verified in the evaluated version's generated/source API. This record only claims this level where explicitly stated.
- **Not exhaustively audited:** a complete symbol-by-symbol comparison of every D3D12/DXGI interface revision was not performed. This is not evidence that an API is missing.

The package metadata and source references are linked in [References and sources](#11-references-and-sources). Auralis's current backend implementation is still foundation-only. Its tests establish compile-time access to ID3D12Device and IDXGIFactory4, not runtime D3D12 feature coverage.

### 2. Candidates and evaluated versions

| Candidate | Evaluated package/release | Scope and package model | Framework evidence | License evidence |
|---|---|---|---|---|
| **Silk.NET** | **2.23.0**: Silk.NET.Direct3D12 and Silk.NET.DXGI | Separate low-level API binding packages; generated API surface | NuGet assets include netstandard2.0, netcoreapp3.1 and net5.0; newer frameworks such as net10.0 are computed compatible | MIT, as identified by the official NuGet package/repository |
| TerraFX | TerraFX.Interop.Windows **10.0.28000** | SDK-aligned Windows/DirectX interop package including D3D12 and DXGI headers | Package contains a net10.0 asset; newer-framework compatibility is computed by NuGet | MIT, stated in package README/repository |
| Vortice | Vortice.Direct3D12 **3.8.3** and Vortice.DXGI **3.8.3** | Separate API-specific packages using generated SharpGen-based bindings and managed wrapper APIs | Packages contain net8.0, net9.0 and net10.0 assets | MIT, stated by the project/package metadata |
| SharpDX | SharpDX.Direct3D12 **4.2.0** and SharpDX.DXGI **4.2.0** | Managed DirectX wrapper family with COM object abstractions | D3D12 package assets include .NET Standard 1.1 and .NET Framework 4.0/4.5 (plus UAP); no net10.0-specific asset | MIT; repository is archived and read-only |

Version references: [Silk.NET.Direct3D12 2.23.0](https://www.nuget.org/packages/Silk.NET.Direct3D12/2.23.0), [Silk.NET.DXGI 2.23.0](https://www.nuget.org/packages/Silk.NET.DXGI/2.23.0), [TerraFX.Interop.Windows 10.0.28000](https://www.nuget.org/packages/TerraFX.Interop.Windows/10.0.28000), [Vortice.Direct3D12 3.8.3](https://www.nuget.org/packages/Vortice.Direct3D12/3.8.3), [Vortice.DXGI 3.8.3](https://www.nuget.org/packages/Vortice.DXGI/3.8.3), [SharpDX.Direct3D12 4.2.0](https://www.nuget.org/packages/SharpDX.Direct3D12/4.2.0), and [SharpDX releases](https://github.com/sharpdx/SharpDX/releases).

### 3. Direct3D 12 and DXGI coverage comparison

All four projects are intended to expose DirectX APIs to .NET. The meaningful difference is not whether a package has a D3D12 name, but how its generated or wrapped API tracks SDK revisions and how much control the application retains over native calls and ownership.

| Area | Silk.NET 2.23.0 | TerraFX 10.0.28000 | Vortice 3.8.3 | SharpDX 4.2.0 |
|---|---|---|---|---|
| Core D3D12 interfaces and device/queue/list/resource APIs | Dedicated D3D12 package; core COM entry types verified in Auralis tests; complete symbol audit not performed here | SDK-header-derived D3D12 declarations; broad one-to-one interop goal | Dedicated D3D12 package and high-level wrapper surface | D3D12 wrapper package; based on an older SDK generation |
| Heaps, barriers, descriptors, root signatures and PSOs | Binding package exists; exact revision/member coverage must be checked against 2.23.0 source before each feature is adopted | Strong fit for direct native structure/method mapping; exact SDK header content is version-bound | High-level wrapper APIs; check the package source/changelog for exact revisions | Older wrapper surface; modern revisions cannot be assumed |
| Enhanced Barriers | Not certified by the ID3D12Device/IDXGIFactory4 compile-time smoke test; inspect exact command-list interface and barrier structures in 2.23.0 | SDK 10.0.28000 is the newest SDK-aligned package in this comparison; inspect emitted declarations for exact interfaces | Inspect 3.8.3 generated API for the relevant command-list revision and barrier structures | No current SDK update stream; do not assume support |
| DXR and acceleration structures | D3D12 package family is present; exact device/command-list revisions and structure coverage require symbol audit | D3D12 headers are included in the package's stated scope; exact symbols are tied to SDK 10.0.28000 | D3D12 package supports this API family; verify exact interfaces/method overloads in 3.8.3 | Historical D3D12 coverage; modern feature revisions unverified |
| Tiled resources / sparse residency | Must verify exact reserved-resource, tile-mapping and newer residency interfaces in 2.23.0 | Native declarations track the included SDK headers; runtime support remains separate | Verify exact API surface in the generated version | Not a safe assumption for modern interfaces |
| VRS and later D3D12 interfaces | Must verify exact interface revision and methods; runtime/driver support is independent | SDK-aligned declarations offer the newest header baseline among evaluated versions | API surface depends on the generated SDK revision used for 3.8.3 | Old release; later interfaces should be treated as unavailable unless source proves otherwise |
| DXGI factory, adapters, swapchains, outputs, formats and presentation | Dedicated DXGI package; IDXGIFactory4 compile-time type verified; later factory/swapchain interfaces need explicit checks | DXGI headers are included in the Windows package | Dedicated DXGI package with modern wrapper surface | DXGI wrapper package exists, but is tied to the old release line |
| HDR/color spaces, tearing, frame latency and modern DXGI revisions | Exact interface and method availability must be checked in 2.23.0; not proven by the current smoke test | Check included DXGI declarations and the Windows SDK/runtime requirement for each interface | Check exact 3.8.3 declarations and wrapper support | Do not assume newer DXGI interface revisions |
| Device removal/reset diagnostics | HRESULT values can be retained at call sites; device-removal interfaces/reasons still need implementation | Low-level HRESULT and interface access is consistent with the direct interop model | HRESULT and wrapper exception paths need deliberate preservation | Wrapper exception model must be checked to ensure native HRESULT details survive |

**Important distinction:** D3D12 and DXGI are versioned native APIs. A managed binding can expose an interface declaration even when the current OS, installed graphics runtime, driver, adapter, or device does not implement the corresponding feature. Conversely, a missing convenience wrapper does not necessarily mean the native SDK lacks the API. For Auralis, every capability must be represented by the common contract only when the backend can query and implement it correctly.

**Coverage interpretation by candidate**

- **Silk.NET:** the package pair provides dedicated D3D12 and DXGI binding surfaces, and Auralis has compile-time evidence for ID3D12Device and IDXGIFactory4 in the pinned assemblies. The 2.23.0 release notes identify an updated Windows SDK baseline (10.0.26100) and DirectX Agility SDK baseline (1.618.5). This is meaningful version evidence, but it does not replace a symbol-level audit for Enhanced Barriers, DXR revisions, sparse residency, VRS, or later DXGI interfaces. Auralis must verify the exact required declaration and method at implementation time.
- **TerraFX:** the 10.0.28000 package explicitly describes bindings for Windows SDK 10.0.28000.1721 and DirectX Agility SDK 1.620.2, and lists D3D12 and DXGI among the included headers. It is the newest SDK-aligned header baseline among the versions compared here. That is a genuine technical strength for early access to newly introduced SDK declarations. The package's stated goal is close-to-C, blittable interop, not an engine-specific ownership abstraction.
- **Vortice:** the project explicitly distributes D3D12 and DXGI packages and describes bindings for these APIs. The project README documents modern .NET targets and directs users interested in high-performance low-level bindings to Vortice.Win32. The D3D12 package depends on Vortice.DXGI and SharpGen.Runtime. Its wrapper API can improve C# ergonomics, but the exact available wrapper surface must be checked in the 3.8.3 source/changelog rather than inferred from the project-wide feature list.
- **SharpDX:** the 4.2.0 D3D12 and DXGI packages exist and expose the historical DirectX wrapper model. However, the upstream repository explicitly states that development stopped in March 2019 and that the repository is archived/read-only. It predates the Windows SDK baselines used by the other candidates, so modern interface coverage cannot be assumed.

No complete symbol-by-symbol coverage matrix for every interface listed in the task is claimed by this document. In particular, no assertion is made that a named feature is absent from Silk.NET 2.23.0 merely because the current Auralis tests do not reference it. A future implementation task must record the exact interface, method, structure, enum, and package version it uses.

### 4. Binding and COM ownership model comparison

#### Silk.NET

Silk.NET presents low-level generated API bindings in API-specific namespaces/packages. This keeps the native vocabulary close to the DirectX API and gives Auralis direct control over pointers, native structures, HRESULT interpretation, and call sequencing. It suits an engine that wants its own resource and synchronization abstractions rather than inheriting a second, library-defined rendering object model.

Generated does not mean automatically safe, complete, allocation-free, or ownership-correct. Auralis must inspect the generated declarations and call implementations actually used. It must explicitly distinguish owned COM references from borrowed pointers, query interfaces deliberately, and release every owned reference exactly once. Where a generated interface is represented through a pointer/vtable-oriented structure, its use must remain within a small, documented unsafe boundary.

#### TerraFX

TerraFX describes its goal as providing blittable, trim-safe, AOT-compatible interop close to the underlying C API. This is attractive when exact native layouts, pointer semantics, and current SDK headers are the primary objective. Its low-level nature also means the caller is responsible for the lifetime discipline: the existence of a generated COM interface does not by itself establish whether a returned pointer is owned, borrowed, or transferred.

For Auralis, TerraFX's SDK alignment is a strength, but selecting it would mean adopting a different native declaration and invocation model. The binding choice is already fixed; the SDK coverage advantage is a research observation, not authorization to introduce a second interop package.

#### Vortice

Vortice uses SharpGen-based generation and provides a more idiomatic managed wrapper layer around DirectX APIs. Its public API can offer conveniences such as managed COM wrapper objects, typed overloads, and IDisposable-oriented usage. These conveniences can be productive in applications and tooling. They also create additional design decisions for a low-level engine: whether each operation creates or returns a wrapper object, how AddRef/Release is encoded, whether a method returns an owned wrapper or borrowed reference, and whether helper conversions allocate.

The package depends on SharpGen.Runtime, and its README notes SharpGen as part of the generation/build process. The wrapper layer is not intrinsically slow; the cost must be measured at actual call sites. Its additional abstraction is simply not the ownership model Auralis has selected for the backend.

#### SharpDX

SharpDX historically provided managed DirectX wrapper classes, including COM object abstractions and deterministic disposal patterns. This model can be convenient and familiar, but its maintenance status is decisive for a new engine backend: the upstream project retired in 2019 and is archived. Even if a given historical API works, it is not an appropriate basis for tracking modern D3D12/DXGI interfaces in a project whose backend is still being built.

#### COM ownership rules Auralis must enforce regardless of binding

COM uses reference counting. A successful creation call or QueryInterface-style operation commonly returns an owned reference that must eventually be released; a pointer borrowed from another object must not be released unless ownership was explicitly acquired. AddRef and Release are native lifetime operations, not substitutes for GPU synchronization.

Auralis will use the following rules:

1. Every owned COM reference has one documented release responsibility. Ownership transfers must be explicit.
2. Borrowed pointers remain borrowed. A borrowed interface must not be wrapped in an owning disposable object unless an additional reference is acquired.
3. QueryInterface results are treated according to the native contract, including the returned reference ownership.
4. Dispose is deterministic and idempotent. No finalizers are introduced in performance-sensitive native ownership paths.
5. Native references used by in-flight GPU work remain alive until the relevant queue fence/completion point makes release safe. COM reference count reaching zero does not prove the GPU has finished using the object.
6. Exception paths must release partially created owned objects and must not double-release references already transferred.
7. Device-loss teardown must not mistake a failed wait or removed device for proof that arbitrary GPU work completed.

The selected binding does not automatically guarantee any of these properties. They are backend invariants that must be implemented and tested by Auralis.

### 5. .NET compatibility and dependency implications

**Silk.NET 2.23.0.** NuGet lists .NET Standard 2.0, .NET Core 3.1, and .NET 5.0 assets. NuGet's compatibility calculation includes net10.0, and the current Auralis backend targets net10.0. This establishes package compatibility for the target framework at restore/build level. It does not independently certify NativeAOT, trimming, every Windows architecture, or all deployment modes. The project currently references only Silk.NET.Direct3D12 and Silk.NET.DXGI directly; transitive packages are visible in the PR's Windows dependency audit. Keep these dependencies private to the Direct3D12 implementation assembly.

**TerraFX 10.0.28000.** The package targets net10.0 and documents no package dependencies on its NuGet page. Its SDK-aligned package is attractive for a .NET 10 Windows-only native interop project. TerraFX explicitly states goals of blittability, trimming safety, and AOT compatibility. Those are project goals and should still be verified against the precise methods and application deployment configuration before treating them as an Auralis guarantee.

**Vortice 3.8.3.** The D3D12 package provides net8.0, net9.0 and net10.0 assets. It depends on SharpGen.Runtime and Vortice.DXGI; therefore the DXGI and generator runtime dependencies are part of the dependency graph. The project README identifies modern C# and .NET 9/.NET 10 support. Auralis would need to account for the transitive runtime package and any wrapper behavior in NativeAOT/trimming tests; no blanket AOT guarantee is claimed here.

**SharpDX 4.2.0.** The D3D12 package includes .NET Standard 1.1 and .NET Framework 4.0/4.5 assets, with dependencies on SharpDX and SharpDX.DXGI. This can provide compatibility with older .NET application targets, but it is not a modern net10.0-specific package and the project is archived. Framework compatibility metadata should not be mistaken for modern runtime support or active maintenance.

For all candidates, Windows architecture support must be distinguished from managed TFM compatibility. D3D12 itself is a Windows native API; a portable managed target does not make the native backend portable. Auralis's backend is intentionally Windows-specific while Auralis.Graphics remains backend-independent.

### 6. Performance and allocation considerations

There is no evidence in the cited package metadata establishing a universal performance winner among Silk.NET, TerraFX, and Vortice for Auralis's future D3D12 call patterns. No benchmark in this decision record compares equivalent calls, JIT/AOT modes, wrapper lifetimes, or GPU workloads. No managed binding can improve or degrade GPU execution time merely by its package identity.

The relevant CPU-side costs to measure include:

- The generated/native call path and interface dispatch.
- Any managed wrapper construction and disposal.
- Whether a call uses a direct pointer, delegate, function pointer, or an additional helper layer.
- Temporary arrays, spans, strings, marshaling, boxing, or hidden allocations.
- COM AddRef/Release and QueryInterface behavior introduced by wrapper helpers.
- Exception construction and diagnostic formatting on failure paths.
- Whether call-site abstractions inhibit inlining or add branches in command recording.

A low-level generated binding can make it possible to issue calls without allocating a managed wrapper per call, but it does not prove that all convenience overloads or helper APIs are allocation-free. TerraFX's blittable design goals and Silk.NET's generated binding approach are architectural characteristics, not benchmark results. Vortice wrapper APIs may allocate in some usage patterns, but that must be established from the exact method implementation and measurement rather than assumed. SharpDX's historical wrapper model likewise cannot be assigned a numeric overhead without a benchmark.

Auralis should benchmark representative operations once implementation exists: command-list recording throughput, descriptor writes, resource creation/destruction, barrier translation, QueryInterface frequency, and frame submission. Use warmed Release builds, report runtime/CPU/OS/driver, measure allocations separately from elapsed time, compare equivalent native operations, and include both JIT and NativeAOT only when both are intended deployment modes. GPU timing must be measured separately from CPU submission time.

The engine's policy remains: avoid unnecessary managed wrapper allocations in hot paths, prefer caller-owned/reusable storage, and preserve correctness and explicit ownership. Do not bypass a clear ownership boundary merely to remove a measured allocation without proving the lifetime behavior remains correct.

### 7. Maintenance and licensing comparison

| Candidate | Observable maintenance evidence | License |
|---|---|---|
| Silk.NET | Official 2.23.0 NuGet release published 2026-01-23; package release notes describe continued 2.x maintenance while 3.0 is in development. Official repository is active. | MIT |
| TerraFX | Official NuGet package 10.0.28000 published 2026-11-16 and tracks Windows SDK/Agility SDK updates. Repository describes .NET Foundation support. | MIT |
| Vortice | 3.8.3 packages published 2026-03-04; official repository README and changelog document ongoing work and modern framework targets. | MIT |
| SharpDX | Upstream repository archived 2019-03-30; README explicitly says no longer under development or maintenance. Latest listed release is 4.2.0. | MIT |

These are dated observations, not forecasts. Release activity does not guarantee future maintenance, and a project with a slower cadence is not automatically unsuitable. For Auralis, tracking new D3D12/DXGI SDK revisions is important because the engine intends to expose modern capabilities over time. The update path and ability to audit generated declarations are therefore more important than popularity or download count.

License identifiers and package/repository statements are linked below. NuGet metadata reports MIT for the evaluated packages; the upstream repository license files are the authoritative project-level references. Transitive dependencies have their own licenses and should be included in Auralis's normal software bill of materials and redistribution review. This is an engineering comparison, not legal advice.

### 8. Auralis-specific engineering evaluation

Auralis deliberately separates three layers:

- **Auralis.Graphics** defines backend-independent contracts. It must not expose Silk.NET, DXGI, D3D12, Win32, or other native implementation types.
- **Auralis.Graphics.Direct3D12** owns the selected Silk.NET packages, unsafe native interop, HRESULT translation, device/runtime interaction, and native object lifetimes.
- **Auralis.Rendering** owns higher-level rendering systems such as render graphs, renderer scheduling, materials, and frame orchestration. It must not become a second native binding layer.

For this architecture, Silk.NET's low-level generated API packages are a suitable fit because they let the backend work with native D3D12/DXGI vocabulary while keeping ownership and engine abstractions under Auralis's control. The packages are isolated in the implementation project, and the current tests verify that the pinned Direct3D12 and DXGI assemblies expose the intended entry types.

The alternatives have legitimate strengths:

- TerraFX offers a newer Windows SDK-aligned declaration set and explicitly emphasizes blittable, trim-safe and AOT-compatible interop. That is a strong low-level alternative in general, but it is not the binding selected for Auralis.
- Vortice offers broad modern DirectX coverage, managed wrapper ergonomics, and current .NET assets. Its higher-level wrapper model and SharpGen.Runtime dependency are valid trade-offs, but Auralis is standardizing on Silk.NET's binding surface and its own lifetime abstractions.
- SharpDX has a mature historical wrapper API and broad legacy DirectX coverage, but its archived status and 2019 retirement make it unsuitable for a new backend expected to track current APIs.

Auralis does not select Silk.NET on a claim that it is universally faster, has the newest headers, or exposes every current D3D12 interface. The decision is a project-level consistency choice: one native binding family, one set of generated API types, one unsafe boundary, and no cross-library COM ownership ambiguity. That consistency is especially valuable when implementing device creation, resource management, multi-queue synchronization, descriptors, pipelines, presentation, DXR, and sparse residency across a sequence of separately reviewed tasks.

The current PR is foundation work, not evidence that the complete graphics backend exists. Its compile-time binding tests prove only that ID3D12Device and IDXGIFactory4 are available. The future implementation tasks must verify and test the exact API surface they consume.

### 9. Final decision: Silk.NET 2.23.0

**Selected binding: Silk.NET 2.23.0**

- **Silk.NET.Direct3D12 2.23.0**
- **Silk.NET.DXGI 2.23.0**

This selection is mandatory and exclusive for all low-level Direct3D 12 and DXGI implementation in Auralis.Graphics.Direct3D12.

The reasons are:

1. It provides dedicated Direct3D 12 and DXGI binding packages, with the intended public interface entry types compile-checked by the backend test project.
2. Its generated low-level binding model leaves Auralis in control of the native call boundary, COM ownership, HRESULT policy, GPU lifetime rules, and engine-facing abstractions.
3. The package versions are pinned and their direct references are confined to the implementation assembly. The common contracts and future Auralis.Rendering layer remain free of native binding dependencies.
4. It avoids mixing COM representations, ownership conventions, generated types, and dependency/runtime models from multiple binding libraries.
5. It is compatible with the backend's net10.0 target according to NuGet's package framework metadata, while keeping the project dependency graph explicit and auditable.

The alternatives are viable technologies with different strengths, not universally inferior products. TerraFX provides a newer SDK-aligned low-level header baseline; Vortice provides modern managed wrappers and broad API coverage; SharpDX provides a historical managed DirectX wrapper model but is retired. They were not selected because Auralis has standardized its backend on Silk.NET and values one consistent native binding and ownership boundary over combining competing models.

**Binding policy**

- Silk.NET is mandatory and exclusive in the Direct3D 12 backend.
- No second native binding may be added without an explicit architectural decision by the project owner.
- Do not add TerraFX, Vortice, SharpDX, or another library as a workaround for a missing Silk.NET declaration.
- Do not create a handwritten COM/vtable layer as a substitute for a missing binding entry point.
- Any unavailable API must be documented by its exact native symbol/interface/member and the pinned package version in which it was checked.
- A missing API must not trigger an automatic switch to another binding. Investigate the official Silk.NET source/release history and record the exact gap for an explicit architectural decision.
- Performance assumptions must be validated against actual Auralis call sites and reproducible benchmarks. No allocation-free or faster-than-alternative guarantee is implied by this decision.

### 10. Known limitations and future reassessment criteria

The following remain explicitly unverified or unimplemented at this foundation stage:

- A complete symbol-by-symbol audit of every D3D12 interface revision, including Enhanced Barriers, DXR, acceleration-structure build/update paths, tiled resources/sparse residency, VRS, and newer device/command-list interfaces.
- A complete symbol-by-symbol audit of modern DXGI factory/swapchain revisions, output/display metadata, color-space/HDR APIs, tearing, frame-latency controls, and presentation diagnostics.
- Runtime availability of any particular interface or feature on a Windows build, adapter, driver, or device.
- NativeAOT and trimming behavior of the exact future call paths used by the backend.
- Allocation and throughput characteristics of actual command recording and wrapper usage.
- COM ownership correctness in real device/resource creation and exceptional teardown paths; no COM objects are created in this foundation task.
- Hardware validation, GPU execution, and device-removal integration tests.

When implementing a feature, record the exact native API requirement, the Silk.NET 2.23.0 symbol(s), the relevant Windows SDK/runtime minimum, the device feature query/driver requirement, and tests proving the backend behavior. If the symbol is absent, document the exact missing name and source inspection performed. Do not infer a gap from a missing convenience overload if the native call is exposed through another documented generated entry point.

Reassessment of the binding decision requires an explicit architectural decision, not an implementation agent's unilateral package substitution. A future review may consider updated Silk.NET versions or a deliberate binding-policy change, but this document does not authorize either.

### 11. References and sources

All external references below were checked for this decision record on **2026-11-28**. Versioned NuGet URLs are used where possible so a future reader can inspect the same package release.

#### Silk.NET 2.23.0

- [Silk.NET.Direct3D12 2.23.0 — NuGet package, frameworks, dependencies and license metadata](https://www.nuget.org/packages/Silk.NET.Direct3D12/2.23.0)
- [Silk.NET.DXGI 2.23.0 — NuGet package, frameworks, dependencies and license metadata](https://www.nuget.org/packages/Silk.NET.DXGI/2.23.0)
- [Silk.NET official source repository](https://github.com/dotnet/Silk.NET)
- [Silk.NET 2.23.0 release/tag history](https://github.com/dotnet/Silk.NET/releases)
- [Silk.NET repository license](https://github.com/dotnet/Silk.NET/blob/main/LICENSE)
- [Silk.NET Direct3D12 package source/project directory](https://github.com/dotnet/Silk.NET/tree/main/src)
- [Silk.NET 2.23.0 NuGet package page with release notes and framework assets](https://www.nuget.org/packages/Silk.NET.Direct3D12/2.23.0)

#### TerraFX

- [TerraFX.Interop.Windows 10.0.28000 — NuGet package, SDK baseline, frameworks and license statement](https://www.nuget.org/packages/TerraFX.Interop.Windows/10.0.28000)
- [TerraFX.Interop.Windows official repository](https://github.com/terrafx/terrafx.interop.windows)
- [TerraFX repository license](https://github.com/terrafx/terrafx.interop.windows/blob/main/LICENSE)
- [TerraFX repository README and documented goals/header coverage](https://github.com/terrafx/terrafx.interop.windows#readme)

#### Vortice

- [Vortice.Direct3D12 3.8.3 — NuGet package, frameworks and dependencies](https://www.nuget.org/packages/Vortice.Direct3D12/3.8.3)
- [Vortice.DXGI 3.8.3 — NuGet package, frameworks and dependencies](https://www.nuget.org/packages/Vortice.DXGI/3.8.3)
- [Vortice.Windows official repository and README](https://github.com/amerkoleci/Vortice.Windows)
- [Vortice.Windows changelog](https://github.com/amerkoleci/Vortice.Windows/blob/main/CHANGELOG.md)
- [Vortice.Windows repository license](https://github.com/amerkoleci/Vortice.Windows/blob/main/LICENSE)
- [SharpGenTools source repository](https://github.com/SharpGenTools/SharpGenTools)

#### SharpDX

- [SharpDX.Direct3D12 4.2.0 — NuGet package, frameworks and dependencies](https://www.nuget.org/packages/SharpDX.Direct3D12/4.2.0)
- [SharpDX.DXGI 4.2.0 — NuGet package](https://www.nuget.org/packages/SharpDX.DXGI/4.2.0)
- [SharpDX official archived repository and retirement notice](https://github.com/sharpdx/SharpDX)
- [SharpDX 4.2.0 release](https://github.com/sharpdx/SharpDX/releases/tag/v4.2.0)
- [SharpDX repository license](https://github.com/sharpdx/SharpDX/blob/master/LICENSE)

#### Native API and platform semantics

- [Microsoft Direct3D 12 programming guide](https://learn.microsoft.com/windows/win32/direct3d12/direct3d-12-programming-guide)
- [Microsoft Direct3D 12 reference](https://learn.microsoft.com/windows/win32/direct3d12/direct3d-12-reference)
- [Microsoft DXGI reference](https://learn.microsoft.com/windows/win32/api/_dxgi/)
- [Microsoft COM reference counting rules](https://learn.microsoft.com/windows/win32/com/rules-for-managing-reference-counts)
- [Microsoft QueryInterface rules](https://learn.microsoft.com/windows/win32/com/rules-for-implementing-queryinterface)
- [Microsoft Direct3D 12 Enhanced Barriers overview](https://learn.microsoft.com/windows/win32/direct3d12/enhanced-barriers)
- [Microsoft Direct3D 12 raytracing](https://learn.microsoft.com/windows/win32/direct3d12/direct3d-12-raytracing)
- [Microsoft DXGI swap-chain presentation](https://learn.microsoft.com/windows/win32/direct3ddxgi/dxgi-flip-model)

Package metadata and repository license declarations are evidence for the listed package/repository licensing, not a complete legal audit of all transitive dependencies. Check the resolved dependency graph and each dependency's license before redistribution. This document is an engineering comparison, not legal advice.
