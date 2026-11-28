# Auralis.Graphics.Direct3D12

## Purpose and responsibility

Auralis.Graphics.Direct3D12 is Auralis's Windows Direct3D 12 backend. It implements the low-level GPU-facing contracts defined by the backend-independent Auralis.Graphics assembly. This project owns the Direct3D 12 and DXGI bindings, native calls, unsafe interop boundaries, HRESULT diagnostics, and native object lifetime.

This foundational task establishes dependencies, runtime-loader detection, native error conversion, tests, solution integration, and Windows CI. It does not yet create adapters, logical devices, GPU resources, queues, command lists, pipelines, swapchains, or rendering systems.

## Exclusive native binding

The backend uses only these generated Silk.NET bindings:

- [Silk.NET.Direct3D12 2.23.0](https://www.nuget.org/packages/Silk.NET.Direct3D12/2.23.0) for Direct3D 12.
- [Silk.NET.DXGI 2.23.0](https://www.nuget.org/packages/Silk.NET.DXGI/2.23.0) for DXGI.

Both package references are declared directly in Auralis.Graphics.Direct3D12.csproj. They are not referenced by the common graphics assembly or higher-level rendering project. No alternative Direct3D 12/DXGI binding is part of this backend. Do not combine bindings or replace missing generated entry points with handwritten COM/vtable or native API bindings. If a required symbol is unavailable in the pinned Silk.NET release, document the exact missing API and obtain an architectural decision before proceeding.

Silk.NET 2.23.0 package metadata identifies the bindings as compatible with .NET 10 through their supported target frameworks and documents their MIT licensing. The backend consumes the low-level generated namespaces Silk.NET.Direct3D12 and Silk.NET.DXGI. The dedicated test project includes compile-time references to the generated D3D12 and DXGI binding entry types so package/API integration is checked during build. No broader performance or allocation guarantee is inferred from the package description; generated methods and any wrappers used by later tasks must be inspected before hot-path adoption.

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
