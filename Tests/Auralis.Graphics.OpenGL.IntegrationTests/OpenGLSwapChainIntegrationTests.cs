using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
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
}
