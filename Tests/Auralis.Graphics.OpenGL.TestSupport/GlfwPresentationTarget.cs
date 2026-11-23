using Auralis.Graphics.Presentation;

namespace Auralis.Graphics.OpenGL.TestSupport;

/// <summary>
/// A presentation target identifying the exact GLFW window owned by a hardware-test context.
/// The handle is borrowed; disposing this adapter never destroys the window.
/// </summary>
public sealed class GlfwPresentationTarget : IGraphicsPresentationTarget
{
    internal const uint NativeWindowHandleKind = 0xA11;

    private readonly object _gate = new();
    private readonly GraphicsNativeSurfaceDescription _nativeSurface;
    private GraphicsPresentationTargetSnapshot _snapshot;

    internal GlfwPresentationTarget(nint window, uint width, uint height)
    {
        WindowHandle = window;
        GraphicsNativeHandle[] handles =
        [
            new(
                GraphicsNativeHandleKind.CreateCustom(NativeWindowHandleKind),
                window,
                GraphicsNativeHandleOwnership.Borrowed)
        ];
        _nativeSurface = new GraphicsNativeSurfaceDescription(GraphicsNativePlatform.Custom, handles);
        _snapshot = new GraphicsPresentationTargetSnapshot(
            _nativeSurface,
            new GraphicsExtent2D(width, height),
            1,
            IsSuspended: false);
        _snapshot.Validate();
    }

    internal nint WindowHandle { get; }

    /// <summary>Gets the current target extent and monotonically increasing revision.</summary>
    public GraphicsPresentationTargetSnapshot GetSnapshot()
    {
        lock (_gate)
            return _snapshot;
    }

    internal void UpdateExtent(uint width, uint height)
    {
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "The GLFW drawable extent must be non-zero.");

        lock (_gate)
        {
            _snapshot = new GraphicsPresentationTargetSnapshot(
                _nativeSurface,
                new GraphicsExtent2D(width, height),
                checked(_snapshot.Revision + 1),
                IsSuspended: false);
        }
    }
}
