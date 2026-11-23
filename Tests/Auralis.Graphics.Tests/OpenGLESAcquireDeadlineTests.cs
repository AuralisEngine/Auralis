using Auralis.Graphics.Backends.OpenGLES;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class OpenGLESAcquireDeadlineTests
{
    [Fact]
    public void ZeroTimeoutExpiresAtStart()
    {
        OpenGLESAcquireDeadline deadline = new(TimeSpan.Zero, 100, 1_000_000);
        Assert.True(deadline.IsExpiredAt(100));
        Assert.Equal(0UL, deadline.GetClientWaitTimeoutNanosecondsAt(100));
        Assert.Equal(TimeSpan.Zero, deadline.GetRemainingTimeAt(100));
    }

    [Fact]
    public void FiniteTimeoutUsesOneMonotonicDeadline()
    {
        OpenGLESAcquireDeadline deadline = new(TimeSpan.FromSeconds(1), 1_000, 1_000_000);
        Assert.False(deadline.IsExpiredAt(500_000));
        Assert.Equal(TimeSpan.FromMilliseconds(501), deadline.GetRemainingTimeAt(500_000));
        Assert.Equal(501_000_000UL, deadline.GetClientWaitTimeoutNanosecondsAt(500_000));
        Assert.True(deadline.IsExpiredAt(1_001_000));
        Assert.Equal(0UL, deadline.GetClientWaitTimeoutNanosecondsAt(1_001_000));
    }

    [Fact]
    public void InfiniteWaitUsesSupportedFiniteNativeIntervals()
    {
        OpenGLESAcquireDeadline deadline = new(Timeout.InfiniteTimeSpan, 100, 1_000_000);
        Assert.True(deadline.IsInfinite);
        Assert.False(deadline.IsExpiredAt(long.MaxValue - 1));
        Assert.Equal(Timeout.InfiniteTimeSpan, deadline.GetRemainingTimeAt(100));
        Assert.Equal(OpenGLESAcquireDeadline.InfiniteClientWaitSliceNanoseconds,
            deadline.GetClientWaitTimeoutNanosecondsAt(100));
        Assert.NotEqual(ulong.MaxValue, deadline.GetClientWaitTimeoutNanosecondsAt(100));
    }

    [Fact]
    public void VeryLargeFiniteDeadlineDoesNotBecomeInfinite()
    {
        OpenGLESAcquireDeadline deadline = new(TimeSpan.MaxValue, 10, long.MaxValue);
        Assert.False(deadline.IsInfinite);
        Assert.False(deadline.IsExpiredAt(11));
        Assert.True(deadline.GetClientWaitTimeoutNanosecondsAt(11) > 0);
    }

    [Fact]
    public void PresentationSyncIsOwnedAndImageReleasedBeforeHostSwap()
    {
        OpenGLESSwapChainImage slot = new() { Texture = null!, Acquired = true };
        OpenGLESGraphicsSwapChain.PublishPresentationSync(slot, (nint)123);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            (Action)(() => throw new InvalidOperationException("Simulated SwapBuffers failure.")));

        Assert.Equal("Simulated SwapBuffers failure.", failure.Message);
        Assert.False(slot.Acquired);
        Assert.Equal((nint)123, slot.PresentSync);
    }

    [Fact]
    public void PresentationSyncPublicationRejectsDuplicateOwnershipAndNullSync()
    {
        OpenGLESSwapChainImage slot = new()
        {
            Texture = null!,
            Acquired = true,
            PresentSync = (nint)456
        };
        Assert.Throws<InvalidOperationException>(() =>
            OpenGLESGraphicsSwapChain.PublishPresentationSync(slot, (nint)123));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OpenGLESGraphicsSwapChain.PublishPresentationSync(slot, 0));
        Assert.Equal((nint)456, slot.PresentSync);
        Assert.True(slot.Acquired);
    }
}
