using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Backends;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Presentation;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Tracks one swap-chain image's color texture, framebuffer, presentation sync, and acquisition state.</summary>
internal sealed class OpenGLSwapChainImage
{
    internal required OpenGLGraphicsTexture Texture;
    internal uint Framebuffer;
    internal nint PresentSync;
    internal bool Acquired;
}

/// <summary>
/// Swapchain implemented as a ring of Auralis-owned offscreen color textures. Present blits the
/// selected texture to the provider's actual default drawable and asks that provider to swap it.
/// The default framebuffer is never exposed as an IGraphicsTexture.
/// </summary>
internal sealed class OpenGLGraphicsSwapChain : OpenGLNativeDeviceObject, IGraphicsSwapChain
{
    private readonly OpenGLGraphicsPresentationSurface _surface;
    private readonly OpenGLGraphicsCommandQueue _queue;
    private readonly OpenGLGraphicsDevice _device;
    private OpenGLSwapChainImage[] _images;
    private GraphicsSwapChainDescription _description;
    private ulong _generation = 1;
    private ulong _surfaceRevision;
    private uint _nextImage;
    private int _nativeReleased;

    internal OpenGLGraphicsSwapChain(
        OpenGLGraphicsPresentationSurface surface,
        OpenGLGraphicsCommandQueue queue,
        in GraphicsSwapChainDescription description,
        in GraphicsPresentationTargetSnapshot snapshot) : base((OpenGLGraphicsDevice)surface.Device)
    {
        _surface = surface;
        _queue = queue;
        _device = (OpenGLGraphicsDevice)surface.Device;
        _description = description;
        _surfaceRevision = snapshot.Revision;
        _images = CreateImages(description);
        using OpenGLContextScope context = _device.AcquireContext();
        _device.RegisterUnderContext(this);
    }

    public GraphicsSwapChainDescription Description
    {
        get
        {
            ThrowIfDisposed();
            return _description;
        }
    }

    public IGraphicsQueue PresentationQueue => _queue;

    public ulong Generation
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref _generation);
        }
    }

    /// <summary>Acquires the next reusable offscreen image, waiting for its previous presentation blit when necessary.</summary>
    /// <param name="timeout">Maximum CPU wait for an image to become reusable, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <returns>An acquired image on success, or a timeout, suspended, or out-of-date status without an image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout" /> is negative and is not infinite.</exception>
    /// <exception cref="GraphicsException">The driver fails while waiting on a presentation sync object.</exception>
    public GraphicsAcquireResult AcquireNextImage(TimeSpan timeout)
    {
        ThrowIfDisposed();
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        // The target refresh, context-gate contention, native waits, and acquisition commit all
        // consume this one monotonic budget. A zero timeout still performs a nonblocking scan.
        GraphicsAcquireDeadline deadline = new(timeout);
        bool allowImmediatePoll = timeout == TimeSpan.Zero;

        GraphicsPresentationTargetSnapshot snapshot;
        try
        {
            snapshot = _surface.RefreshSnapshot();
        }
        catch (GraphicsBackendUnavailableException)
        {
            return new GraphicsAcquireResult(GraphicsAcquireStatus.OutOfDate, default);
        }

        if (snapshot.IsSuspended)
            return new GraphicsAcquireResult(GraphicsAcquireStatus.Suspended, default);

        if (snapshot.Revision != _surfaceRevision)
            return new GraphicsAcquireResult(GraphicsAcquireStatus.OutOfDate, default);

        if (!_device.TryAcquireContext(
                deadline.GetRemainingTime(),
                CancellationToken.None,
                out OpenGLContextScope context))
        {
            return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);
        }

        using (context)
        {
            GL gl = context.Gl;
            while (true)
            {
                int firstPendingIndex = -1;
                bool hasUnacquiredImage = false;

                // Poll every slot before waiting. An already-reusable later image must not be
                // blocked behind an earlier image whose previous presentation is still pending.
                for (int attempt = 0; attempt < _images.Length; attempt++)
                {
                    uint index = (_nextImage + (uint)attempt) % (uint)_images.Length;
                    OpenGLSwapChainImage image = _images[index];
                    if (image.Acquired)
                        continue;

                    hasUnacquiredImage = true;
                    if (image.PresentSync == 0)
                    {
                        if (!allowImmediatePoll && deadline.IsExpired)
                            return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

                        // Revalidate immediately before publishing acquisition.
                        if (!allowImmediatePoll && deadline.IsExpired)
                            return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

                        return AcquireImage(index, image);
                    }

                    ARB pollStatus = _device.SyncApi.ClientWaitSync(
                        image.PresentSync,
                        (SyncObjectMask)0,
                        0);

                    if (pollStatus is ARB.AlreadySignaled or ARB.ConditionSatisfied)
                    {
                        if (!allowImmediatePoll && deadline.IsExpired)
                            return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

                        _device.SyncApi.DeleteSync(image.PresentSync);
                        image.PresentSync = 0;
                        return AcquireImage(index, image);
                    }

                    if (pollStatus == ARB.WaitFailed)
                        throw new GraphicsException(
                            "OpenGL failed while polling a presentation sync before image acquisition.");
                    if (pollStatus != ARB.TimeoutExpired)
                        throw new GraphicsException(
                            $"OpenGL returned unexpected client-wait status {pollStatus} for a presentation sync.");

                    if (firstPendingIndex < 0)
                        firstPendingIndex = (int)index;
                }

                // If all slots are held by the application, GPU completion cannot make one reusable.
                if (!hasUnacquiredImage || firstPendingIndex < 0 || deadline.IsExpired)
                    return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

                // Calculate the native timeout immediately before every blocking wait. Infinite
                // waits are deliberately sliced so no GL_TIMEOUT_IGNORED sentinel is passed.
                ulong remainingNanoseconds = deadline.GetClientWaitTimeoutNanoseconds();
                if (remainingNanoseconds == 0)
                    return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

                OpenGLSwapChainImage pending = _images[firstPendingIndex];
                ARB waitStatus = _device.SyncApi.ClientWaitSync(
                    pending.PresentSync,
                    (SyncObjectMask)1, // GL_SYNC_FLUSH_COMMANDS_BIT
                    remainingNanoseconds);

                if (waitStatus is ARB.AlreadySignaled or ARB.ConditionSatisfied)
                {
                    // The sync is complete even if the deadline expired while the native call was
                    // returning. Retire it, but leave the image unacquired for the next operation.
                    _device.SyncApi.DeleteSync(pending.PresentSync);
                    pending.PresentSync = 0;

                    if (deadline.IsExpired)
                        return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

                    return AcquireImage((uint)firstPendingIndex, pending);
                }

                if (waitStatus == ARB.WaitFailed)
                    throw new GraphicsException(
                        "OpenGL failed while waiting for a presented swap-chain image.");
                if (waitStatus != ARB.TimeoutExpired)
                    throw new GraphicsException(
                        $"OpenGL returned unexpected client-wait status {waitStatus} for a presentation sync.");

                // A timed wait consumes the original deadline. Do not rescan and acquire an image
                // that became available only after the caller's timeout has elapsed.
                if (deadline.IsExpired)
                    return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);
            }
        }
    }

    private GraphicsAcquireResult AcquireImage(uint index, OpenGLSwapChainImage image)
    {
        image.Acquired = true;
        _nextImage = (index + 1) % (uint)_images.Length;
        return new GraphicsAcquireResult(
            GraphicsAcquireStatus.Success,
            new GraphicsAcquiredImage(index, _generation, image.Texture));
    }

    /// <summary>Blits an acquired image to the host drawable, inserts a reuse sync, and requests a host buffer swap.</summary>
    /// <param name="image">Image previously acquired from this swap chain and not yet presented.</param>
    /// <param name="waitFence">Optional same-device timeline fence whose submitted GPU work must precede the blit.</param>
    /// <param name="waitValue">Timeline value to wait for when <paramref name="waitFence" /> is supplied.</param>
    /// <returns><see cref="GraphicsPresentStatus.Presented" />, or a suspended/out-of-date status when the host target changed.</returns>
    /// <exception cref="ArgumentException">A non-zero wait value is supplied without a fence.</exception>
    /// <exception cref="GraphicsValidationException">The image is stale/foreign or the wait fence belongs to another device.</exception>
    /// <exception cref="GraphicsException">The driver cannot create the presentation completion sync.</exception>
    public GraphicsPresentStatus Present(
        in GraphicsAcquiredImage image,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0)
    {
        ThrowIfDisposed();

        if (waitFence is null && waitValue != 0)
            throw new ArgumentException("A non-zero wait value requires a fence.", nameof(waitValue));

        if (image.Generation != _generation || image.ImageIndex >= (uint)_images.Length)
            return GraphicsPresentStatus.OutOfDate;

        OpenGLSwapChainImage slot = _images[image.ImageIndex];
        if (!slot.Acquired || !ReferenceEquals(slot.Texture, image.Texture))
            throw new GraphicsValidationException("The acquired image is stale, already presented, or belongs to another swapchain.");

        GraphicsPresentationTargetSnapshot snapshot;
        try
        {
            snapshot = _surface.RefreshSnapshot();
        }
        catch (GraphicsBackendUnavailableException)
        {
            slot.Acquired = false;
            return GraphicsPresentStatus.OutOfDate;
        }

        if (snapshot.IsSuspended)
        {
            slot.Acquired = false;
            return GraphicsPresentStatus.Suspended;
        }

        if (snapshot.Revision != _surfaceRevision)
        {
            slot.Acquired = false;
            return GraphicsPresentStatus.OutOfDate;
        }

        OpenGLGraphicsFence? openGlFence = null;
        if (waitFence is not null)
        {
            openGlFence = waitFence as OpenGLGraphicsFence;
            if (openGlFence is null || !ReferenceEquals(openGlFence.Device, _device))
                throw new GraphicsValidationException("The present wait fence must belong to this OpenGL device.");
        }

        using (OpenGLContextScope context = _device.AcquireContext())
        {
            GL gl = context.Gl;
            if (openGlFence is not null)
                openGlFence.EnqueueGpuWait(gl, waitValue);

            // The default framebuffer's draw-buffer selection belongs to the current
            // drawable. Do not inherit a stale host setting; select BACK for double-buffered
            // contexts and FRONT for single-buffered contexts.
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            gl.DrawBuffer(gl.GetInteger(GLEnum.Doublebuffer) != 0
                ? DrawBufferMode.Back
                : DrawBufferMode.Front);

            _device.DirectStateAccess.BlitNamedFramebuffer(
                slot.Framebuffer,
                0,
                0,
                0,
                checked((int)_description.Width),
                checked((int)_description.Height),
                0,
                0,
                checked((int)snapshot.Extent.Width),
                checked((int)snapshot.Extent.Height),
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear);

            nint sync = _device.SyncApi.FenceSync(
                SyncCondition.SyncGpuCommandsComplete,
                SyncBehaviorFlags.None);
            if (sync == 0)
                throw new GraphicsException("OpenGL failed to create a presentation completion sync.");

            if (slot.PresentSync != 0)
                _device.SyncApi.DeleteSync(slot.PresentSync);
            slot.PresentSync = sync;
            slot.Acquired = false;

            _device.ContextProvider.SwapBuffers();
        }

        return GraphicsPresentStatus.Presented;
    }

    /// <summary>Recreates the offscreen image ring for a new description and invalidates all previously acquired image handles.</summary>
    /// <param name="description">New image count, extent, format, and present mode.</param>
    /// <remarks>The device waits for pending GL work before deleting old framebuffers/textures. A successful resize increments <see cref="Generation" /> so stale acquired-image values can be rejected.</remarks>
    /// <exception cref="GraphicsValidationException">The description is invalid, the host target is suspended, or the queue/target is no longer compatible.</exception>
    /// <exception cref="NotSupportedException">The provider cannot honor the requested present mode.</exception>
    public void Resize(in GraphicsSwapChainDescription description)
    {
        ThrowIfDisposed();
        GraphicsSwapChainDescription copy = description;
        copy.Validate();

        if (!_surface.SupportsPresentation(_queue))
            throw new GraphicsValidationException("The swapchain's queue is no longer compatible with its surface.");

        OpenGLPresentationTargetValidation.ValidatePresentMode(_device.ContextProvider, copy.PresentMode);
        _device.ContextProvider.SetPresentMode(copy.PresentMode);
        GraphicsPresentationTargetSnapshot snapshot = _surface.RefreshSnapshot();
        if (snapshot.IsSuspended)
            throw new GraphicsValidationException("A swapchain cannot be resized to a suspended presentation target.");

        // Resize invalidates every acquired image and waits for all previous presentation blits
        // before releasing their backing textures/framebuffers.
        OpenGLSwapChainImage[] oldImages = _images;
        using (OpenGLContextScope context = _device.AcquireContext())
        {
            context.Gl.Finish();
            ReleaseImageNative(context.Gl, oldImages);
        }

        for (int i = 0; i < oldImages.Length; i++)
            oldImages[i].Texture.Dispose();

        _description = copy;
        _surfaceRevision = snapshot.Revision;
        _nextImage = 0;
        _generation = checked(_generation + 1);
        _images = CreateImages(copy);
    }

    private OpenGLSwapChainImage[] CreateImages(in GraphicsSwapChainDescription description)
    {
        if (description.BufferCount > int.MaxValue)
            throw new GraphicsValidationException("The requested swapchain buffer count exceeds the managed array limit.");

        OpenGLSwapChainImage[] images = new OpenGLSwapChainImage[checked((int)description.BufferCount)];
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
                IGraphicsTexture created = _device.CreateTexture(textureDescription);
                if (created is not OpenGLGraphicsTexture texture)
                {
                    created.Dispose();
                    throw new GraphicsException("The OpenGL swapchain texture was created by an incompatible backend.");
                }

                uint framebuffer = 0;
                try
                {
                    using (OpenGLContextScope context = _device.AcquireContext())
                    {
                        GL gl = context.Gl;
                        _device.DirectStateAccess.CreateFramebuffers(1, out uint handle);
                        if (handle == 0)
                            throw new GraphicsException("OpenGL failed to create a swapchain framebuffer.");

                        try
                        {
                            _device.DirectStateAccess.NamedFramebufferTexture(
                                handle,
                                FramebufferAttachment.ColorAttachment0,
                                texture.Handle,
                                0);
                            _device.DirectStateAccess.NamedFramebufferDrawBuffer(handle, ColorBuffer.ColorAttachment0);
                            _device.DirectStateAccess.NamedFramebufferReadBuffer(handle, ColorBuffer.ColorAttachment0);

                            ARB status = _device.DirectStateAccess.CheckNamedFramebufferStatus(
                                handle,
                                FramebufferTarget.Framebuffer);
                            if (status != ARB.FramebufferComplete)
                                throw new GraphicsException($"Swapchain framebuffer is incomplete: {status}.");

                            framebuffer = handle;
                        }
                        catch
                        {
                            gl.DeleteFramebuffer(handle);
                            throw;
                        }
                    }

                    images[i] = new OpenGLSwapChainImage
                    {
                        Texture = texture,
                        Framebuffer = framebuffer
                    };
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
            using OpenGLContextScope context = _device.AcquireContext();
            ReleaseImageNative(context.Gl, images);
            for (int i = 0; i < images.Length; i++)
                images[i]?.Texture.Dispose();
            throw;
        }
    }

    private void ReleaseImageNative(GL gl, OpenGLSwapChainImage[] images)
    {
        for (int i = 0; i < images.Length; i++)
        {
            OpenGLSwapChainImage? image = images[i];
            if (image is null)
                continue;

            if (image.PresentSync != 0)
            {
                _device.SyncApi.DeleteSync(image.PresentSync);
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

    private static ulong GetRemainingNanoseconds(long startTimestamp, long timeoutTicks)
    {
        if (timeoutTicks == long.MaxValue)
            return ulong.MaxValue;

        long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        long remainingTicks = Math.Max(0, timeoutTicks - elapsed);
        if (remainingTicks == 0)
            return 0;

        double nanoseconds = remainingTicks * (1_000_000_000d / Stopwatch.Frequency);
        return nanoseconds >= ulong.MaxValue ? ulong.MaxValue : (ulong)nanoseconds;
    }

    /// <summary>Releases owner references; actual framebuffer and texture teardown is performed under the current GL context.</summary>
    protected override void DisposeCore()
    {
        // Enqueue the framebuffer/sync objects before textures so FIFO deferred deletion drops
        // framebuffer references before releasing the attached texture names.
        ReleaseOwnerReference();
        for (int i = 0; i < _images.Length; i++)
            _images[i].Texture.Dispose();
    }

    /// <summary>Deletes per-image framebuffers and presentation syncs while the owning context is current.</summary>
    /// <param name="gl">Current GL dispatch object used for native deletion.</param>
    public override void ReleaseNative(GL gl)
    {
        if (Interlocked.Exchange(ref _nativeReleased, 1) != 0)
            return;
        ReleaseImageNative(gl, _images);
    }
}
