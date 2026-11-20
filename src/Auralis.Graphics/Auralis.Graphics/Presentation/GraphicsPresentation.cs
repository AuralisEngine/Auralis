using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;

namespace Auralis.Graphics.Presentation;

public enum GraphicsNativePlatform
{
    Windows,
    Android,
    X11,
    Wayland,
    Apple,
    Custom
}

/// <summary>Extensible semantic identifier for one native object required by a presentation target.</summary>
public readonly record struct GraphicsNativeHandleKind(uint Value)
{
    public static GraphicsNativeHandleKind Win32Window { get; } = new(1);
    public static GraphicsNativeHandleKind Win32Instance { get; } = new(2);
    public static GraphicsNativeHandleKind AndroidNativeWindow { get; } = new(3);
    public static GraphicsNativeHandleKind X11Display { get; } = new(4);
    public static GraphicsNativeHandleKind X11Window { get; } = new(5);
    public static GraphicsNativeHandleKind WaylandDisplay { get; } = new(6);
    public static GraphicsNativeHandleKind WaylandSurface { get; } = new(7);
    public static GraphicsNativeHandleKind MetalLayer { get; } = new(8);

    /// <summary>Creates an application-defined kind. Values below this range are reserved by Auralis.</summary>
    public static GraphicsNativeHandleKind CreateCustom(uint value)
    {
        if (value < 0x8000)
            throw new ArgumentOutOfRangeException(nameof(value), "Custom handle kinds must use values >= 0x8000.");
        return new GraphicsNativeHandleKind(value);
    }
}

public enum GraphicsNativeHandleOwnership
{
    Borrowed,
    OwnedByGraphics
}

/// <summary>
/// One opaque native object. A target may provide multiple handles (for example, display plus surface).
/// The common graphics API never interprets Value or imports the corresponding OS type.
/// </summary>
public readonly record struct GraphicsNativeHandle(
    GraphicsNativeHandleKind Kind,
    nint Value,
    GraphicsNativeHandleOwnership Ownership);

/// <summary>Immutable native handle set supplied by a platform integration or an external application.</summary>
public sealed class GraphicsNativeSurfaceDescription
{
    private readonly GraphicsNativeHandle[] _handles;

    public GraphicsNativePlatform Platform { get; }

    public ReadOnlySpan<GraphicsNativeHandle> Handles => _handles;

    public GraphicsNativeSurfaceDescription(
        GraphicsNativePlatform platform,
        ReadOnlySpan<GraphicsNativeHandle> handles)
    {
        Platform = platform;
        _handles = handles.ToArray();
        Validate();
    }

    public bool TryGetHandle(GraphicsNativeHandleKind kind, out GraphicsNativeHandle handle)
    {
        for (int i = 0; i < _handles.Length; i++)
        {
            if (_handles[i].Kind == kind)
            {
                handle = _handles[i];
                return true;
            }
        }

        handle = default;
        return false;
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Platform))
            throw new ArgumentOutOfRangeException(nameof(Platform));
        if (_handles.Length == 0)
            throw new ArgumentException("A native presentation target must provide at least one handle.", nameof(Handles));

        for (int i = 0; i < _handles.Length; i++)
        {
            GraphicsNativeHandle handle = _handles[i];
            if (handle.Kind.Value == 0)
                throw new ArgumentException("Native handle kinds must be non-zero.", nameof(Handles));
            if (!Enum.IsDefined(handle.Ownership))
                throw new ArgumentOutOfRangeException(nameof(Handles), "Native handle ownership is invalid.");

            for (int j = 0; j < i; j++)
            {
                if (_handles[j].Kind == handle.Kind)
                    throw new ArgumentException($"Native handle kind {handle.Kind.Value} is duplicated.", nameof(Handles));
            }
        }
    }
}

/// <summary>Unsigned width/height pair used by presentation targets and swapchains.</summary>
public readonly record struct GraphicsExtent2D(uint Width, uint Height);

/// <summary>
/// Snapshot of a target's native objects and current drawable size. Revision must increase whenever
/// the native surface identity changes; resizing changes the extent. A suspended target may have a zero extent.
/// </summary>
public readonly record struct GraphicsPresentationTargetSnapshot(
    GraphicsNativeSurfaceDescription NativeSurface,
    GraphicsExtent2D Extent,
    ulong Revision,
    bool IsSuspended)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(NativeSurface);
        NativeSurface.Validate();

        if (!IsSuspended && (Extent.Width == 0 || Extent.Height == 0))
            throw new ArgumentException("A non-suspended presentation target must have a non-zero extent.");
    }
}

/// <summary>
/// Implemented by a platform integration or host application. The graphics surface borrows this target;
/// the caller must keep the target and all borrowed native objects alive until the surface is disposed.
/// </summary>
public interface IGraphicsPresentationTarget
{
    GraphicsPresentationTargetSnapshot GetSnapshot();
}

public enum GraphicsPresentMode
{
    Fifo,
    FifoRelaxed,
    Immediate,
    Mailbox
}

/// <summary>Presentation image configuration. Width and height must be non-zero.</summary>
public readonly record struct GraphicsSwapChainDescription(
    uint Width,
    uint Height,
    GraphicsFormat Format,
    uint BufferCount,
    GraphicsPresentMode PresentMode)
{
    public void Validate()
    {
        if (Width == 0 || Height == 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Swapchain dimensions must be non-zero.");
        if (Format == GraphicsFormat.Unknown || !Enum.IsDefined(Format))
            throw new ArgumentOutOfRangeException(nameof(Format));
        if (BufferCount < 2)
            throw new ArgumentOutOfRangeException(nameof(BufferCount), "A swapchain requires at least two images.");
        if (!Enum.IsDefined(PresentMode))
            throw new ArgumentOutOfRangeException(nameof(PresentMode));
    }
}

public enum GraphicsAcquireStatus
{
    Success,
    Suboptimal,
    Timeout,
    OutOfDate,
    Suspended
}

public enum GraphicsPresentStatus
{
    Presented,
    Suboptimal,
    OutOfDate,
    Suspended
}

/// <summary>An image leased from a swapchain. It is invalidated by presenting or resizing the swapchain.</summary>
public readonly record struct GraphicsAcquiredImage(
    uint ImageIndex,
    ulong Generation,
    IGraphicsTexture Texture);

public readonly record struct GraphicsAcquireResult(
    GraphicsAcquireStatus Status,
    GraphicsAcquiredImage Image)
{
    public bool HasImage => Status is GraphicsAcquireStatus.Success or GraphicsAcquireStatus.Suboptimal;
}

/// <summary>
/// Backend-created surface associated with a borrowed presentation target. Disposing this surface must
/// not destroy borrowed windows/layers or dispose the target.
/// </summary>
public interface IGraphicsPresentationSurface : IGraphicsDeviceObject
{
    IGraphicsPresentationTarget Target { get; }

    GraphicsPresentationTargetSnapshot Snapshot { get; }

    /// <summary>
    /// Returns whether this exact queue can present to this surface. Queue capability is surface-specific;
    /// a device-wide Present flag or a graphics queue role is not sufficient evidence. Returns false for queues
    /// owned by another device.
    /// </summary>
    bool SupportsPresentation(IGraphicsQueue queue);

    /// <summary>Creates a swapchain using an explicitly selected, surface-compatible presentation queue.</summary>
    IGraphicsSwapChain CreateSwapChain(
        IGraphicsQueue presentationQueue,
        in GraphicsSwapChainDescription description);
}

/// <summary>Presentation queue for images acquired from a surface.</summary>
public interface IGraphicsSwapChain : IGraphicsDeviceObject
{
    GraphicsSwapChainDescription Description { get; }

    /// <summary>The exact queue selected for presentation to the owning surface.</summary>
    IGraphicsQueue PresentationQueue { get; }

    ulong Generation { get; }

    GraphicsAcquireResult AcquireNextImage(TimeSpan timeout);

    GraphicsPresentStatus Present(
        in GraphicsAcquiredImage image,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0);

    /// <summary>Recreates the image set. All previously acquired images become invalid.</summary>
    void Resize(in GraphicsSwapChainDescription description);
}
