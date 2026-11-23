using System.Diagnostics;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>Presentation surface tied to a host-supplied GLES drawable; it never owns the native target.</summary>
internal sealed class OpenGLESGraphicsPresentationSurface : GraphicsDeviceObject, IGraphicsPresentationSurface
{
    private readonly OpenGLESGraphicsDevice _device;
    private readonly Lock _gate = new();
    private GraphicsPresentationTargetSnapshot _snapshot;

    internal OpenGLESGraphicsPresentationSurface(
        OpenGLESGraphicsDevice device,
        IGraphicsPresentationTarget target,
        in GraphicsPresentationTargetSnapshot snapshot) : base(device)
    {
        _device = device;
        Target = target;
        snapshot.Validate();
        _snapshot = snapshot;
    }

    public IGraphicsPresentationTarget Target { get; }

    public GraphicsPresentationTargetSnapshot Snapshot
    {
        get
        {
            ThrowIfDisposed();
            lock (_gate)
                return _snapshot;
        }
    }

    internal GraphicsPresentationTargetSnapshot RefreshSnapshot()
    {
        ThrowIfDisposed();
        GraphicsPresentationTargetSnapshot current = Target.GetSnapshot();
        current.Validate();
        if (!_device.ContextProvider.IsPresentationTargetCompatible(current))
            throw new GraphicsValidationException("The host target no longer matches the injected OpenGL ES drawable.");

        lock (_gate)
        {
            if (current.Revision < _snapshot.Revision)
                throw new GraphicsValidationException("Presentation target revisions must increase monotonically.");
            _snapshot = current;
            return current;
        }
    }

    /// <summary>Checks whether the device graphics queue can present to the current host GLES drawable.</summary>
    /// <param name="queue">Candidate queue to validate.</param>
    /// <returns><see langword="true" /> only for this device's graphics queue and a compatible provider drawable.</returns>
    public bool SupportsPresentation(IGraphicsQueue queue)
    {
        if (IsDisposed || queue is not OpenGLESGraphicsCommandQueue glesQueue)
            return false;
        if (!ReferenceEquals(queue.Device, _device) || glesQueue.QueueType != GraphicsQueueType.Graphics)
            return false;
        if (!_device.ContextProvider.SupportsPresentation)
            return false;

        try
        {
            GraphicsPresentationTargetSnapshot current = Target.GetSnapshot();
            current.Validate();
            return _device.ContextProvider.IsPresentationTargetCompatible(current);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creates a swap chain of backend-owned color textures for the host GLES drawable.</summary>
    /// <param name="presentationQueue">Graphics queue associated with this surface and device.</param>
    /// <param name="description">Image count, exact drawable extent, format, and present mode.</param>
    /// <returns>A swap chain that blits an acquired image to framebuffer zero and delegates buffer swapping to the host provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="presentationQueue" /> is <see langword="null" />.</exception>
    /// <exception cref="GraphicsValidationException">The queue is incompatible, the target is suspended, or the requested extent differs from the current drawable.</exception>
    /// <exception cref="NotSupportedException">The host provider cannot honor the requested presentation mode.</exception>
        IGraphicsQueue presentationQueue,
        in GraphicsSwapChainDescription description)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(presentationQueue);
        GraphicsSwapChainDescription copy = description;
        copy.Validate();
        if (!SupportsPresentation(presentationQueue))
            throw new GraphicsValidationException("The selected graphics queue cannot present to this GLES drawable.");
        if (!_device.ContextProvider.SupportsPresentMode(copy.PresentMode))
            throw new NotSupportedException($"The host drawable does not support present mode {copy.PresentMode}.");

        GraphicsPresentationTargetSnapshot snapshot = RefreshSnapshot();
        if (snapshot.IsSuspended)
            throw new GraphicsValidationException("A swapchain cannot be created for a suspended drawable.");
        if (copy.Width != snapshot.Extent.Width || copy.Height != snapshot.Extent.Height)
            throw new GraphicsValidationException("The swapchain extent must match the current host drawable extent.");

        _device.ContextProvider.SetPresentMode(copy.PresentMode);
        return new OpenGLESGraphicsSwapChain(this, (OpenGLESGraphicsCommandQueue)presentationQueue, copy, snapshot);
    }

    /// <summary>Disposes only the surface adapter; its host drawable and context provider remain externally owned.</summary>
    protected override void DisposeCore() { }
}

/// <summary>Tracks one GLES swap-chain color texture, framebuffer, presentation sync, and acquisition state.</summary>
internal sealed class OpenGLESSwapChainImage
{
    internal required OpenGLESGraphicsTexture Texture;
    internal uint Framebuffer;
    internal nint PresentSync;
    internal bool Acquired;
}

/// <summary>
/// Swapchain implemented with backend-owned 2D color textures. Present blits to framebuffer zero and asks
/// the supplied host provider to swap its drawable. The GLES backend creates no platform context or surface.
/// </summary>
internal sealed class OpenGLESGraphicsSwapChain : OpenGLESNativeDeviceObject, IGraphicsSwapChain
{
    private readonly OpenGLESGraphicsPresentationSurface _surface;
    private readonly OpenGLESGraphicsCommandQueue _queue;
    private readonly OpenGLESGraphicsDevice _device;
    private OpenGLESSwapChainImage[] _images;
    private GraphicsSwapChainDescription _description;
    private ulong _generation = 1;
    private ulong _surfaceRevision;
    private uint _nextImage;

    internal OpenGLESGraphicsSwapChain(
        OpenGLESGraphicsPresentationSurface surface,
        OpenGLESGraphicsCommandQueue queue,
        in GraphicsSwapChainDescription description,
        in GraphicsPresentationTargetSnapshot snapshot) : base((OpenGLESGraphicsDevice)surface.Device)
    {
        _surface = surface;
        _queue = queue;
        _device = (OpenGLESGraphicsDevice)surface.Device;
        _description = description;
        _surfaceRevision = snapshot.Revision;
        _images = CreateImages(description);
        using OpenGLESContextScope context = _device.AcquireContext();
        _device.RegisterUnderContext(this);
    }

    public GraphicsSwapChainDescription Description
    {
        get { ThrowIfDisposed(); return _description; }
    }

    public IGraphicsQueue PresentationQueue => _queue;

    public ulong Generation
    {
        get { ThrowIfDisposed(); return Volatile.Read(ref _generation); }
    }

    /// <summary>Acquires the next reusable GLES offscreen image, polling its previous presentation sync as needed.</summary>
    /// <param name="timeout">Maximum CPU wait for an image to become reusable, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <returns>An acquired image, or a timeout, suspended, or out-of-date status without an image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout" /> is negative and is not infinite.</exception>
    /// <exception cref="GraphicsValidationException">The current host snapshot no longer matches the injected GLES drawable.</exception>
    /// <exception cref="GraphicsException">GLES reports a failed client wait.</exception>
    {
        ThrowIfDisposed();
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        GraphicsPresentationTargetSnapshot snapshot = _surface.RefreshSnapshot();
        if (snapshot.IsSuspended)
            return new GraphicsAcquireResult(GraphicsAcquireStatus.Suspended, default);
        if (snapshot.Revision != _surfaceRevision ||
            snapshot.Extent.Width != _description.Width || snapshot.Extent.Height != _description.Height)
            return new GraphicsAcquireResult(GraphicsAcquireStatus.OutOfDate, default);

        long start = Stopwatch.GetTimestamp();
        long timeoutTicks = timeout == Timeout.InfiniteTimeSpan
            ? long.MaxValue
            : (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            (int index, bool anyUnacquired) result;
            using (OpenGLESContextScope context = _device.AcquireContext())
            {
                GL gl = context.Gl;
                bool foundIndex = false;
                int acquiredIndex = -1;
                bool hasUnacquired = false;
                for (int attempt = 0; attempt < _images.Length; attempt++)
                {
                    uint index = (_nextImage + (uint)attempt) % (uint)_images.Length;
                    OpenGLESSwapChainImage image = _images[index];
                    if (image.Acquired)
                        continue;

                    hasUnacquired = true;
                    if (image.PresentSync != 0)
                    {
                        GLEnum status = gl.ClientWaitSync(image.PresentSync, (uint)0, 0);
                        if (status == GLEnum.TimeoutExpired)
                            continue;
                        if (status == GLEnum.WaitFailed)
                            throw new GraphicsException("OpenGL ES failed while waiting to reacquire a presented image.");
                        gl.DeleteSync(image.PresentSync);
                        image.PresentSync = 0;
                    }

                    image.Acquired = true;
                    _nextImage = (index + 1) % (uint)_images.Length;
                    acquiredIndex = (int)index;
                    foundIndex = true;
                    break;
                }

                result = (foundIndex ? acquiredIndex : -1, hasUnacquired);
            }

            if (result.index >= 0)
            {
                int index = result.index;
                return new GraphicsAcquireResult(
                    GraphicsAcquireStatus.Success,
                    new GraphicsAcquiredImage((uint)index, _generation, _images[index].Texture));
            }

            if (!result.anyUnacquired || timeoutTicks == 0 ||
                (timeoutTicks != long.MaxValue && Stopwatch.GetTimestamp() - start >= timeoutTicks))
                return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

            Thread.Yield();
        }
    }

    /// <summary>Blits an acquired image to framebuffer zero, inserts a GLES sync, flushes, and asks the host provider to swap buffers.</summary>
    /// <param name="image">Image previously acquired from this swap chain and not yet presented.</param>
    /// <param name="waitFence">Optional same-device timeline fence whose submitted commands must precede the blit.</param>
    /// <param name="waitValue">Timeline value to wait for when <paramref name="waitFence" /> is supplied.</param>
    /// <returns><see cref="GraphicsPresentStatus.Presented" />, or suspended/out-of-date when the drawable changed.</returns>
    /// <exception cref="ArgumentException">A non-zero wait value is supplied without a fence.</exception>
    /// <exception cref="GraphicsValidationException">The acquired image is stale/foreign or the wait fence belongs to another device.</exception>
    /// <exception cref="GraphicsException">GLES fails to create the presentation sync.</exception>
        in GraphicsAcquiredImage image,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0)
    {
        ThrowIfDisposed();
        if (waitFence is null && waitValue != 0)
            throw new ArgumentException("A non-zero wait value requires a wait fence.", nameof(waitValue));

        if (image.Generation != _generation || image.ImageIndex >= (uint)_images.Length)
            return GraphicsPresentStatus.OutOfDate;

        OpenGLESSwapChainImage slot = _images[image.ImageIndex];
        if (!slot.Acquired || !ReferenceEquals(slot.Texture, image.Texture))
            throw new GraphicsValidationException("The acquired GLES image is stale or belongs to another swapchain.");

        GraphicsPresentationTargetSnapshot snapshot = _surface.RefreshSnapshot();
        if (snapshot.IsSuspended)
        {
            slot.Acquired = false;
            return GraphicsPresentStatus.Suspended;
        }
        if (snapshot.Revision != _surfaceRevision ||
            snapshot.Extent.Width != _description.Width || snapshot.Extent.Height != _description.Height)
        {
            slot.Acquired = false;
            return GraphicsPresentStatus.OutOfDate;
        }

        OpenGLESGraphicsFence? fence = null;
        if (waitFence is not null)
        {
            fence = waitFence as OpenGLESGraphicsFence;
            if (fence is null || !ReferenceEquals(fence.Device, _device))
                throw new GraphicsValidationException("The wait fence must belong to this GLES device.");
        }

        using (OpenGLESContextScope context = _device.AcquireContext())
        {
            GL gl = context.Gl;
            fence?.EnqueueGpuWait(gl, waitValue);
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, slot.Framebuffer);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            gl.BlitFramebuffer(
                0, 0,
                checked((int)_description.Width),
                checked((int)_description.Height),
                0, 0,
                checked((int)snapshot.Extent.Width),
                checked((int)snapshot.Extent.Height),
                (uint)GLEnum.ColorBufferBit,
                GLEnum.Linear);

            nint sync = gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, (uint)0);
            if (sync == 0)
                throw new GraphicsException("OpenGL ES failed to create a presentation sync object.");

            if (slot.PresentSync != 0)
                gl.DeleteSync(slot.PresentSync);
            slot.PresentSync = sync;
            gl.Flush();
            _device.ContextProvider.SwapBuffers();
            slot.Acquired = false;
        }

        return GraphicsPresentStatus.Presented;
    }

    /// <summary>Replaces the offscreen image ring for a new description and invalidates all earlier acquired-image handles.</summary>
    /// <param name="description">New image count, extent, format, and present mode.</param>
    /// <remarks>The replacement images are allocated before old images are released, so allocation failure leaves the old swap chain intact. Successful resize waits for old work and increments <see cref="Generation" />.</remarks>
    /// <exception cref="GraphicsValidationException">The description or target extent is invalid, or the queue can no longer present.</exception>
    /// <exception cref="NotSupportedException">The host provider cannot honor the requested present mode.</exception>
    /// <exception cref="GraphicsException">Creating or releasing the native replacement image set fails.</exception>
    {
        ThrowIfDisposed();
        GraphicsSwapChainDescription copy = description;
        copy.Validate();
        if (!_surface.SupportsPresentation(_queue))
            throw new GraphicsValidationException("The swapchain queue is no longer compatible with the host drawable.");
        if (!_device.ContextProvider.SupportsPresentMode(copy.PresentMode))
            throw new NotSupportedException($"The host drawable does not support present mode {copy.PresentMode}.");

        GraphicsPresentationTargetSnapshot snapshot = _surface.RefreshSnapshot();
        if (snapshot.IsSuspended ||
            snapshot.Extent.Width != copy.Width || snapshot.Extent.Height != copy.Height)
            throw new GraphicsValidationException("The replacement swapchain extent must match a non-suspended host drawable.");

        _device.ContextProvider.SetPresentMode(copy.PresentMode);
        // Allocate the replacement set first so a failed resize leaves the existing swapchain usable.
        OpenGLESSwapChainImage[] replacement = CreateImages(copy);
        OpenGLESSwapChainImage[] old = _images;
        try
        {
            using (OpenGLESContextScope context = _device.AcquireContext())
            {
                context.Gl.Finish();
                ReleaseImageNative(context.Gl, old);
            }
        }
        catch
        {
            using (OpenGLESContextScope context = _device.AcquireContext())
                ReleaseImageNative(context.Gl, replacement);
            for (int i = 0; i < replacement.Length; i++)
                replacement[i].Texture.Dispose();
            throw;
        }

        for (int i = 0; i < old.Length; i++)
            old[i].Texture.Dispose();

        _description = copy;
        _surfaceRevision = snapshot.Revision;
        _nextImage = 0;
        _generation = checked(_generation + 1);
        _images = replacement;
    }

    private OpenGLESSwapChainImage[] CreateImages(in GraphicsSwapChainDescription description)
    {
        if (description.BufferCount is < 2 or > 8)
            throw new GraphicsValidationException("The GLES swapchain supports two to eight buffered images.");

        OpenGLESSwapChainImage[] images = new OpenGLESSwapChainImage[checked((int)description.BufferCount)];
        GraphicsTextureDescription textureDescription = new(
            GraphicsTextureDimension.Texture2D,
            description.Format,
            description.Width,
            description.Height,
            1,
            1,
            1,
            1,
            GraphicsTextureUsage.ColorAttachment | GraphicsTextureUsage.CopySource);

        try
        {
            for (int i = 0; i < images.Length; i++)
            {
                OpenGLESGraphicsTexture texture = (OpenGLESGraphicsTexture)_device.CreateTexture(textureDescription);
                uint framebuffer = 0;
                try
                {
                    using (OpenGLESContextScope context = _device.AcquireContext())
                    {
                        GL gl = context.Gl;
                        uint handle = gl.GenFramebuffer();
                        if (handle == 0)
                            throw new GraphicsException("OpenGL ES failed to create a swapchain framebuffer.");
                        try
                        {
                            gl.BindFramebuffer(FramebufferTarget.Framebuffer, handle);
                            gl.FramebufferTexture2D(
                                FramebufferTarget.Framebuffer,
                                GLEnum.ColorAttachment0,
                                TextureTarget.Texture2D,
                                texture.Handle,
                                0);
                            GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                            if (status != GLEnum.FramebufferComplete)
                                throw new GraphicsException($"GLES swapchain framebuffer is incomplete: {status}.");
                            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                            framebuffer = handle;
                        }
                        catch
                        {
                            gl.DeleteFramebuffer(handle);
                            throw;
                        }
                    }
                    images[i] = new OpenGLESSwapChainImage { Texture = texture, Framebuffer = framebuffer };
                }
                catch
                {
                    texture.Dispose();
                    throw;
                }
            }
            return images;
        }
        catch
        {
            using OpenGLESContextScope context = _device.AcquireContext();
            ReleaseImageNative(context.Gl, images);
            for (int i = 0; i < images.Length; i++)
                images[i]?.Texture.Dispose();
            throw;
        }
    }

    private static void ReleaseImageNative(GL gl, OpenGLESSwapChainImage[] images)
    {
        for (int i = 0; i < images.Length; i++)
        {
            OpenGLESSwapChainImage? image = images[i];
            if (image is null)
                continue;
            if (image.PresentSync != 0)
            {
                gl.DeleteSync(image.PresentSync);
                image.PresentSync = 0;
            }
            if (image.Framebuffer != 0)
            {
                gl.DeleteFramebuffer(image.Framebuffer);
                image.Framebuffer = 0;
            }
            image.Acquired = false;
        }
    }

    /// <summary>Closes the public swap-chain owner; native image resources are released under the GLES context.</summary>
    protected override void DisposeCore()
    {
        Exception? failure = null;
        try
        {
            ReleaseOwnerReference();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        for (int i = 0; i < _images.Length; i++)
        {
            try
            {
                _images[i].Texture.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
            throw new GraphicsException("GLES swapchain disposal encountered a native cleanup failure.", failure);
    }

    /// <summary>Deletes all image framebuffers and sync objects while the owning GLES context is current.</summary>
    /// <param name="gl">Current GLES dispatch object used for native deletion.</param>
    public override void ReleaseNative(GL gl) => ReleaseImageNative(gl, _images);
}
