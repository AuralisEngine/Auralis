using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Devices;
using Auralis.Graphics.OpenGL.TestSupport;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
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
}
