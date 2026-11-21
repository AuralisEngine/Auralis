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

**Validation:** the latest reported ECS validation workflow for the implementation commit succeeded (restore, build, and ECS test steps). The PR reports 24 passing tests. A README-only synchronization commit follows that validated implementation commit. No before/after benchmark results or numerical performance gains are claimed.

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

**Validation:** the latest reported FBX validation workflow for the implementation commit succeeded (restore, solution build, and FBX test steps). A README-only synchronization commit follows that validated implementation commit.

### [PR #11 — Implement and expand the OpenGL graphics backend](https://github.com/mahdikshk/Auralis/pull/11)

**Status:** Open · Draft  
**Branch:** `feature/opengl-backend-silknet`

This PR continues the existing graphics abstraction with two concrete Silk.NET backends in the same `Auralis.Graphics` assembly:

- Desktop OpenGL 4.5 Core Profile.
- OpenGL ES 3.1+.

The implementation uses host-supplied context providers and keeps native window/context creation outside the graphics library. It preserves the common resource, command-list, pipeline, descriptor-snapshot, synchronization, and deferred native-resource lifetime contracts.

Current work in the branch includes:

- Buffer, texture, texture-view, sampler, shader, descriptor, graphics/compute pipeline, synchronization, and presentation support.
- Desktop OpenGL EXT mesh/task shaders and a separate NV mesh/task path. EXT and NV GLSL dialects cannot be mixed. NV supports direct one-dimensional dispatch; NV indirect remains unsupported because its native `{ count, first }` record does not match Auralis's common three-dimensional indirect command.
- GLES multiple render targets, typed integer clears, independent blending when `GL_EXT_draw_buffers_indexed` is available, native/fallback multi-draw indirect, extension-gated base-vertex/base-instance drawing, ES 3.2 tessellation patch counts, multisample textures, texture views, image-copy extensions, and optional EXT mesh/task shaders.
- Per-format GLES capability reporting. `BGRA8UNorm` and `BGRA8UNormSrgb` are not mapped or advertised; compressed formats and some depth/float usages are extension/version-gated.
- Opt-in desktop and GLES hardware integration runners, hardware-independent regression tests, and a separate BenchmarkDotNet project.

**Platform and API boundaries:**

- The host/platform layer must provide the native context through the existing context-provider contracts.
- Logical graphics, compute, and copy queues share a serialized context command stream; they are not physically independent hardware queues.
- GLES has no native 1D texture target. GLES SPIR-V ingestion is not implemented.
- No common multisample resolve operation exists; `CopyTexture` is not reinterpreted as a resolve.
- Descriptor indexing, update-after-bind, variable descriptor counts, sparse residency, timestamp-query result retrieval, and multi-viewport remain unimplemented because the current common contracts do not express their required semantics.
- Desktop NV indirect mesh drawing remains rejected because its argument layout is incompatible with the common indirect command.

**Validation:** the latest successful Graphics validation run available during this update, [run #44947648797](https://github.com/mahdikshk/Auralis/actions/runs/44947648797), built the hardware-independent graphics tests, desktop integration runner, GLES integration runner, and benchmark project, then passed **232 hardware-independent tests** (0 failed, 0 skipped). That run validated commit `0077928756400bd28cea4f5286b64364f52fd81b`, not the current PR head. The current branch head still requires a fresh CI run. The integration runners are opt-in and have not been executed against real hardware during this continuation; no current driver or GPU result is claimed.
## Planned areas

The solution reserves space for additional engine subsystems, but they should not be considered available features until implementation work lands. Areas include rendering, physics, audio, animation, scene/world management, platform integration, editor tooling, and a serialized asset format.

## License

No license file or repository license metadata is currently present.