using Auralis.Graphics.Backends.OpenGL;
using Silk.NET.OpenGL;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class OpenGLNativeResourcePreparationTests
{
    [Fact]
    public void PreparationAttemptsEveryResourceAndCanSucceedOnRetry()
    {
        var first = new TestResource(1);
        var second = new TestResource(1);
        var ready = new TestResource(0);
        IOpenGLNativeResource[] resources = [first, second, ready];

        List<Exception>? firstAttempt = OpenGLNativeResourcePreparation.PrepareAll(resources, resources.Length);
        Assert.NotNull(firstAttempt);
        Assert.Equal(2, firstAttempt.Count);
        Assert.Equal(1, first.PreparationAttempts);
        Assert.Equal(1, second.PreparationAttempts);
        Assert.Equal(1, ready.PreparationAttempts);

        List<Exception>? retry = OpenGLNativeResourcePreparation.PrepareAll(resources, resources.Length);
        Assert.Null(retry);
        Assert.Equal(2, first.PreparationAttempts);
        Assert.Equal(2, second.PreparationAttempts);
        Assert.Equal(2, ready.PreparationAttempts);
    }

    [Fact]
    public void PreparationRejectsInvalidSnapshotCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenGLNativeResourcePreparation.PrepareAll(null, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenGLNativeResourcePreparation.PrepareAll([], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenGLNativeResourcePreparation.PrepareAll([], -1));
    }

    private sealed class TestResource(int failuresBeforeSuccess)
        : IOpenGLNativeResource, IOpenGLNativeResourcePreparation
    {
        private int _remainingFailures = failuresBeforeSuccess;
        public int PreparationAttempts { get; private set; }
        public bool TryRetain() => true;
        public void ReleaseReference() { }
        public void ReleaseNative(GL gl) { }

        public void PrepareForNativeRelease()
        {
            PreparationAttempts++;
            if (_remainingFailures <= 0)
                return;
            _remainingFailures--;
            throw new InvalidOperationException($"Preparation failure {PreparationAttempts}.");
        }
    }
}
