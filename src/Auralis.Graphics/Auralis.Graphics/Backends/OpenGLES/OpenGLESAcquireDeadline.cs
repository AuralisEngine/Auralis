using Auralis.Graphics.Backends;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Compatibility facade for the GLES presentation path. The underlying deadline is shared with
/// desktop OpenGL so both implementations use identical monotonic timeout arithmetic.
/// </summary>
internal readonly struct OpenGLESAcquireDeadline
{
    internal const ulong InfiniteClientWaitSliceNanoseconds =
        GraphicsAcquireDeadline.InfiniteClientWaitSliceNanoseconds;

    private readonly GraphicsAcquireDeadline _deadline;

    internal OpenGLESAcquireDeadline(TimeSpan timeout) =>
        _deadline = new GraphicsAcquireDeadline(timeout);

    internal OpenGLESAcquireDeadline(TimeSpan timeout, long startTimestamp, long frequency) =>
        _deadline = new GraphicsAcquireDeadline(timeout, startTimestamp, frequency);

    internal bool IsInfinite => _deadline.IsInfinite;
    internal bool IsExpired => _deadline.IsExpired;
    internal bool IsExpiredAt(long timestamp) => _deadline.IsExpiredAt(timestamp);
    internal TimeSpan GetRemainingTime() => _deadline.GetRemainingTime();
    internal TimeSpan GetRemainingTimeAt(long timestamp) => _deadline.GetRemainingTimeAt(timestamp);
    internal ulong GetClientWaitTimeoutNanoseconds() => _deadline.GetClientWaitTimeoutNanoseconds();
    internal ulong GetClientWaitTimeoutNanosecondsAt(long timestamp) =>
        _deadline.GetClientWaitTimeoutNanosecondsAt(timestamp);
}
