using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Per-device fault-injection callbacks used only by friend integration-test assemblies.
/// A null callback leaves the production resource path unchanged.
/// </summary>
internal sealed class OpenGLESSwapChainTestHooks
{
    /// <summary>Supplies a controllable monotonic clock for acquisition deadline tests.</summary>
    internal Func<long>? TimestampProvider { get; set; }

    /// <summary>Overrides zero-timeout presentation-sync polling for deterministic acquisition tests.</summary>
    internal Func<nint, GLEnum>? PollClientWaitSync { get; set; }

    /// <summary>Overrides blocking presentation-sync waits for deterministic timeout tests.</summary>
    internal Func<nint, uint, ulong, GLEnum>? WaitClientWaitSync { get; set; }

    /// <summary>Observes a sync after acquisition has successfully deleted and relinquished it.</summary>
    internal Action<nint>? AfterAcquireSyncDeleted { get; set; }

    /// <summary>Runs immediately before a replacement texture allocation.</summary>
    internal Action? BeforeTextureCreation { get; set; }

    /// <summary>Runs after the replacement texture is registered and owned by its image slot.</summary>
    internal Action<OpenGLESGraphicsTexture>? AfterTextureCreation { get; set; }

    /// <summary>Runs immediately before native framebuffer creation.</summary>
    internal Action<GL>? BeforeFramebufferCreation { get; set; }

    /// <summary>Runs after a newly created framebuffer handle has been stored in its image slot.</summary>
    internal Action<uint>? AfterFramebufferCreated { get; set; }

    /// <summary>Runs immediately before the resize GPU-idle wait.</summary>
    internal Action? BeforeGpuFinish { get; set; }

    /// <summary>Runs immediately before an image framebuffer is deleted.</summary>
    internal Action<uint>? BeforeFramebufferDeletion { get; set; }
}
