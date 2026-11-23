using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Per-device fault-injection callbacks used only by friend integration-test assemblies.
/// A null callback leaves the production resource path unchanged.
/// </summary>
internal sealed class OpenGLSwapChainTestHooks
{
    /// <summary>Supplies a controllable monotonic clock for acquisition deadline tests.</summary>
    internal Func<long>? TimestampProvider { get; set; }

    /// <summary>Overrides zero-timeout presentation-sync polling for deterministic acquisition tests.</summary>
    internal Func<nint, ARB>? PollClientWaitSync { get; set; }

    /// <summary>Overrides blocking presentation-sync waits for deterministic timeout tests.</summary>
    internal Func<nint, SyncObjectMask, ulong, ARB>? WaitClientWaitSync { get; set; }

    /// <summary>Observes a sync after acquisition has successfully deleted and relinquished it.</summary>
    internal Action<nint>? AfterAcquireSyncDeleted { get; set; }

    /// <summary>Runs immediately before a replacement texture allocation.</summary>
    internal Action? BeforeTextureCreation { get; set; }

    /// <summary>Runs after a swap-chain texture is created and transferred to its image slot.</summary>
    internal Action<OpenGLGraphicsTexture>? AfterTextureCreated { get; set; }

    /// <summary>Runs after the replacement texture is registered and owned by its image slot.</summary>
    internal Action<OpenGLGraphicsTexture>? AfterTextureCreation { get; set; }

    /// <summary>Runs immediately before native framebuffer creation.</summary>
    internal Action<GL>? BeforeFramebufferCreation { get; set; }

    /// <summary>Runs after a newly created framebuffer handle has been stored in its image slot.</summary>
    internal Action<uint>? AfterFramebufferCreated { get; set; }

    /// <summary>Runs after the image framebuffer is complete, while the owning context is current.</summary>
    internal Action<GL, uint>? AfterFramebufferInitialized { get; set; }

    /// <summary>Runs immediately before the resize GPU-idle wait.</summary>
    internal Action? BeforeGpuFinish { get; set; }

    /// <summary>Runs immediately before an image framebuffer is deleted.</summary>
    internal Action<uint>? BeforeFramebufferDeletion { get; set; }

    /// <summary>Runs after an image framebuffer has been deleted successfully.</summary>
    internal Action<uint>? AfterFramebufferDeleted { get; set; }

    /// <summary>Runs after an image texture has been deleted successfully.</summary>
    internal Action<uint>? AfterTextureDeleted { get; set; }

    /// <summary>Observes presentation syncs created by the actual Present operation.</summary>
    internal Action<nint>? AfterPresentationSyncCreated { get; set; }

    /// <summary>Runs after a presentation sync is deleted during acquisition or retirement.</summary>
    internal Action<nint>? AfterPresentationSyncDeleted { get; set; }
}
