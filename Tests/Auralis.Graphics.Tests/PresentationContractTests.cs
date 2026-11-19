using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class PresentationContractTests
{
    [Fact]
    public void NativeSurfaceSupportsMultipleTypedHandlesAndPerHandleOwnership()
    {
        GraphicsNativeHandle[] handles =
        [
            new(GraphicsNativeHandleKind.Win32Window, (nint)42, GraphicsNativeHandleOwnership.Borrowed),
            new(GraphicsNativeHandleKind.Win32Instance, (nint)84, GraphicsNativeHandleOwnership.OwnedByGraphics)
        ];

        var description = new GraphicsNativeSurfaceDescription(GraphicsNativePlatform.Windows, handles);
        handles[0] = default;

        Assert.Equal(2, description.Handles.Length);
        Assert.True(description.TryGetHandle(GraphicsNativeHandleKind.Win32Window, out var window));
        Assert.Equal((nint)42, window.Value);
        Assert.Equal(GraphicsNativeHandleOwnership.Borrowed, window.Ownership);
    }

    [Fact]
    public void NativeSurfaceRejectsDuplicateHandleKinds()
    {
        GraphicsNativeHandle[] handles =
        [
            new(GraphicsNativeHandleKind.X11Display, (nint)1, GraphicsNativeHandleOwnership.Borrowed),
            new(GraphicsNativeHandleKind.X11Display, (nint)2, GraphicsNativeHandleOwnership.Borrowed)
        ];

        Assert.Throws<ArgumentException>(
            () => new GraphicsNativeSurfaceDescription(GraphicsNativePlatform.X11, handles));
    }

    [Fact]
    public void CustomNativeHandleKindsMustUseReservedCustomRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphicsNativeHandleKind.CreateCustom(10));
        Assert.Equal(0x8000u, GraphicsNativeHandleKind.CreateCustom(0x8000).Value);
    }

    [Fact]
    public void SwapchainRejectsInvalidImageCountAndZeroExtent()
    {
        var invalid = new GraphicsSwapChainDescription(
            1920, 1080, GraphicsFormat.BGRA8UNorm, 1, GraphicsPresentMode.Fifo);

        Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate);

        var zeroExtent = invalid with { Width = 0, BufferCount = 2 };
        Assert.Throws<ArgumentOutOfRangeException>(zeroExtent.Validate);
    }
}
