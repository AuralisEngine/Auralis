# Auralis.Graphics.Direct3D12

## Responsibility

`Auralis.Graphics.Direct3D12` is the Windows Direct3D 12 implementation assembly for the backend-neutral contracts in `Auralis.Graphics`. It owns native D3D12/DXGI calls, unsafe interop, HRESULT diagnostics, and the lifetime of native objects created by the backend.

`Auralis.Rendering` remains a separate higher-level rendering layer. Native API types must not leak into the common graphics contracts or the higher-level rendering assembly.

## Required native binding

This backend exclusively references:

- [Silk.NET.Direct3D12 2.23.0](https://www.nuget.org/packages/Silk.NET.Direct3D12/2.23.0)
- [Silk.NET.DXGI 2.23.0](https://www.nuget.org/packages/Silk.NET.DXGI/2.23.0)

Both direct package references belong in this project and nowhere in the common graphics contract assembly. Do not add a second native binding or replace a missing generated API with a parallel handwritten COM/vtable implementation. A suspected missing API must first be recorded by exact native symbol and verified against the pinned Silk.NET source.

## Current PR #15 implementation boundary

PR #15 establishes project infrastructure, a System32-only loader probe, HRESULT conversion helpers, compile-time package integration tests, Windows validation and documentation. Its Silk.NET test references `ID3D12Device` and `IDXGIFactory4` only to verify that the generated types compile and come from the expected assemblies. It does not yet invoke a D3D12 or DXGI method, create a COM object, enumerate an adapter, create a device, or execute GPU work.

The full inventory of actual PR usage, the binding comparison, COM ownership findings, source references and binding decision is maintained in [Native Binding Comparison](NATIVE-BINDING-COMPARISON.md).

## Lifetime and performance invariants

Every owned COM reference must have one clear release responsibility. Borrowed pointers must not be released as owned. Disposal at the Auralis owner level must be deterministic and idempotent; do not introduce finalizers in performance-sensitive native ownership paths. Native objects used by submitted GPU work must be retained until the relevant queue completion point permits release. Correct synchronization and ownership take priority over speculative micro-optimizations.

Inspect the actual generated call/helper used at each call site. Do not assume a binding method or convenience wrapper is allocation-free. Avoid unnecessary wrapper objects, allocations, boxing, transient collections and reflection in high-frequency command paths; validate performance with measurements from Auralis workloads.

## Validation and roadmap

The dedicated backend tests currently cover HRESULT interpretation, pure runtime availability decisions and compile-time resolution of the pinned Silk.NET entry types. They do not create a D3D12 device or validate GPU execution. The Windows workflow restores the solution, audits the backend dependency graph, builds the backend and graphics test projects, and runs the backend/common graphics tests.

Adapter discovery, device creation, queue/command execution, resource allocation, descriptors, pipelines, presentation and advanced D3D12 features remain separate tasks tracked by [issue #14](https://github.com/AuralisEngine/Auralis/issues/14). This foundation must not be represented as a completed D3D12 backend.
