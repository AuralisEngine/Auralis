# Auralis.Graphics.Direct3D12

The Direct3D 12 backend is the Windows implementation of the backend-neutral contracts in
`Auralis.Graphics`. Native API bindings and native implementation code belong in this project;
the common graphics assembly must remain free of D3D12, DXGI, Win32 and binding-specific types.

This project implements lower-level GPU operations. Render graphs, materials, renderer scheduling,
scene rendering and other higher-level rendering systems belong in `Auralis.Rendering`.

## Binding decision

The backend uses **TerraFX.Interop.Windows 10.0.28000** as its Direct3D 12 and DXGI interop layer.
The package is referenced only by this project. The selected package line targets .NET 10 and
provides low-level Windows SDK bindings, keeping native ABI types, pointer use and COM ownership
explicit. Its MIT license is compatible with the repository's use. The package is intentionally
low-level: the backend owns deterministic COM lifetime and must not assume that wrapper objects,
calls or allocations are free.

Alternatives reviewed on 2026-11-28:

| Binding | Coverage and target fit | Interop model | Decision |
| --- | --- | --- | --- |
| [TerraFX.Interop.Windows](https://www.nuget.org/packages/TerraFX.Interop.Windows) 10.0.28000 | D3D12 and DXGI Windows SDK surface; .NET 10 package line. | Close-to-SDK, blittable bindings intended for trim/AOT-friendly native interop. Requires explicit low-level lifetime management. MIT. | Selected. |
| [Vortice.Direct3D12](https://www.nuget.org/packages/Vortice.Direct3D12) and [Vortice.DXGI](https://www.nuget.org/packages/Vortice.DXGI) 3.8.3 | Broad D3D12/DXGI coverage and .NET 10 support. | More idiomatic managed wrappers and convenience APIs; wrapper lifetime and allocation behavior would need auditing in performance-sensitive paths. MIT. | Not selected; retain as a fallback if TerraFX has a concrete coverage gap. |
| [Silk.NET.Direct3D12](https://www.nuget.org/packages/Silk.NET.Direct3D12) 2.23.0 | Generated Direct3D bindings; stable 2.x package targets older frameworks but can be consumed by newer .NET targets. | Low-level generated bindings. MIT. | Not selected; its older target and separate 3.x development line provide less benefit for this .NET 10 backend. |

No second native binding is added. Re-evaluate this decision only if a required D3D12/DXGI entry point is absent or a specific maintenance issue is demonstrated. Any change must include an updated comparison and must not introduce overlapping wrappers without a concrete reason.

## Project boundaries

- `Auralis.Graphics` remains the authoritative backend-independent API.
- `Auralis.Graphics.Direct3D12` owns TerraFX, native imports, unsafe code, HRESULT conversion and native objects.
- `Auralis.Rendering` owns higher-level rendering architecture and is not referenced by this backend.
- Provider registration remains explicit; no runtime assembly scanning, reflection-based discovery or reflection-based resource registry is permitted.
- Shader compilation is outside this project. The backend accepts compiled DXIL artifacts and rejects incompatible shader formats.

## Runtime availability

`Runtime/D3D12RuntimeAvailability` probes whether `dxgi.dll` and `d3d12.dll` can be loaded from the Windows System32 search path. On non-Windows systems it reports the backend unavailable without attempting a native load.

This is deliberately not an adapter or feature-level probe. A successfully loaded system library does not prove that a usable adapter exists, that device creation will succeed, or that optional GPU features are supported. DXGI adapter enumeration and native device/feature negotiation are implemented in D3D12-02 and D3D12-03.

## Native error handling

Use `D3D12HResult.ThrowIfFailed` for HRESULT-returning D3D12/DXGI calls. It treats all non-negative values as success and throws `D3D12NativeException` for failed HRESULTs. The exception retains the signed HRESULT, operation name, hexadecimal representation and a `COMException` inner exception carrying the same HRESULT. The exception factory rejects non-failure HRESULTs to preserve this invariant.

Do not discard HRESULTs or replace useful device-removed / DXGI diagnostics with generic exceptions.

## Lifetime and performance rules

- Native COM ownership must be deterministic and documented.
- Follow the common `GraphicsDeviceObject` idempotent disposal model; do not add finalizers.
- Native objects referenced by in-flight GPU work must be retired only after the relevant queue fence has completed.
- Keep hot paths allocation-conscious. Use spans, reusable storage and explicit ownership; avoid reflection, LINQ, boxing and transient collections in command paths.
- Correct synchronization and lifetime take precedence over micro-optimizations.

## Build and test

The backend project targets .NET 10. The dedicated managed test project is
`Tests/Auralis.Graphics.Direct3D12.Tests`; its initial tests do not require a GPU or D3D12 device.

Run from the repository root:

```powershell
dotnet restore Auralis.slnx
dotnet build Tests/Auralis.Graphics.Direct3D12.Tests/Auralis.Graphics.Direct3D12.Tests.csproj --configuration Release
dotnet test Tests/Auralis.Graphics.Direct3D12.Tests/Auralis.Graphics.Direct3D12.Tests.csproj --configuration Release
dotnet build Tests/Auralis.Graphics.Tests/Auralis.Graphics.Tests.csproj --configuration Release
dotnet test Tests/Auralis.Graphics.Tests/Auralis.Graphics.Tests.csproj --configuration Release
```

**Current repository-wide build note:** the first Windows CI attempt also ran `dotnet build Auralis.slnx --configuration Release` and was blocked by an unrelated existing compiler error `CS0161` in `src/Auralis.Tools/AssetPipeline/Auralis.Tools.AssetPipeline.Interchange/FBX/FBXReader.cs` (`FBXReader.GetFBXHeader(ReadOnlyMemory<byte>)` does not return on every path). That source is unchanged by this PR. The D3D12 project and its dedicated tests compiled before the unrelated solution build failed. The D3D12 workflow therefore restores the full solution and builds/tests the backend and common graphics test projects directly, avoiding unrelated projects while preserving the graphics contract baseline. The solution-wide issue should be fixed separately.

Windows native integration tests will be added alongside the tasks that introduce native API execution.
