# Auralis OpenGL Integration Tests

This is a hardware-backed integration test project for the OpenGL backend. It is deliberately separate from the hardware-independent `Auralis.Graphics.Tests` project.

## What this project verifies

Tests exercise the public Auralis graphics API against a real desktop OpenGL 4.5 Core context and an actual driver. They assert observable behavior rather than merely checking that objects can be constructed.

Initial coverage:

- Device creation through explicit backend registration.
- CPU-writable buffer mapping and data integrity.
- GPU buffer-copy submission, fence completion, and readback integrity.

Planned test groups:

- Texture allocation, views, format compatibility, and texture copies.
- Sampler creation and sampler state.
- Shader compilation and diagnostic behavior.
- Graphics pipeline creation, descriptor binding, render-target operations, and draw output verification.
- Compute pipeline dispatch and output verification.
- Indirect draw/dispatch argument handling.
- Resource barriers and visibility across producer/consumer operations.
- Fence signaling, CPU waits, queue-side waits, and deferred resource destruction.
- Presentation surface, swapchain acquisition/present, resize, and disposal when a platform presentation target is supplied.
- Repeated create/use/dispose cycles and native-resource lifetime checks.

## Requirements

- .NET 10 SDK.
- A desktop OpenGL 4.5 Core implementation and a working GPU driver.
- A display/session capable of creating a hidden GLFW-backed OpenGL window. Headless CI needs a configured virtual display or an OS-specific offscreen context provider.

## Run

From the repository root:

```bash
dotnet test Tests/Auralis.Graphics.OpenGL.IntegrationTests/Auralis.Graphics.OpenGL.IntegrationTests.csproj -c Release
```

The tests are intentionally not marked as skipped when no driver/context is available: inability to create the required context is an environment failure and should be visible in CI.

## Design rules

- No dependency on or reuse of a smoke-test executable.
- No assumptions based on window color, triangle appearance, frame timing, or visual inspection.
- Assertions validate resource contents, synchronization completion, state transitions, and rendered/compute results.
- The test fixture owns the native window/context; Auralis owns the graphics device and its resources.
- The fixture disposes the Auralis device before disposing the borrowed native context.
- Hardware-independent validation remains in `Tests/Auralis.Graphics.Tests`; this project covers actual driver/backend behavior.
