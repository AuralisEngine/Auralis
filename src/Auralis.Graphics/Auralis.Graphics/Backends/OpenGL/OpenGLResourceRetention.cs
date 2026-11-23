using System.Runtime.ExceptionServices;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Owns one native reference per distinct resource used by a recording. The collection is
/// recording-thread-owned; resource reference transitions themselves are atomic.
/// </summary>
internal sealed class OpenGLResourceRetention
{
    private readonly List<IOpenGLNativeResource> _resources = new(16);
    private readonly HashSet<IOpenGLNativeResource> _uniqueResources =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets the number of distinct native resources retained by this recording.</summary>
    internal int Count => _resources.Count;

    /// <summary>Retains a resource once per recording, even when multiple commands reference it.</summary>
    /// <param name="resource">Native GL object referenced by a recorded command.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resource" /> is <see langword="null" />.</exception>
    /// <exception cref="ObjectDisposedException">The resource owner has closed and can no longer be retained.</exception>
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!_uniqueResources.Add(resource))
            return;

        if (!resource.TryRetain())
        {
            _uniqueResources.Remove(resource);
            throw new ObjectDisposedException(resource.GetType().Name);
        }

        try
        {
            _resources.Add(resource);
        }
        catch
        {
            _uniqueResources.Remove(resource);
            resource.ReleaseReference();
            throw;
        }
    }

    /// <summary>Releases every retained resource in reverse acquisition order and continues after individual release failures.</summary>
    /// <exception cref="AggregateException">One or more resource references failed to release; all other retained references are still attempted.</exception>
    internal void ReleaseAll()
    {
        List<Exception>? failures = null;
        while (_resources.Count != 0)
        {
            int index = _resources.Count - 1;
            IOpenGLNativeResource resource = _resources[index];
            _resources.RemoveAt(index);
            _uniqueResources.Remove(resource);

            try
            {
                resource.ReleaseReference();
            }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>(2)).Add(exception);
            }
        }

        if (failures is not null)
            throw new AggregateException(
                "One or more retained OpenGL resources failed to release during command-list cleanup.",
                failures);
    }
}
