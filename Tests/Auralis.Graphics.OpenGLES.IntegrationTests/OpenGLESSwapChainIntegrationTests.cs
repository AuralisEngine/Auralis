using System.Diagnostics;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGLES;
using Xunit;

namespace Auralis.Graphics.OpenGLES.IntegrationTests;

public sealed partial class OpenGLESIntegrationTests
{
    [Fact]
    public void SwapChainAcquisitionPresentationAndResizeWorkOnTheHostDrawable() =>
        RunHardwareTest(ExerciseSwapChainLifecycle);

    private static void ExerciseSwapChainLifecycle(
        IGraphicsDevice device,
        GlfwOpenGLESContextProvider provider)
    {
        GlfwPresentationTarget target = provider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
        IGraphicsQueue queue = device.GetPresentationQueue();

        Assert.True(surface.SupportsPresentation(queue));
        RequireColorFormat(device, GraphicsFormat.RGBA8UNorm);
        RequireColorFormat(device, GraphicsFormat.RGBA8UNormSrgb);

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

        OpenGLESGraphicsDevice nativeDevice = (OpenGLESGraphicsDevice)device;
        using (OpenGLESContextScope heldContext = nativeDevice.AcquireContext())
        {
            Task<GraphicsAcquireResult> contendedAcquire = Task.Run(
                () => swapChain.AcquireNextImage(TimeSpan.FromMilliseconds(75)));
            Assert.Equal(GraphicsAcquireStatus.Timeout, contendedAcquire.GetAwaiter().GetResult().Status);
        }

        GraphicsAcquireResult first = swapChain.AcquireNextImage(TimeSpan.Zero);
        Assert.Equal(GraphicsAcquireStatus.Success, first.Status);
        Assert.Equal(0u, first.Image.ImageIndex);
        Assert.Equal(1UL, first.Image.Generation);

        GraphicsAcquireResult second = swapChain.AcquireNextImage(TimeSpan.Zero);
        Assert.Equal(GraphicsAcquireStatus.Success, second.Status);
        Assert.Equal(1u, second.Image.ImageIndex);
        Assert.Equal(GraphicsAcquireStatus.Timeout, swapChain.AcquireNextImage(TimeSpan.Zero).Status);

        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(first.Image));
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(second.Image));

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

        GraphicsAcquireResult staleAfterResize = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsAcquireStatus.Success, staleAfterResize.Status);

        GraphicsPresentationTargetSnapshot resizedTarget =
            provider.ResizePresentationTarget(target, 96, 72);
        GraphicsSwapChainDescription resizedDescription = description with
        {
            Width = resizedTarget.Extent.Width,
            Height = resizedTarget.Extent.Height,
            BufferCount = 3
        };

        swapChain.Resize(resizedDescription);
        Assert.Equal(2UL, swapChain.Generation);
        Assert.Equal(resizedDescription, swapChain.Description);
        Assert.Equal(GraphicsPresentStatus.OutOfDate, swapChain.Present(staleAfterResize.Image));

        GraphicsAcquireResult resizedImage = swapChain.AcquireNextImage(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphicsAcquireStatus.Success, resizedImage.Status);
        Assert.Equal(2UL, resizedImage.Image.Generation);
        Assert.Equal(GraphicsPresentStatus.Presented, swapChain.Present(resizedImage.Image));

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
            $"PASS: OpenGL ES swap-chain lifecycle, host drawable blit/swap, zero-timeout acquisition, " +
            $"SwapBuffers failure recovery, resize generations, format/buffer-count changes, and repeated retirement. " +
            $"Drawable={resizedTarget.Extent.Width}x{resizedTarget.Extent.Height}.");
    }

    private static void RequireColorFormat(IGraphicsDevice device, GraphicsFormat format)
    {
        if (!device.Capabilities.SupportsFormat(format, GraphicsFormatSupport.ColorAttachment))
            Skip($"The active GLES driver does not expose {format} as a color attachment.");
    }

    [Fact]
    public void SwapChainResizeRollsBackTextureFramebufferAndGpuWaitFailures() =>
        RunHardwareTest(ExerciseResizePreparationFailures);

    private static void ExerciseResizePreparationFailures(
        IGraphicsDevice graphicsDevice,
        GlfwOpenGLESContextProvider provider)
    {
        OpenGLESGraphicsDevice device = (OpenGLESGraphicsDevice)graphicsDevice;
        GlfwPresentationTarget target = provider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
        using IGraphicsSwapChain swapChain = surface.CreateSwapChain(
            device.GetPresentationQueue(),
            new GraphicsSwapChainDescription(
                target.GetSnapshot().Extent.Width,
                target.GetSnapshot().Extent.Height,
                GraphicsFormat.RGBA8UNorm,
                2,
                GraphicsPresentMode.Immediate));

        OpenGLESSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        GraphicsSwapChainDescription original = swapChain.Description;
        GraphicsSwapChainDescription replacement = original with { BufferCount = 3 };

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
            AcquireAndPresent(swapChain);

            hooks.AfterFramebufferCreated = _ =>
            {
                hooks.AfterFramebufferCreated = null;
                throw new InvalidOperationException("Injected framebuffer initialization failure.");
            };
            Assert.Throws<InvalidOperationException>(() => swapChain.Resize(replacement));
            Assert.Equal(1UL, swapChain.Generation);
            Assert.Equal(original, swapChain.Description);
            AcquireAndPresent(swapChain);

            hooks.BeforeGpuFinish = () =>
            {
                hooks.BeforeGpuFinish = null;
                throw new InvalidOperationException("Injected previous-work wait failure.");
            };
            Assert.Throws<AggregateException>(() => swapChain.Resize(replacement));
            Assert.Equal(1UL, swapChain.Generation);
            Assert.Equal(original, swapChain.Description);
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

    private static void ExerciseResizeRetirementFailure(
        IGraphicsDevice graphicsDevice,
        GlfwOpenGLESContextProvider provider)
    {
        OpenGLESGraphicsDevice device = (OpenGLESGraphicsDevice)graphicsDevice;
        GlfwPresentationTarget target = provider.CreatePresentationTarget();
        using IGraphicsPresentationSurface surface = device.CreatePresentationSurface(target);
        using IGraphicsSwapChain swapChain = surface.CreateSwapChain(
            device.GetPresentationQueue(),
            new GraphicsSwapChainDescription(
                target.GetSnapshot().Extent.Width,
                target.GetSnapshot().Extent.Height,
                GraphicsFormat.RGBA8UNorm,
                2,
                GraphicsPresentMode.Immediate));

        OpenGLESSwapChainTestHooks hooks = new();
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
                swapChain.Resize(swapChain.Description with { BufferCount = 3 }));

            Assert.Throws<GraphicsException>(() => swapChain.AcquireNextImage(TimeSpan.Zero));
            Assert.Throws<GraphicsException>(() => _ = swapChain.Description);
        }
        finally
        {
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

    private static void ExerciseAcquisitionScanOrder(
        IGraphicsDevice graphicsDevice,
        GlfwOpenGLESContextProvider provider)
    {
        OpenGLESGraphicsDevice device = (OpenGLESGraphicsDevice)graphicsDevice;
        GlfwPresentationTarget target = provider.CreatePresentationTarget();
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

        using (OpenGLESContextScope context = device.AcquireContext())
            context.Gl.Finish();

        OpenGLESSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        int polls = 0;
        int waits = 0;
        int deletions = 0;
        hooks.PollClientWaitSync = _ =>
            ++polls == 1 ? GLEnum.TimeoutExpired : GLEnum.ConditionSatisfied;
        hooks.WaitClientWaitSync = (_, _, _) =>
        {
            waits++;
            return GLEnum.TimeoutExpired;
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

            hooks.PollClientWaitSync = _ => GLEnum.ConditionSatisfied;
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

    private static void ExerciseFiniteAcquireDeadline(
        IGraphicsDevice graphicsDevice,
        GlfwOpenGLESContextProvider provider)
    {
        OpenGLESGraphicsDevice device = (OpenGLESGraphicsDevice)graphicsDevice;
        GlfwPresentationTarget target = provider.CreatePresentationTarget();
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
        using (OpenGLESContextScope context = device.AcquireContext())
            context.Gl.Finish();

        OpenGLESSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        long fakeTimestamp = 0;
        List<ulong> waitBudgets = new(2);
        int deletions = 0;
        hooks.TimestampProvider = () => fakeTimestamp;
        hooks.PollClientWaitSync = _ => GLEnum.TimeoutExpired;
        hooks.WaitClientWaitSync = (_, flags, timeoutNanoseconds) =>
        {
            Assert.Equal((uint)1, flags);
            waitBudgets.Add(timeoutNanoseconds);
            fakeTimestamp += Stopwatch.Frequency * 3 / 5;
            return GLEnum.TimeoutExpired;
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

            hooks.PollClientWaitSync = _ => GLEnum.ConditionSatisfied;
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

    private static void ExerciseInfiniteAcquire(
        IGraphicsDevice graphicsDevice,
        GlfwOpenGLESContextProvider provider)
    {
        OpenGLESGraphicsDevice device = (OpenGLESGraphicsDevice)graphicsDevice;
        GlfwPresentationTarget target = provider.CreatePresentationTarget();
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
        using (OpenGLESContextScope context = device.AcquireContext())
            context.Gl.Finish();

        OpenGLESSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        hooks.PollClientWaitSync = _ => GLEnum.TimeoutExpired;
        List<ulong> waitBudgets = new(2);
        int waits = 0;
        hooks.WaitClientWaitSync = (_, flags, timeoutNanoseconds) =>
        {
            Assert.Equal((uint)1, flags);
            waitBudgets.Add(timeoutNanoseconds);
            return ++waits == 1 ? GLEnum.TimeoutExpired : GLEnum.ConditionSatisfied;
        };

        try
        {
            GraphicsAcquireResult acquired = swapChain.AcquireNextImage(Timeout.InfiniteTimeSpan);
            Assert.Equal(GraphicsAcquireStatus.Success, acquired.Status);
            Assert.Equal(0u, acquired.Image.ImageIndex);
            Assert.Equal(2, waitBudgets.Count);
            Assert.All(waitBudgets, budget =>
            {
                Assert.Equal(OpenGLESAcquireDeadline.InfiniteClientWaitSliceNanoseconds, budget);
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

    private static void ExerciseLateCompletedWait(
        IGraphicsDevice graphicsDevice,
        GlfwOpenGLESContextProvider provider)
    {
        OpenGLESGraphicsDevice device = (OpenGLESGraphicsDevice)graphicsDevice;
        GlfwPresentationTarget target = provider.CreatePresentationTarget();
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
        using (OpenGLESContextScope context = device.AcquireContext())
            context.Gl.Finish();

        OpenGLESSwapChainTestHooks hooks = new();
        device.SwapChainTestHooks = hooks;
        long fakeTimestamp = 0;
        int deletions = 0;
        hooks.TimestampProvider = () => fakeTimestamp;
        hooks.PollClientWaitSync = _ => GLEnum.TimeoutExpired;
        hooks.WaitClientWaitSync = (_, _, _) =>
        {
            fakeTimestamp += Stopwatch.Frequency * 2;
            return GLEnum.ConditionSatisfied;
        };
        hooks.AfterAcquireSyncDeleted = _ => deletions++;

        try
        {
            GraphicsAcquireResult timedOut = swapChain.AcquireNextImage(TimeSpan.FromSeconds(1));
            Assert.Equal(GraphicsAcquireStatus.Timeout, timedOut.Status);
            Assert.Equal(1, deletions);

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

}
