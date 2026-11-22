using Auralis.Graphics.Backends.OpenGLES;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Abstractions;
using Xunit;

namespace Auralis.Graphics.Tests;

/// <summary>
/// Exercises the GLES parent/view native-reference state machine without creating a GL context.
/// The same state is used by OpenGLESNativeDeviceObject for owner, view-dependency and command refs.
/// </summary>
public sealed class OpenGLESResourceReferenceStateTests
{
    [Fact]
    public void MultipleViewsKeepDisposedParentStorageUntilTheLastViewIsReleased()
    {
        var references = new OpenGLESResourceReferenceState();
        int nativeDeleteCount = 0;

        Assert.True(references.TryRetain()); // First live texture view.
        Assert.True(references.TryRetain()); // Second live texture view.
        Assert.Equal(3, references.ReferenceCount);

        Assert.False(references.CloseOwner());
        Assert.True(references.IsClosed);
        Assert.Equal(2, references.ReferenceCount);
        Assert.False(references.TryRetain()); // No new view may be created from a disposed owner.

        if (references.ReleaseReference())
            nativeDeleteCount++;
        Assert.Equal(0, nativeDeleteCount);
        Assert.Equal(1, references.ReferenceCount);

        if (references.ReleaseReference())
            nativeDeleteCount++;

        Assert.Equal(1, nativeDeleteCount);
        Assert.Equal(0, references.ReferenceCount);
        Assert.False(references.CloseOwner()); // Repeated owner disposal is harmless.
        Assert.Throws<InvalidOperationException>(() => references.ReleaseReference());
    }

    [Fact]
    public void SubmittedCommandRetainsViewAndParentStorageUntilCompletionAndReset()
    {
        var parentReferences = new OpenGLESResourceReferenceState();
        var viewReferences = new OpenGLESResourceReferenceState();
        var lifecycle = new GraphicsCommandListLifecycle(GraphicsQueueType.Graphics);
        int viewDeleteCount = 0;
        int parentDeleteCount = 0;

        Assert.True(parentReferences.TryRetain()); // The view's dependency on parent storage.

        lifecycle.Begin();
        Assert.True(viewReferences.TryRetain()); // The executable command list retains the view.
        lifecycle.End();
        lifecycle.MarkSubmitted();

        Assert.False(parentReferences.CloseOwner()); // Original texture owner is disposed.
        Assert.False(viewReferences.CloseOwner()); // View owner is disposed after recording.
        Assert.Equal(1, parentReferences.ReferenceCount);
        Assert.Equal(1, viewReferences.ReferenceCount);
        Assert.Throws<InvalidOperationException>(lifecycle.Reset);

        lifecycle.MarkCompleted();
        lifecycle.Reset();

        if (viewReferences.ReleaseReference())
        {
            viewDeleteCount++;
            if (parentReferences.ReleaseReference())
                parentDeleteCount++;
        }

        Assert.Equal(1, viewDeleteCount);
        Assert.Equal(1, parentDeleteCount);
        Assert.Equal(0, parentReferences.ReferenceCount);
        Assert.Equal(0, viewReferences.ReferenceCount);
    }
}
