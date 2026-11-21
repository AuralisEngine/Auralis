using Auralis.Graphics.Backends.OpenGL;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Silk.NET.OpenGL;
using Xunit;

namespace Auralis.Graphics.Tests;

/// <summary>
/// Deterministic tests for the same native-reference state machine used by OpenGL device objects
/// and by command-list recording. These tests intentionally do not require a GL context or timing.
/// </summary>
public sealed class OpenGLResourceRetentionTests
{
    [Fact]
    public void DisposedBufferRemainsAliveUntilRecordedReferencesAreReleased()
    {
        var retention = new OpenGLResourceRetention();
        var buffer = new InstrumentedNativeResource();

        retention.Retain(buffer);
        buffer.DisposeOwner();

        Assert.Equal(0, buffer.NativeReleaseCount);
        retention.ReleaseAll();
        Assert.Equal(1, buffer.NativeReleaseCount);
    }

    [Fact]
    public void DisposedTextureRemainsAliveUntilRecordedReferencesAreReleased()
    {
        var retention = new OpenGLResourceRetention();
        var texture = new InstrumentedNativeResource();

        retention.Retain(texture);
        texture.DisposeOwner();

        Assert.Equal(0, texture.NativeReleaseCount);
        retention.ReleaseAll();
        Assert.Equal(1, texture.NativeReleaseCount);
    }

    [Fact]
    public void ResettingCommandListReleasesItsRecordedNativeReferences()
    {
        var retention = new OpenGLResourceRetention();
        var resource = new InstrumentedNativeResource();

        retention.Retain(resource);
        resource.DisposeOwner();
        Assert.Equal(0, resource.NativeReleaseCount);

        // Reset releases the complete recording's retained resource set.
        retention.ReleaseAll();

        Assert.Equal(1, resource.NativeReleaseCount);
        Assert.Equal(0, retention.Count);
    }

    [Fact]
    public void DisposingUnsubmittedCommandListReleasesItsRecordedNativeReferences()
    {
        var retention = new OpenGLResourceRetention();
        var resource = new InstrumentedNativeResource();

        retention.Retain(resource);
        resource.DisposeOwner();
        Assert.Equal(0, resource.NativeReleaseCount);

        // Dispose of an executable list uses the same cleanup path as resetting it.
        retention.ReleaseAll();

        Assert.Equal(1, resource.NativeReleaseCount);
    }

    [Fact]
    public void SubmittedCommandListKeepsDisposedResourcesUntilCompletionAndReset()
    {
        var lifecycle = new GraphicsCommandListLifecycle(GraphicsQueueType.Graphics);
        var retention = new OpenGLResourceRetention();
        var resource = new InstrumentedNativeResource("buffer");

        lifecycle.Begin();
        retention.Retain(resource);
        lifecycle.End();
        lifecycle.MarkSubmitted();
        resource.DisposeOwner();

        Assert.Equal(GraphicsCommandListState.Pending, lifecycle.State);
        Assert.Equal(0, resource.NativeReleaseCount);
        Assert.Throws<InvalidOperationException>(lifecycle.Reset);

        lifecycle.MarkCompleted();
        lifecycle.Reset();
        retention.ReleaseAll();

        Assert.Equal(1, resource.NativeReleaseCount);
    }

    [Fact]
    public void MultipleCommandListsRetainSharedResourceIndependently()
    {
        var firstList = new OpenGLResourceRetention();
        var secondList = new OpenGLResourceRetention();
        var resource = new InstrumentedNativeResource();

        firstList.Retain(resource);
        secondList.Retain(resource);
        resource.DisposeOwner();

        firstList.ReleaseAll();
        Assert.Equal(0, resource.NativeReleaseCount);

        secondList.ReleaseAll();
        Assert.Equal(1, resource.NativeReleaseCount);
    }

    [Fact]
    public void FailedSubmissionCanCompleteAndReleaseResourcesOnSubsequentReset()
    {
        var lifecycle = new GraphicsCommandListLifecycle(GraphicsQueueType.Compute);
        var retention = new OpenGLResourceRetention();
        var resource = new InstrumentedNativeResource();

        lifecycle.Begin();
        retention.Retain(resource);
        lifecycle.End();
        lifecycle.MarkSubmitted();
        resource.DisposeOwner();

        // The queue's failed-execution path completes the list after finishing any issued GL work.
        lifecycle.MarkCompleted();
        Assert.Equal(0, resource.NativeReleaseCount);

        lifecycle.Reset();
        retention.ReleaseAll();
        Assert.Equal(1, resource.NativeReleaseCount);
    }

    [Fact]
    public void DeviceDisposalMayForceNativeReleaseBeforeManagedRetentionsDrain()
    {
        var retention = new OpenGLResourceRetention();
        var resource = new InstrumentedNativeResource();

        retention.Retain(resource);
        resource.DisposeOwner();

        // Device disposal releases all registered GL objects only after finishing the context.
        resource.ReleaseNative(null!);
        Assert.Equal(1, resource.NativeReleaseCount);

        // Later command-list cleanup must not issue a second native delete.
        retention.ReleaseAll();
        Assert.Equal(1, resource.NativeReleaseCount);
    }

    [Fact]
    public void RepeatedDisposalAndDuplicateReleaseCannotDeleteNativeResourceTwice()
    {
        var retention = new OpenGLResourceRetention();
        var resource = new InstrumentedNativeResource();

        retention.Retain(resource);
        retention.Retain(resource); // A list holds one reference per distinct object.
        resource.DisposeOwner();
        resource.DisposeOwner();

        Assert.Equal(0, resource.NativeReleaseCount);
        Assert.Equal(1, retention.Count);

        retention.ReleaseAll();
        Assert.Equal(1, resource.NativeReleaseCount);
        Assert.Throws<InvalidOperationException>(resource.ReleaseReference);
    }

    private sealed class InstrumentedNativeResource : IOpenGLNativeResource
    {
        private OpenGLNativeResourceReferenceState _references = new(1);
        private int _ownerDisposed;
        private int _nativeReleased;

        internal int NativeReleaseCount => Volatile.Read(ref _nativeReleased);
        public bool TryRetain() => _references.TryRetain();

        public void ReleaseReference()
        {
            if (_references.ReleaseReference())
                ReleaseNative(null!);
        }

        internal void DisposeOwner()
        {
            if (Interlocked.Exchange(ref _ownerDisposed, 1) == 0 &&
                _references.ReleaseOwnerReference())
                ReleaseNative(null!);
        }

        public void ReleaseNative(GL gl) => Interlocked.Exchange(ref _nativeReleased, 1);
    }
}
