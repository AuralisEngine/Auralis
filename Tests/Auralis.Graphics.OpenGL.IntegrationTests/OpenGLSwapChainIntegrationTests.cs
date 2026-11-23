using System.Diagnostics;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Xunit;

namespace Auralis.Graphics.OpenGL.IntegrationTests;

public sealed partial class OpenGLIntegrationTests
{
    [Fact]
    public void GlfwPresentationTargetUsesAValidCustomNativeHandleKind()
    {
        Assert.Equal(
            GlfwPresentationTarget.NativeWindowHandleKind,
            GraphicsNativeHandleKind.CreateCustom(GlfwPresentationTarget.NativeWindowHandleKind).Value);
    }

    [Fact]
    public void SwapChainAcquisitionPresentationAndResizeWorkOnTheHostDrawable() =>
        RunHardwareTest(ExerciseSwapChainLifecycle);

    private static void ExerciseSwapChainLifecycle(GlfwOpenGLHardwareSession session)
    {
        GlfwOpenGLContextProvider provider = session.ContextProvider;
        GlfwPresentationTarget target = provider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = session.Device.CreatePresentationSurface(target);
        IGraphicsQueue queue = session.Device.GetPresentationQueue();

        Assert.True(surface.SupportsPresentation(queue));
        RequireFormat(session.Device, GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.ColorAttachment);
        RequireFormat(session.Device, GraphicsFormat.RGBA8UNormSrgb, GraphicsFormatSupport.ColorAttachment);

        GraphicsPresentationTargetSnapshot initialTarget = target.GetSnapshot();
        GraphicsSwapChainDescription description = new(
            initialTarget.Extent.Width,
            initialTarget.Extent.Height,
            GraphicsFormat.RGBA8UNorm,
            2,
            GraphicsPresentMode.Immediate);

        using IGraphicsSwapChain swapChain = surface.CreateSwapChain(queue, description);
        Assert.Equal(1UL, swapChain.Generation);
        Assert.Equal(description, swapChain.Description);

        // Keep the device context gate on this thread while another thread attempts acquisition.
        // The timeout must include gate contention and return without ever entering a blocking GL wait.
        OpenGLGraphicsDevice nativeDevice = (OpenGLGraphicsDevice)session.Device;
        using (OpenGLContextScope heldContext = nativeDevice.AcquireContext())
        {
            Task<GraphicsAcquireResult> contendedAcquire = Task.Run(
                () => swapChain.AcquireNextImage(TimeSpan.FromMilliseconds(75)));
            Assert.Equal(GraphicsAcquireStatus.Timeout, contendedAcquire.GetAwaiter().GetResult().Status);
        }

        // Zero-timeout acquisition must still return images that have no presentation sync.
        GraphicsAcquireResult first = swapChain.AcquireNextImage(TimeSpan.Zero);
        Assert.Equal(GraphicsAcquireStatus.Success, first.Status);
        Assert.Equal(0u, first.Image.ImageIndex);
        Assert.Equal(1UL, first.Image.Generation);

        GraphicsAcquireResult second = swapChain.AcquireNextImage(TimeSpan.Zero);
        Assert.Equal(GraphicsAcquireStatus.Success, second.Status);
        Assert.Equal(1u, second.Image.ImageIndex);
        Assert.Equal(1UL, second.Image.Generation);

        Assert.Equal(
            GraphicsAcquireStatus.Timeout,
            swapChain.AcquireNextImage(TimeSpan.Zero).Status);

        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(first.Image));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(second.Image));

        // Hold every other image at the application so the following acquisition must wait for
        // the exact image whose real Present call publishes a sync and then sees a provider failure.
        GraphicsAcquireResult failedSwapImage = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsAcquireStatus.Success, failedSwapImage.Status);
        GraphicsAcquireResult applicationHeldImage = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsAcquireStatus.Success, applicationHeldImage.Status);

        provider.FailNextSwapBuffers = true;
        Assert.Throws<InvalidOperationException>(() => swapChain.Present(failedSwapImage.Image));

        GraphicsAcquireResult afterSwapFailure = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsAcquireStatus.Success, afterSwapFailure.Status);
        Assert.Equal(failedSwapImage.Image.ImageIndex, afterSwapFailure.Image.ImageIndex);
        Assert.Equal(failedSwapImage.Image.Generation, afterSwapFailure.Image.Generation);
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(afterSwapFailure.Image));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(applicationHeldImage.Image));

        // Keep one acquired handle alive across a real drawable resize. The successful resize must
        // invalidate it and return a new ring at the actual framebuffer extent reported by GLFW.
        GraphicsAcquireResult staleAfterResize = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsAcquireStatus.Success, staleAfterResize.Status);

        GraphicsPresentationTargetSnapshot resizedTarget =
            provider.ResizePresentationTarget(target, 96, 72);
        GraphicsSwapChainDescription resizedDescription = description with
        {
            Width = resizedTarget.Extent.Width,
            Height = resizedTarget.Extent.Height,
            BufferCount = 3,
            PresentMode = GraphicsPresentMode.Fifo
        };

        swapChain.Resize(resizedDescription);
        Assert.Equal(GraphicsPresentMode.Fifo, provider.CurrentPresentMode);
        Assert.Equal(2UL, swapChain.Generation);
        Assert.Equal(resizedDescription, swapChain.Description);
        Assert.Equal(GraphicsPresentStatus.OutOfDate, swapChain.Present(staleAfterResize.Image));

        GraphicsAcquireResult resizedImage = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsAcquireStatus.Success, resizedImage.Status);
        Assert.Equal(2UL, resizedImage.Image.Generation);
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(resizedImage.Image));

        // Exercise a supported format transition and repeated image-ring retirement.
        GraphicsSwapChainDescription formatDescription = resizedDescription with
        {
            Format = GraphicsFormat.RGBA8UNormSrgb,
            BufferCount = 2
        };
        swapChain.Resize(formatDescription);
        Assert.Equal(3UL, swapChain.Generation);

        for (int i = 0; i < 3; i++)
        {
            GraphicsAcquireResult image = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
            Assert.Equal(GraphicsAcquireStatus.Success, image.Status);
            Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(image.Image));

            GraphicsSwapChainDescription repeated = swapChain.Description with
            {
                BufferCount = i % 2 == 0 ? 3u : 2u
            };
            swapChain.Resize(repeated);
        }

        Assert.Equal(6UL, swapChain.Generation);
        Console.WriteLine(
            $"PASS: Desktop OpenGL swap-chain lifecycle, host drawable blit/swap, zero-timeout acquisition, " +
            $"SwapBuffers failure recovery, resize generations, format/buffer-count changes, and repeated retirement. " +
            $"Drawable={resizedTarget.Extent.Width}x{resizedTarget.Extent.Height}.");
    }

    [Fact]
    public void SwapChainResizeRollsBackTextureFramebufferAndGpuWaitFailures() =>
        RunHardwareTest(ExerciseResizePreparationFailures);

    private static void ExerciseResizePreparationFailures(GlfwOpenGLHardwareSession session)
    {
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        GlfwPresentationTarget target = session.ContextProvider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
        using IGraphicsSwapChain swapChain = surface.CreateSwapChain(
            device.GetPresentationQueue(),
            new GraphicsSwapChainDescription(
                target.GetSnapshot().Extent.Width,
                target.GetSnapshot().Extent.Height,
                GraphicsFormat.RGBA8UNorm,
                2,
                GraphicsPresentMode.Immediate));

        OpenGLSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        GraphicsSwapChainDescription original = swapChain.Description;
        GraphicsSwapChainDescription replacement = original with
        {
            BufferCount = 3,
            PresentMode = GraphicsPresentMode.Fifo
        };

        try
        {
            hooks.BeforeTextureCreation = () =>
            {
                hooks.BeforeTextureCreation = null;
                throw new InvalidOperationException("Injected replacement texture allocation failure.");
            };
            Assert.Throws<InvalidOperationException>(() => swapChain.Resize(replacement));
            Assert.Equal(1UL, swapChain.Generation);
            Assert.Equal(original, swapChain.Description);
            Assert.Equal(original.PresentMode, session.ContextProvider.CurrentPresentMode);
            AcquireAndPresent(swapChain);

            hooks.AfterFramebufferCreated = _ =>
            {
                hooks.AfterFramebufferCreated = null;
                throw new InvalidOperationException("Injected framebuffer initialization failure.");
            };
            Assert.Throws<InvalidOperationException>(() => swapChain.Resize(replacement));
            Assert.Equal(1UL, swapChain.Generation);
            Assert.Equal(original, swapChain.Description);
            Assert.Equal(original.PresentMode, session.ContextProvider.CurrentPresentMode);
            AcquireAndPresent(swapChain);

            hooks.BeforeGpuFinish = () =>
            {
                hooks.BeforeGpuFinish = null;
                throw new InvalidOperationException("Injected previous-work wait failure.");
            };
            Assert.Throws<AggregateException>(() => swapChain.Resize(replacement));
            Assert.Equal(1UL, swapChain.Generation);
            Assert.Equal(original, swapChain.Description);
            Assert.Equal(original.PresentMode, session.ContextProvider.CurrentPresentMode);
            AcquireAndPresent(swapChain);
        }
        finally
        {
            device.SwapChainTestHooks = null;
        }
    }

    [Fact]
    public void SwapChainPartialRetirementFailureFaultsTheOldRingAndAllowsCleanupRetry() =>
        RunHardwareTest(ExerciseResizeRetirementFailure);

    private static void ExerciseResizeRetirementFailure(GlfwOpenGLHardwareSession session)
    {
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        GlfwPresentationTarget target = session.ContextProvider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
        using IGraphicsSwapChain swapChain = surface.CreateSwapChain(
            device.GetPresentationQueue(),
            new GraphicsSwapChainDescription(
                target.GetSnapshot().Extent.Width,
                target.GetSnapshot().Extent.Height,
                GraphicsFormat.RGBA8UNorm,
                2,
                GraphicsPresentMode.Immediate));

        OpenGLSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        try
        {
            AcquireAndPresent(swapChain);
            hooks.BeforeFramebufferDeletion = _ =>
            {
                hooks.BeforeFramebufferDeletion = null;
                throw new InvalidOperationException("Injected framebuffer retirement failure.");
            };

            Assert.Throws<AggregateException>(() =>
                swapChain.Resize(swapChain.Description with
                {
                    BufferCount = 3,
                    PresentMode = GraphicsPresentMode.Fifo
                }));
            Assert.Equal(GraphicsPresentMode.Immediate, session.ContextProvider.CurrentPresentMode);

            Assert.Throws<GraphicsException>(() => swapChain.AcquireNextImage(TimeSpan.Zero));
            Assert.Throws<GraphicsException>(() => _ = swapChain.Description);
        }
        finally
        {
            // The callback is one-shot; disposing the faulted swap chain retries the native
            // framebuffer deletion and then drops the texture owner references.
            device.SwapChainTestHooks = null;
        }
    }

    private static void AcquireAndPresent(IGraphicsSwapChain swapChain)
    {
        GraphicsAcquireResult acquired = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsAcquireStatus.Success, acquired.Status);
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(acquired.Image));
    }


    [Fact]
    public void AcquisitionScansLaterSignaledImagesBeforeWaitingOnEarlierPendingImages() =>
        RunHardwareTest(ExerciseAcquisitionScanOrder);

    private static void ExerciseAcquisitionScanOrder(GlfwOpenGLHardwareSession session)
    {
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        GlfwPresentationTarget target = session.ContextProvider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
        using IGraphicsSwapChain swapChain = surface.CreateSwapChain(
            device.GetPresentationQueue(),
            new GraphicsSwapChainDescription(
                target.GetSnapshot().Extent.Width,
                target.GetSnapshot().Extent.Height,
                GraphicsFormat.RGBA8UNorm,
                2,
                GraphicsPresentMode.Immediate));

        GraphicsAcquireResult first = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        GraphicsAcquireResult second = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsAcquireStatus.Success, first.Status);
        Assert.Equal(GraphicsAcquireStatus.Success, second.Status);
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(first.Image));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(second.Image));

        using (OpenGLContextScope context = device.AcquireContext())
            context.Gl.Finish();

        OpenGLSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        int polls = 0;
        int waits = 0;
        int deletions = 0;
        hooks.PollClientWaitSync = _ =>
            ++polls == 1 ? ARB.TimeoutExpired : ARB.ConditionSatisfied;
        hooks.WaitClientWaitSync = (_, _, _) =>
        {
            waits++;
            return ARB.TimeoutExpired;
        };
        hooks.AfterAcquireSyncDeleted = _ => deletions++;

        try
        {
            GraphicsAcquireResult later = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
            Assert.Equal(GraphicsAcquireStatus.Success, later.Status);
            Assert.Equal(1u, later.Image.ImageIndex);
            Assert.Equal(2, polls);
            Assert.Equal(0, waits);
            Assert.Equal(1, deletions);

            // Round-robin resumes at image zero. Its real sync completed during Finish, and this
            // zero-timeout poll retires it without a blocking wait.
            hooks.PollClientWaitSync = _ => ARB.ConditionSatisfied;
            GraphicsAcquireResult earlier = swapChain.AcquireNextImage(TimeSpan.Zero);
            Assert.Equal(GraphicsAcquireStatus.Success, earlier.Status);
            Assert.Equal(0u, earlier.Image.ImageIndex);
            Assert.Equal(2, deletions);
        }
        finally
        {
            device.SwapChainTestHooks = null;
        }
    }

    [Fact]
    public void AcquisitionUsesOneShrinkingDeadlineAcrossRepeatedNativeWaits() =>
        RunHardwareTest(ExerciseFiniteAcquireDeadline);

    private static void ExerciseFiniteAcquireDeadline(GlfwOpenGLHardwareSession session)
    {
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        GlfwPresentationTarget target = session.ContextProvider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
        using IGraphicsSwapChain swapChain = surface.CreateSwapChain(
            device.GetPresentationQueue(),
            new GraphicsSwapChainDescription(
                target.GetSnapshot().Extent.Width,
                target.GetSnapshot().Extent.Height,
                GraphicsFormat.RGBA8UNorm,
                2,
                GraphicsPresentMode.Immediate));

        GraphicsAcquireResult first = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        GraphicsAcquireResult second = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(first.Image));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(second.Image));
        using (OpenGLContextScope context = device.AcquireContext())
            context.Gl.Finish();

        OpenGLSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        long fakeTimestamp = 0;
        List<ulong> waitBudgets = new(2);
        int deletions = 0;
        hooks.TimestampProvider = () => fakeTimestamp;
        hooks.PollClientWaitSync = _ => ARB.TimeoutExpired;
        hooks.WaitClientWaitSync = (_, flags, timeoutNanoseconds) =>
        {
            Assert.Equal((SyncObjectMask)1, flags);
            waitBudgets.Add(timeoutNanoseconds);
            fakeTimestamp += Stopwatch.Frequency * 3 / 5;
            return ARB.TimeoutExpired;
        };
        hooks.AfterAcquireSyncDeleted = _ => deletions++;

        try
        {
            GraphicsAcquireResult timedOut = swapChain.AcquireNextImage(TimeSpan.FromSeconds(1));
            Assert.Equal(GraphicsAcquireStatus.Timeout, timedOut.Status);
            Assert.Equal(2, waitBudgets.Count);
            Assert.True(waitBudgets[1] < waitBudgets[0]);
            Assert.True(waitBudgets[1] > 0);
            Assert.Equal(0, deletions);

            // The fake first slot now reports the real completed dependency. A fresh zero-timeout
            // acquisition may retire it and take image zero; the timed-out call did not acquire it.
            hooks.PollClientWaitSync = _ => ARB.ConditionSatisfied;
            GraphicsAcquireResult immediate = swapChain.AcquireNextImage(TimeSpan.Zero);
            Assert.Equal(GraphicsAcquireStatus.Success, immediate.Status);
            Assert.Equal(0u, immediate.Image.ImageIndex);
            Assert.Equal(1, deletions);
        }
        finally
        {
            device.SwapChainTestHooks = null;
        }
    }

    [Fact]
    public void InfiniteAcquisitionUsesFiniteNativeWaitSlices() =>
        RunHardwareTest(ExerciseInfiniteAcquire);

    private static void ExerciseInfiniteAcquire(GlfwOpenGLHardwareSession session)
    {
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        GlfwPresentationTarget target = session.ContextProvider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
        using IGraphicsSwapChain swapChain = surface.CreateSwapChain(
            device.GetPresentationQueue(),
            new GraphicsSwapChainDescription(
                target.GetSnapshot().Extent.Width,
                target.GetSnapshot().Extent.Height,
                GraphicsFormat.RGBA8UNorm,
                2,
                GraphicsPresentMode.Immediate));

        GraphicsAcquireResult first = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        GraphicsAcquireResult second = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(first.Image));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(second.Image));
        using (OpenGLContextScope context = device.AcquireContext())
            context.Gl.Finish();

        OpenGLSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        hooks.PollClientWaitSync = _ => ARB.TimeoutExpired;
        List<ulong> waitBudgets = new(2);
        int waits = 0;
        hooks.WaitClientWaitSync = (_, flags, timeoutNanoseconds) =>
        {
            Assert.Equal((SyncObjectMask)1, flags);
            waitBudgets.Add(timeoutNanoseconds);
            return ++waits == 1 ? ARB.TimeoutExpired : ARB.ConditionSatisfied;
        };

        try
        {
            GraphicsAcquireResult acquired = swapChain.AcquireNextImage(Timeout.InfiniteTimeSpan);
            Assert.Equal(GraphicsAcquireStatus.Success, acquired.Status);
            Assert.Equal(0u, acquired.Image.ImageIndex);
            Assert.Equal(2, waitBudgets.Count);
            Assert.All(waitBudgets, budget =>
            {
                Assert.Equal(GraphicsAcquireDeadline.InfiniteClientWaitSliceNanoseconds, budget);
                Assert.NotEqual(ulong.MaxValue, budget);
            });
        }
        finally
        {
            device.SwapChainTestHooks = null;
        }
    }


    [Fact]
    public void CompletedPresentationWaitAfterDeadlineDoesNotPublishAcquisition() =>
        RunHardwareTest(ExerciseLateCompletedWait);

    private static void ExerciseLateCompletedWait(GlfwOpenGLHardwareSession session)
    {
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        GlfwPresentationTarget target = session.ContextProvider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
        using IGraphicsSwapChain swapChain = surface.CreateSwapChain(
            device.GetPresentationQueue(),
            new GraphicsSwapChainDescription(
                target.GetSnapshot().Extent.Width,
                target.GetSnapshot().Extent.Height,
                GraphicsFormat.RGBA8UNorm,
                2,
                GraphicsPresentMode.Immediate));

        GraphicsAcquireResult first = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        GraphicsAcquireResult second = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(first.Image));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(second.Image));
        using (OpenGLContextScope context = device.AcquireContext())
            context.Gl.Finish();

        OpenGLSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        long fakeTimestamp = 0;
        int deletions = 0;
        hooks.TimestampProvider = () => fakeTimestamp;
        hooks.PollClientWaitSync = _ => ARB.TimeoutExpired;
        hooks.WaitClientWaitSync = (_, _, _) =>
        {
            fakeTimestamp += Stopwatch.Frequency * 2;
            return ARB.ConditionSatisfied;
        };
        hooks.AfterAcquireSyncDeleted = _ => deletions++;

        try
        {
            GraphicsAcquireResult timedOut = swapChain.AcquireNextImage(TimeSpan.FromSeconds(1));
            Assert.Equal(GraphicsAcquireStatus.Timeout, timedOut.Status);
            Assert.Equal(1, deletions);

            // The completed sync was retired but the operation did not acquire the image. A new
            // zero-timeout call can now acquire that same first round-robin slot immediately.
            GraphicsAcquireResult immediate = swapChain.AcquireNextImage(TimeSpan.Zero);
            Assert.Equal(GraphicsAcquireStatus.Success, immediate.Status);
            Assert.Equal(0u, immediate.Image.ImageIndex);
            Assert.Equal(1, deletions);
        }
        finally
        {
            device.SwapChainTestHooks = null;
        }
    }


    [Fact]
    public void RepeatedSwapChainCyclesReleaseTrackedNativeTexturesFramebuffersAndSyncs() =>
        RunHardwareTest(ExerciseTrackedResourceRetirement);

    private static void ExerciseTrackedResourceRetirement(GlfwOpenGLHardwareSession session)
    {
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        HashSet<uint> activeTextures = [];
        HashSet<uint> activeFramebuffers = [];
        HashSet<nint> activeSyncs = [];
        HashSet<uint> allTextures = [];
        HashSet<uint> allFramebuffers = [];
        HashSet<nint> allSyncs = [];

        OpenGLSwapChainTestHooks hooks = new()
        {
            AfterTextureCreated = texture =>
            {
                uint handle = texture.Handle;
                Assert.True(activeTextures.Add(handle), $"Texture {handle} was registered twice.");
                allTextures.Add(handle);
            },
            AfterTextureDeleted = handle =>
                Assert.True(activeTextures.Remove(handle), $"Texture {handle} was deleted without ownership."),
            AfterFramebufferCreated = handle =>
            {
                Assert.True(activeFramebuffers.Add(handle), $"Framebuffer {handle} was registered twice.");
                allFramebuffers.Add(handle);
            },
            AfterFramebufferDeleted = handle =>
                Assert.True(activeFramebuffers.Remove(handle), $"Framebuffer {handle} was deleted without ownership."),
            AfterPresentationSyncCreated = sync =>
            {
                Assert.True(activeSyncs.Add(sync), $"Sync {sync} was registered twice.");
                allSyncs.Add(sync);
            },
            AfterPresentationSyncDeleted = sync =>
                Assert.True(activeSyncs.Remove(sync), $"Sync {sync} was deleted without ownership.")
        };

        device.SwapChainTestHooks = hooks;
        try
        {
            GlfwPresentationTarget target = session.ContextProvider.CreatePresentationTarget();
            using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
            GraphicsSwapChainDescription description = new(
                target.GetSnapshot().Extent.Width,
                target.GetSnapshot().Extent.Height,
                GraphicsFormat.RGBA8UNorm,
                2,
                GraphicsPresentMode.Immediate);

            IGraphicsSwapChain swapChain = surface.CreateSwapChain(device.GetPresentationQueue(), description);
            try
            {
                for (int cycle = 0; cycle < 4; cycle++)
                {
                    GraphicsAcquireResult first = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
                    GraphicsAcquireResult second = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
                    Assert.Equal(GraphicsAcquireStatus.Success, first.Status);
                    Assert.Equal(GraphicsAcquireStatus.Success, second.Status);
                    Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(first.Image));
                    Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(second.Image));
                    Assert.Equal(2, activeSyncs.Count);

                    uint nextCount = cycle % 2 == 0 ? 3u : 2u;
                    swapChain.Resize(swapChain.Description with { BufferCount = nextCount });

                    Assert.Empty(activeSyncs);
                    Assert.Equal((int)nextCount, activeTextures.Count);
                    Assert.Equal((int)nextCount, activeFramebuffers.Count);

                    using OpenGLContextScope context = device.AcquireContext();
                    context.Gl.Finish();
                    foreach (uint handle in allTextures)
                        Assert.True(activeTextures.Contains(handle) || !context.Gl.IsTexture(handle),
                            $"Retired texture {handle} is still a valid GL texture.");
                    foreach (uint handle in allFramebuffers)
                        Assert.True(activeFramebuffers.Contains(handle) || !context.Gl.IsFramebuffer(handle),
                            $"Retired framebuffer {handle} is still a valid GL framebuffer.");
                    foreach (nint sync in allSyncs)
                        Assert.True(activeSyncs.Contains(sync) || !_deviceSyncIsValid(device, sync),
                            $"Retired sync {sync} is still a valid GL sync.");
                }
            }
            finally
            {
                swapChain.Dispose();
            }

            using (OpenGLContextScope context = device.AcquireContext())
            {
                context.Gl.Finish();
                Assert.Empty(activeTextures);
                Assert.Empty(activeFramebuffers);
                Assert.Empty(activeSyncs);
                foreach (uint handle in allTextures)
                    Assert.False(context.Gl.IsTexture(handle), $"Texture {handle} survived swap-chain disposal.");
                foreach (uint handle in allFramebuffers)
                    Assert.False(context.Gl.IsFramebuffer(handle), $"Framebuffer {handle} survived swap-chain disposal.");
                foreach (nint sync in allSyncs)
                    Assert.False(device.SyncApi.IsSync(sync), $"Sync {sync} survived swap-chain disposal.");
            }

            Console.WriteLine(
                $"PASS: Desktop GL native swap-chain resource accounting. " +
                $"Textures={allTextures.Count}, framebuffers={allFramebuffers.Count}, syncs={allSyncs.Count} created and all released.");
        }
        finally
        {
            device.SwapChainTestHooks = null;
        }
    }

    private static bool _deviceSyncIsValid(OpenGLGraphicsDevice device, nint sync) =>
        device.SyncApi.IsSync(sync);


    [Fact]
    public void PresentBlitsTheOffscreenImageIntoTheDefaultDrawable() =>
        RunHardwareTest(ExercisePresentationReadback);

    private static unsafe void ExercisePresentationReadback(GlfwOpenGLHardwareSession session)
    {
        OpenGLGraphicsDevice device = (OpenGLGraphicsDevice)session.Device;
        GlfwOpenGLContextProvider provider = session.ContextProvider;
        GlfwPresentationTarget target = provider.CreatePresentationTarget();
        OpenGLSwapChainTestHooks hooks = new()
        {
            AfterFramebufferInitialized = (gl, framebuffer) =>
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                gl.ClearColor(1f, 0f, 0f, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            }
        };

        device.SwapChainTestHooks = hooks;
        int readbacks = 0;
        provider.BeforeSwapBuffers = () =>
        {
            using OpenGLContextScope context = device.AcquireContext();
            GL gl = context.Gl;
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            gl.ReadBuffer(gl.GetInteger(GLEnum.Doublebuffer) != 0
                ? ReadBufferMode.Back
                : ReadBufferMode.Front);

            byte* pixel = stackalloc byte[4];
            gl.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
            Assert.Equal((byte)255, pixel[0]);
            Assert.Equal((byte)0, pixel[1]);
            Assert.Equal((byte)0, pixel[2]);
            Assert.Equal((byte)255, pixel[3]);
            readbacks++;
        };

        try
        {
            using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
            using IGraphicsSwapChain swapChain = surface.CreateSwapChain(
                device.GetPresentationQueue(),
                new GraphicsSwapChainDescription(
                    target.GetSnapshot().Extent.Width,
                    target.GetSnapshot().Extent.Height,
                    GraphicsFormat.RGBA8UNorm,
                    2,
                    GraphicsPresentMode.Immediate));

            GraphicsAcquireResult acquired = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
            Assert.Equal(GraphicsAcquireStatus.Success, acquired.Status);
            Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(acquired.Image));
            Assert.Equal(1, readbacks);
        }
        finally
        {
            provider.BeforeSwapBuffers = null;
            device.SwapChainTestHooks = null;
        }
    }

}
