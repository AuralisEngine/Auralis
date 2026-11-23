using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.OpenGL.IntegrationTests;

public sealed partial class OpenGLIntegrationTests
{
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
                swapChain.Resize(swapChain.Description with { BufferCount = 3 }));

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

}
