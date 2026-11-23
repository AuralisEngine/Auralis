using Auralis.Graphics.Backends;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class GraphicsAcquireDeadlineTests
{
    [Fact]
    public void FiniteTimeoutUsesOneShrinkingMonotonicBudget()
    {
        GraphicsAcquireDeadline deadline = new(TimeSpan.FromSeconds(1), 1_000, 1_000);

        Assert.Equal(TimeSpan.FromMilliseconds(750), deadline.GetRemainingTimeAt(1_250));
        Assert.Equal(750_000_000UL, deadline.GetClientWaitTimeoutNanosecondsAt(1_250));
        Assert.Equal(TimeSpan.FromMilliseconds(250), deadline.GetRemainingTimeAt(1_750));
        Assert.Equal(250_000_000UL, deadline.GetClientWaitTimeoutNanosecondsAt(1_750));
        Assert.True(deadline.IsExpiredAt(2_000));
        Assert.Equal(TimeSpan.Zero, deadline.GetRemainingTimeAt(2_000));
        Assert.Equal(0UL, deadline.GetClientWaitTimeoutNanosecondsAt(2_000));
    }

    [Fact]
    public void ZeroTimeoutHasNoWaitBudgetButCanStillBePolled()
    {
        GraphicsAcquireDeadline deadline = new(TimeSpan.Zero, 50, 1_000_000);

        Assert.True(deadline.IsExpiredAt(50));
        Assert.Equal(TimeSpan.Zero, deadline.GetRemainingTimeAt(50));
        Assert.Equal(0UL, deadline.GetClientWaitTimeoutNanosecondsAt(50));
    }

    [Fact]
    public void PositiveSubTickContextBudgetRoundsUpInsteadOfPrematurelyBecomingZero()
    {
        const long frequency = 10_000_000;
        GraphicsAcquireDeadline deadline = new(TimeSpan.FromSeconds(1), 100, frequency);

        Assert.Equal(TimeSpan.FromTicks(1), deadline.GetRemainingTimeAt(100 + frequency - 1));
        Assert.Equal(100UL, deadline.GetClientWaitTimeoutNanosecondsAt(100 + frequency - 1));
    }

    [Fact]
    public void InfiniteWaitUsesFiniteNativeSlices()
    {
        GraphicsAcquireDeadline deadline = new(Timeout.InfiniteTimeSpan, 100, 1_000_000);

        Assert.True(deadline.IsInfinite);
        Assert.False(deadline.IsExpiredAt(long.MaxValue - 1));
        Assert.Equal(Timeout.InfiniteTimeSpan, deadline.GetRemainingTimeAt(100));
        Assert.Equal(GraphicsAcquireDeadline.InfiniteClientWaitSliceNanoseconds,
            deadline.GetClientWaitTimeoutNanosecondsAt(100));
        Assert.NotEqual(ulong.MaxValue, deadline.GetClientWaitTimeoutNanosecondsAt(100));
    }

    [Fact]
    public void HugeFiniteWaitDoesNotBecomeInfiniteSentinel()
    {
        GraphicsAcquireDeadline deadline = new(TimeSpan.MaxValue, 0, 1);

        Assert.False(deadline.IsInfinite);
        Assert.Equal(ulong.MaxValue - 1, deadline.GetClientWaitTimeoutNanosecondsAt(1));
        Assert.NotEqual(ulong.MaxValue, deadline.GetClientWaitTimeoutNanosecondsAt(1));
    }

    [Fact]
    public void NegativeTimeoutExceptInfiniteIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsAcquireDeadline(TimeSpan.FromTicks(-1), 0, 1));
    }
}
