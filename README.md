# Auralis

Auralis is an early-stage, modular game-engine and runtime project written in C# for **.NET 10**. It is being developed as a set of libraries: engine foundations, mathematics, graphics contracts, and asset-pipeline components.

The repository is not yet a complete or production-ready game engine. This README distinguishes what is currently on `master` from work that exists only in open draft pull requests.

## Project status

**Development status:** early development; APIs and project boundaries may change.

The default branch currently contains the core libraries and the backend-independent graphics API. Several substantial features are being developed in separate pull requests. Draft PR code is not part of `master` until it is merged.

## What is currently on `master`

| Project | Current scope |
| --- | --- |
| `Auralis.Core` | Foundational runtime utilities, including platform identification, memory helpers, disposal support, and reference-counting primitives. |
| `Auralis.Core.Mathematics` | Engine-owned mathematical types, including vectors, quaternions, matrices, planes, points, and geometric types. |
| `Auralis.Core.IO` | Core I/O project in the solution; its broader functionality is still developing. |
| `Auralis.Engine` | Initial entity/component and scripting types. The archetype-based World/ECS rewrite is tracked separately in [PR #8](https://github.com/mahdikshk/Auralis/pull/8). |
| `Auralis.Graphics` | Backend-independent graphics contracts: device/provider creation, resources, commands, pipelines, synchronization, capabilities, validation, and presentation interop. **The current default branch does not contain a concrete native graphics backend.** |
| `Auralis.Tools.AssetPipeline.Interchange` | Initial FBX document/node model and reader/writer foundation. The full binary/ASCII parser and scene importer are being developed in [PR #9](https://github.com/mahdikshk/Auralis/pull/9). |
| `Tests/Auralis.Graphics.Tests` | Hardware-independent tests for the graphics abstraction and its validation/lifecycle contracts. |

The graphics API is intentionally separate from window creation and platform integration. A host application or platform layer is responsible for supplying native presentation handles; a graphics backend is responsible for translating Auralis contracts to its native API.

## Repository layout

The solution uses the `.slnx` format.

```text
Auralis/
├── Auralis.slnx
├── src/
│   ├── Auralis.Core/
│   │   ├── Auralis.Core/
│   │   ├── Auralis.Core.Mathematics/
│   │   └── Auralis.Core.IO/
│   ├── AuralisRuntime/
│   │   └── Auralis.Engine/
│   ├── Auralis.Graphics/
│   │   └── Auralis.Graphics/
│   └── Auralis.Tools/
│       └── AssetPipeline/
│           └── Auralis.Tools.AssetPipeline.Interchange/
└── Tests/
    └── Auralis.Graphics.Tests/
```

The solution also reserves folders for future areas such as animation, audio, editor tooling, memory, physics, platform integration, rendering, scenes, simulation, and world management. A folder entry in the solution does not imply that the corresponding subsystem is implemented.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/).
- An IDE with `.slnx` support, or the `.NET` command-line tools.

## Getting started

Clone the repository, then restore, build, and run the tests:

```bash
git clone https://github.com/mahdikshk/Auralis.git
cd Auralis

dotnet restore Auralis.slnx
dotnet build Auralis.slnx --configuration Release
dotnet test Auralis.slnx --configuration Release
```

These commands validate the projects and tests currently included in the checked-out branch. The draft pull requests below add their own projects and validation workflows.

## Work in progress

The following snapshot reflects the repository's open pull requests on **21 November 2026**. All three are currently **open draft PRs**; their implementations and test projects are not part of `master` yet. Check GitHub for their live status.

### [PR #8 — Rewrite ECS around archetypes and class-based entities](https://github.com/mahdikshk/Auralis/pull/8)

**Status:** Open · Draft  
**Branch:** `feature/ecs-architecture-rewrite`

This PR replaces per-entity component-dictionary storage with an archetype-first ECS while retaining an object-oriented `Entity` facade.

Current work in the branch includes:

- Archetypes with dense, chunked component columns; managed component arrays by default and explicitly registered unmanaged native columns.
- Compact, generation-based `EntityId` handles, cached queries, value-type query enumerators, and version-checked component views.
- Structural changes through the World owner thread, with an `EntityCommandBuffer` for deferred changes and concurrent command recording.
- Transform processing, script discovery, and a deterministic single-threaded system scheduler.

**Concurrency boundary:** command recording is multi-producer, but World access, queries, structural mutation, and playback remain owner-thread-only. This PR does not implement a parallel ECS scheduler or work stealing.

**Validation:** the latest reported ECS validation workflow for the current PR head succeeded (restore, build, and ECS test steps). The PR reports 24 passing tests. No before/after benchmark results or numerical performance gains are claimed.

### [PR #9 — Add pure-managed FBX parser and scene importer](https://github.com/mahdikshk/Auralis/pull/9)

**Status:** Open · Draft  
**Branch:** `feature/fbx-importer`

This PR expands the interchange project with a pure-managed parser for binary and ASCII FBX and an importer that produces typed, in-memory scene data.

Current work in the branch includes:

- Binary FBX records and typed arrays, including zlib-compressed array payloads; an ASCII parser operating on UTF-8 input.
- Configurable parser resource limits and synchronous memory-based plus asynchronous stream entry points.
- Mesh geometry, triangulation, normals/tangents, colors, multiple UV layers, material assignments, model hierarchy, pivots, and transforms.
- Skin clusters and bone influences, blend-shape data, and transform/blend-shape animation tracks.
- Coordinate-system and unit conversion based on FBX global settings.

**Scope boundaries:** the imported scene is an intermediate representation, not runtime components. The PR does not implement `.AuraAsset` serialization, FBX export, camera/light import, or external texture resolution/copying. Animation curves are currently sampled into linear key tracks; FBX interpolation modes and animation-layer blending are not yet translated.

**Validation:** the latest reported FBX validation workflow for the current PR head succeeded (restore, solution build, and FBX test steps). The PR's earlier validation notes report five passing parser/importer tests; consult the current workflow for the latest run details.

### [PR #11 — Implement and expand the OpenGL graphics backend](https://github.com/mahdikshk/Auralis/pull/11)

**Status:** Open · Draft  
**Branch:** `feature/opengl-backend-silknet`

This PR implements a concrete backend on top of the existing Auralis graphics abstraction, targeting **desktop OpenGL 4.5 Core Profile** and using Silk.NET for OpenGL bindings.

The branch currently covers:

- Injected context-provider integration, device/capability discovery, and explicit backend registration.
- Buffer and texture resources, samplers, shader compilation/linking, descriptors, graphics/compute pipelines, and command recording.
- Direct, indexed, indirect, compute, and extension-dependent mesh/task commands, plus resource copies and memory barriers.
- GL synchronization, deferred native-resource deletion, offscreen swapchain images, blit presentation, resize, and KHR_debug integration.
- Hardware-independent regression tests, a hardware integration harness, and a separate benchmark project.

**Platform and backend boundaries:**

- This PR targets desktop OpenGL 4.5; OpenGL ES is not implemented.
- The host/platform layer must create and provide the native context and drawable through `IOpenGLContextProvider`.
- Graphics, compute, and copy queues are logical roles over one serialized GL context stream, not independent physical queues.
- The new mesh/task, partially-bound descriptor, indirect-command, and full live-device resource-lifetime paths still need actual-driver verification.
- The integration harness and benchmarks are built by CI but are not executed there. No measured throughput or allocation improvements are claimed.

**Validation status:** the latest Graphics validation workflow for the current PR head failed while building the OpenGL benchmark project; the hardware-independent test step was skipped as a result. The PR description records an earlier run with 92 passing graphics tests, but that earlier result does not replace validation of the current head. See [the latest workflow run](https://github.com/mahdikshk/Auralis/actions/runs/44876487875).

## Planned areas

The solution reserves space for additional engine subsystems, but they should not be considered available features until implementation work lands. Areas include rendering, physics, audio, animation, scene/world management, platform integration, editor tooling, and a serialized asset format.

## License

No license file or repository license metadata is currently present.