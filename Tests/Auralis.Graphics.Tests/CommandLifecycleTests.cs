using Auralis.Graphics.Commands;
using Auralis.Graphics.Abstractions;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class CommandLifecycleTests
{
    [Fact]
    public void ReusableCommandListMustCompleteBeforeReset()
    {
        var lifecycle = new GraphicsCommandListLifecycle(GraphicsQueueType.Graphics);

        Assert.Throws<InvalidOperationException>(lifecycle.End);
        lifecycle.Begin();
        lifecycle.EnsureGraphicsCommands();
        lifecycle.End();

        Assert.Equal(GraphicsCommandListState.Executable, lifecycle.State);
        lifecycle.MarkSubmitted();
        Assert.Throws<InvalidOperationException>(lifecycle.Reset);

        lifecycle.MarkCompleted();
        lifecycle.Reset();
        Assert.Equal(GraphicsCommandListState.Initial, lifecycle.State);
    }

    [Fact]
    public void QueueRoleRestrictsDrawAndDispatchContexts()
    {
        var copy = new GraphicsCommandListLifecycle(GraphicsQueueType.Copy);
        copy.Begin();

        Assert.Throws<InvalidOperationException>(copy.EnsureGraphicsCommands);
        Assert.Throws<InvalidOperationException>(copy.EnsureComputeCommands);
        copy.EnsureCopyCommands();
    }

    [Fact]
    public void OneTimeCommandListCannotBeSubmittedTwiceOrResetAfterSubmission()
    {
        var lifecycle = new GraphicsCommandListLifecycle(
            GraphicsQueueType.Compute,
            GraphicsCommandListUsage.OneTimeSubmit);

        lifecycle.Begin();
        lifecycle.EnsureComputeCommands();
        lifecycle.End();
        lifecycle.MarkSubmitted();
        lifecycle.MarkCompleted();

        Assert.Throws<InvalidOperationException>(lifecycle.MarkSubmitted);
        Assert.Throws<InvalidOperationException>(lifecycle.Reset);
    }

    [Fact]
    public void PendingCommandListCannotBeDisposed()
    {
        var lifecycle = new GraphicsCommandListLifecycle(GraphicsQueueType.Copy);
        lifecycle.Begin();
        lifecycle.End();
        lifecycle.MarkSubmitted();

        Assert.Throws<InvalidOperationException>(lifecycle.MarkDisposed);

        lifecycle.MarkCompleted();
        lifecycle.MarkDisposed();
        Assert.Equal(GraphicsCommandListState.Disposed, lifecycle.State);
    }
}
