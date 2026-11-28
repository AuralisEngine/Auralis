# Auralis.Graphics.Direct3D12

The Direct3D 12 backend implements the backend-independent contracts in `Auralis.Graphics`.
All D3D12, DXGI, Win32 interop and native implementation code stays in this project.
The backend is a low-level GPU implementation; render graphs, materials, renderer scheduling,
scene rendering and other higher-level systems belong in `Auralis.Rendering`.

## Native binding decision

**Silk.NET is the required and exclusive native binding for this backend.**

The backend references:

- [Silk.NET.Direct3D12 2.23.0](https://www.nuget.org/packages/Silk.NET.Direct3D12/2.23.0)
- [Silk.NET.DXGI 2.23.0](https://www.nuget.org/packages/Silk.NET.DXGI/2.23.0)

Both packages are published by the Silk.NET project under the MIT license. Their package assets target
.NET 5.0 and compatible lower-level targets, and are consumable by this project's .NET 10 target.
Silk.NET exposes generated, low-level bindings over the Direct3D 12 and DXGI native APIs; the backend
retains explicit responsibility for COM ownership, pointer validity, synchronization and deterministic
release. The generated call surface is intended to keep binding overhead negligible, but performance
must still be measured in the backend's actual hot paths.

Alternatives considered:

| Binding | Coverage and runtime fit | Interop/performance model | Decision |
| --- | --- | --- | --- |
| **Silk.NET.Direct3D12 + Silk.NET.DXGI 2.23.0** | Direct3D 12 and DXGI bindings; compatible with .NET 10 through its .NET 5 target assets. MIT. | Generated low-level bindings with direct native access and explicit lifetime management. | **Selected and mandatory.** |
| TerraFX.Interop.Windows 10.0.28000 | Broad Windows SDK/D3D12/DXGI surface with a .NET 10 package line. MIT. | Close-to-SDK interop; requires explicit pointer and COM management. | Rejected: Auralis requires Silk.NET. |
| Vortice.Direct3D12 + Vortice.DXGI 3.8.3 | Broad Direct3D 12/DXGI coverage and modern .NET support. MIT. | Higher-level COM wrappers and convenience APIs; wrapper behavior would require hot-path auditing. | Rejected: a second binding is unnecessary and violates the Silk.NET-only rule. |

Do not introduce TerraFX, Vortice, SharpDX, or another native graphics binding. If Silk.NET lacks a
specific required symbol or interface, document the exact missing API surface and stop for an
architectural decision. Do not silently mix bindings.

## Internal architecture

The implementation is organized around explicit, testable boundaries:

- `Interop`: HRESULT conversion and native error diagnostics.
- `Runtime`: Windows and D3D12/DXGI loader availability checks.
- `Adapters`: DXGI adapter discovery and metadata translation (subsequent task).
- `Devices`: native device creation and feature negotiation (subsequent task).
- `Resources`, `Memory`, `Commands`, `Synchronization`, `Pipelines`, and `Presentation`:
  backend-owned GPU functionality introduced by their respective implementation tasks.

No reflection-based discovery, runtime assembly scanning, finalizers, or native API types in the common
`Auralis.Graphics` assembly are permitted.

## Runtime requirements

The provider is Windows-only. Runtime loader availability is distinct from adapter availability,
device creation success, feature-level support, driver capability, and features enabled on a logical
device. The runtime probe only reports whether the OS is Windows and whether the system DXGI and D3D12
libraries can be loaded from the System32 search location.

## Validation

The dedicated `Tests/Auralis.Graphics.Direct3D12.Tests` project contains hardware-independent tests
for native error conversion and runtime availability decisions. Native device/adapter integration
tests are added by the provider and device tasks.
