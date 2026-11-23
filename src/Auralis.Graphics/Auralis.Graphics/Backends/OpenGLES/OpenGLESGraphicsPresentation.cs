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
    public IGraphicsSwapChain CreateSwapChain(
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
    private readonly object _stateGate = new();
    private readonly object _resizeGate = new();
    private OpenGLESSwapChainImage[] _images;
    private List<OpenGLESSwapChainImage[]>? _retainedImageSets;
    private Exception? _faulted;
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
        _images = Array.Empty<OpenGLESSwapChainImage>();

        using OpenGLESContextScope context = _device.AcquireContext();
        _device.RegisterUnderContext(this);
        try
        {
            _images = CreateImages(description);
        }
        catch (Exception creationFailure)
        {
            try
            {
                ReleaseOwnerReference();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "OpenGL ES swap-chain creation failed and native rollback could not complete.",
                    creationFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    public GraphicsSwapChainDescription Description
    {
        get
        {
            ThrowIfDisposed();
            ThrowIfFaulted();
            lock (_stateGate)
                return _description;
        }
    }

    public IGraphicsQueue PresentationQueue => _queue;

    public ulong Generation
    {
        get
        {
            ThrowIfDisposed();
            ThrowIfFaulted();
            return Volatile.Read(ref _generation);
        }
    }

    /// <summary>
    /// Acquires the next reusable GLES offscreen image. Context-gate contention and native sync waits
    /// share one monotonic deadline; the context remains activated while pending syncs are waited.
    /// </summary>
    /// <param name="timeout">Maximum CPU wait for an image to become reusable, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <returns>An acquired image, or a timeout, suspended, or out-of-date status without an image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout" /> is negative and is not infinite.</exception>
    /// <exception cref="GraphicsValidationException">The current host snapshot no longer matches the injected GLES drawable.</exception>
    /// <exception cref="GraphicsException">GLES reports a failed client wait.</exception>
    public GraphicsAcquireResult AcquireNextImage(TimeSpan timeout)
    {
        ThrowIfDisposed();
        ThrowIfFaulted();
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        GraphicsPresentationTargetSnapshot snapshot = _surface.RefreshSnapshot();
        if (snapshot.IsSuspended)
            return new GraphicsAcquireResult(GraphicsAcquireStatus.Suspended, default);
        lock (_stateGate)
        {
            if (snapshot.Revision != _surfaceRevision ||
                snapshot.Extent.Width != _description.Width ||
                snapshot.Extent.Height != _description.Height)
            {
                return new GraphicsAcquireResult(GraphicsAcquireStatus.OutOfDate, default);
            }
        }

        OpenGLESAcquireDeadline deadline = new(timeout);
        if (!_device.TryAcquireContext(
                deadline.GetRemainingTime(),
                CancellationToken.None,
                out OpenGLESContextScope context))
        {
            return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);
        }

        using (context)
        {
            // Resize commits while holding this same device gate; revalidate before observing slots.
            if (snapshot.Revision != _surfaceRevision ||
                snapshot.Extent.Width != _description.Width ||
                snapshot.Extent.Height != _description.Height)
            {
                return new GraphicsAcquireResult(GraphicsAcquireStatus.OutOfDate, default);
            }

            GL gl = context.Gl;
            while (true)
            {
                int firstPendingIndex = -1;
                bool hasUnacquiredImage = false;

                // Inspect all slots before blocking so an already-signaled later image wins over
                // an earlier image whose previous GPU work is still pending.
                for (int attempt = 0; attempt < _images.Length; attempt++)
                {
                    uint index = (_nextImage + (uint)attempt) % (uint)_images.Length;
                    OpenGLESSwapChainImage image = _images[index];
                    if (image.Acquired)
                        continue;

                    hasUnacquiredImage = true;
                    if (image.PresentSync == 0)
                        return AcquireImage(index, image);

                    GLEnum status = gl.ClientWaitSync(image.PresentSync, (uint)0, 0);
                    if (status is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)
                    {
                        gl.DeleteSync(image.PresentSync);
                        image.PresentSync = 0;
                        return AcquireImage(index, image);
                    }

                    if (status == GLEnum.WaitFailed)
                        throw new GraphicsException(
                            "OpenGL ES failed while polling a presentation sync before image acquisition.");
                    if (status != GLEnum.TimeoutExpired)
                        throw new GraphicsException(
                            $"OpenGL ES returned unexpected client-wait status {status} for a presentation sync.");

                    if (firstPendingIndex < 0)
                        firstPendingIndex = (int)index;
                }

                // If all slots are held by the application, GPU completion cannot make one reusable.
                if (!hasUnacquiredImage || firstPendingIndex < 0 || deadline.IsExpired)
                    return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

                ulong waitNanoseconds = deadline.GetClientWaitTimeoutNanoseconds();
                if (waitNanoseconds == 0)
                    return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

                OpenGLESSwapChainImage pending = _images[firstPendingIndex];
                // SYNC_FLUSH_COMMANDS_BIT also makes progress if an earlier Flush failed after the
                // fence was inserted. Infinite waits use bounded native intervals rather than the
                // GL_TIMEOUT_IGNORED sentinel, which is not a ClientWaitSync timeout value.
                GLEnum waitStatus = gl.ClientWaitSync(pending.PresentSync, (uint)1, waitNanoseconds);
                if (waitStatus is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)
                {
                    gl.DeleteSync(pending.PresentSync);
                    pending.PresentSync = 0;
                    return AcquireImage((uint)firstPendingIndex, pending);
                }

                if (waitStatus == GLEnum.WaitFailed)
                    throw new GraphicsException(
                        "OpenGL ES failed while waiting for a presented swap-chain image.");
                if (waitStatus != GLEnum.TimeoutExpired)
                    throw new GraphicsException(
                        $"OpenGL ES returned unexpected client-wait status {waitStatus} for a presentation sync.");

                // Rescan all available images before reporting expiry; another slot may have
                // completed while the native wait was blocked. The next scan checks the original
                // deadline after probing every slot, so timeout accounting is not extended.

            }
        }
    }

    private GraphicsAcquireResult AcquireImage(uint index, OpenGLESSwapChainImage image)
    {
        image.Acquired = true;
        _nextImage = (index + 1) % (uint)_images.Length;
        return new GraphicsAcquireResult(
            GraphicsAcquireStatus.Success,
            new GraphicsAcquiredImage(index, _generation, image.Texture));
    }

    internal static void PublishPresentationSync(OpenGLESSwapChainImage slot, nint sync)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (sync == 0)
            throw new ArgumentOutOfRangeException(nameof(sync));
        if (slot.PresentSync != 0)
            throw new InvalidOperationException(
                "An acquired GLES swap-chain image must not already own a presentation sync.");

        // Publish ownership and release the acquired state before the host swap operation. The
        // sync prevents actual reuse until the submitted GPU work has completed.
        slot.PresentSync = sync;
        slot.Acquired = false;
    }

    /// <summary>Blits an acquired image to framebuffer zero, inserts a GLES sync, flushes, and asks the host provider to swap buffers.</summary>
    /// <param name="image">Image previously acquired from this swap chain and not yet presented.</param>
    /// <param name="waitFence">Optional same-device timeline fence whose submitted commands must precede the blit.</param>
    /// <param name="waitValue">Timeline value to wait for when <paramref name="waitFence" /> is supplied.</param>
    /// <returns><see cref="GraphicsPresentStatus.Presented" />, or suspended/out-of-date when the drawable changed.</returns>
    /// <exception cref="ArgumentException">A non-zero wait value is supplied without a fence.</exception>
    /// <exception cref="GraphicsValidationException">The acquired image is stale/foreign or the wait fence belongs to another device.</exception>
    /// <exception cref="GraphicsException">GLES fails to create the presentation sync.</exception>
    public GraphicsPresentStatus Present(
        in GraphicsAcquiredImage image,
        IGraphicsFence? waitFence = null,
        ulong waitValue = 0)
    {
        ThrowIfDisposed();
        ThrowIfFaulted();
        if (waitFence is null && waitValue != 0)
            throw new ArgumentException("A non-zero wait value requires a wait fence.", nameof(waitValue));

        OpenGLESSwapChainImage slot;
        lock (_stateGate)
        {
            if (image.Generation != _generation || image.ImageIndex >= (uint)_images.Length)
                return GraphicsPresentStatus.OutOfDate;

            slot = _images[image.ImageIndex];
        }

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
            if (image.Generation != _generation ||
                image.ImageIndex >= (uint)_images.Length ||
                !ReferenceEquals(_images[image.ImageIndex], slot))
            {
                return GraphicsPresentStatus.OutOfDate;
            }

            if (!slot.Acquired || !ReferenceEquals(slot.Texture, image.Texture))
                throw new GraphicsValidationException(
                    "The acquired GLES image was already presented or invalidated before presentation.");

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

            try
            {
                PublishPresentationSync(slot, sync);
            }
            catch
            {
                // The slot did not take ownership; delete the newly-created object before rethrowing.
                gl.DeleteSync(sync);
                throw;
            }

            gl.Flush();
            // A failed host swap does not undo the submitted GPU work. Keep the completion sync
            // attached so later acquisition can safely wait before reusing this image.
            _device.ContextProvider.SwapBuffers();
        }

        return GraphicsPresentStatus.Presented;
    }

    /// <summary>Replaces the offscreen image ring for a new description and invalidates all earlier acquired-image handles.</summary>
    /// <param name="description">New image count, extent, format, and present mode.</param>
    /// <remarks>The replacement images are allocated before old images are released, so allocation failure leaves the old swap chain intact. Successful resize waits for old work and increments <see cref="Generation" />.</remarks>
    /// <exception cref="GraphicsValidationException">The description or target extent is invalid, or the queue can no longer present.</exception>
    /// <exception cref="NotSupportedException">The host provider cannot honor the requested present mode.</exception>
    /// <exception cref="GraphicsException">Creating or releasing the native replacement image set fails.</exception>
    public void Resize(in GraphicsSwapChainDescription description)
    {
        lock (_resizeGate)
        {
            ThrowIfDisposed();
            ThrowIfFaulted();

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

            ulong nextGeneration = checked(_generation + 1);
            OpenGLESSwapChainImage[] replacement = CreateImages(copy);
            OpenGLESSwapChainImage[] previousImages = _images;
            GraphicsPresentMode previousMode = _description.PresentMode;

            using OpenGLESContextScope context = _device.AcquireContext();
            List<Exception> failures = new(4);

            try
            {
                // This provider side effect occurs only after the full replacement has been built.
                _device.ContextProvider.SetPresentMode(copy.PresentMode);
            }
            catch (Exception modeFailure)
            {
                failures.Add(modeFailure);
                bool restored = TryRestorePresentMode(previousMode, failures);
                CleanupImageSetUnderContext(context.Gl, replacement, failures);
                if (!restored)
                {
                    Volatile.Write(
                        ref _faulted,
                        new AggregateException(
                            "The GLES provider could not restore its previous present mode after a failed resize.",
                            failures));
                }

                throw new AggregateException(
                    "The GLES swap-chain present mode could not be changed; the original ring was preserved.",
                    failures);
            }

            try
            {
                _device.SwapChainTestHooks?.BeforeGpuFinish?.Invoke();
                context.Gl.Finish();
            }
            catch (Exception waitFailure)
            {
                failures.Add(waitFailure);
                bool restored = TryRestorePresentMode(previousMode, failures);
                CleanupImageSetUnderContext(context.Gl, replacement, failures);
                if (!restored)
                {
                    Volatile.Write(
                        ref _faulted,
                        new AggregateException(
                            "The GLES provider could not restore its previous present mode after GPU wait failure.",
                            failures));
                }

                throw new AggregateException(
                    "The previous GLES swap-chain resources were not retired because GPU completion could not be confirmed.",
                    failures);
            }

            // Retirement is destructive. If any native object or texture cannot be released, the
            // old ring may be partial and the chain becomes unusable instead of returning stale images.
            try
            {
                ReleaseImageNative(context.Gl, previousImages);
            }
            catch (Exception retirementFailure)
            {
                failures.Add(retirementFailure);
            }

            for (int i = 0; i < previousImages.Length; i++)
            {
                OpenGLESSwapChainImage image = previousImages[i];
                if (image.PresentSync != 0 || image.Framebuffer != 0)
                {
                    failures.Add(new GraphicsException(
                        "A previous GLES swap-chain image still owns native synchronization or framebuffer resources; its texture cannot be retired safely."));
                    continue;
                }

                try
                {
                    image.Texture.Dispose();
                }
                catch (Exception retirementFailure)
                {
                    failures.Add(retirementFailure);
                }
            }

            if (failures.Count != 0)
            {
                TryRestorePresentMode(previousMode, failures);
                AggregateException retirementError = new(
                    "The previous GLES swap-chain image ring could not be retired completely.",
                    failures);
                Volatile.Write(ref _faulted, retirementError);

                List<Exception> allFailures = new(failures);
                CleanupImageSetUnderContext(context.Gl, replacement, allFailures);
                throw new AggregateException(
                    "GLES swap-chain resize failed during resource retirement. The swap chain is faulted and cannot be used again.",
                    allFailures);
            }

            lock (_stateGate)
            {
                _images = replacement;
                _description = copy;
                _surfaceRevision = snapshot.Revision;
                _nextImage = 0;
                Volatile.Write(ref _generation, nextGeneration);
            }
        }
    }

    private void ThrowIfFaulted()
    {
        Exception? failure = Volatile.Read(ref _faulted);
        if (failure is not null)
            throw new GraphicsException(
                "The OpenGL ES swap chain is unusable because a previous resize could not retire its resources.",
                failure);
    }

    private bool TryRestorePresentMode(GraphicsPresentMode previousMode, List<Exception> failures)
    {
        try
        {
            _device.ContextProvider.SetPresentMode(previousMode);
            return true;
        }
        catch (Exception restoreFailure)
        {
            failures.Add(restoreFailure);
            return false;
        }
    }

    /// <summary>
    /// Attempts cleanup of a staged GLES image set. Images that still own native handles retain
    /// their textures and are kept alive for device-shutdown retry.
    /// </summary>
    private void CleanupImageSetUnderContext(
        GL gl,
        OpenGLESSwapChainImage[] images,
        List<Exception> failures)
    {
        int failureCount = failures.Count;
        try
        {
            ReleaseImageNative(gl, images);
        }
        catch (Exception cleanupFailure)
        {
            failures.Add(cleanupFailure);
        }

        for (int i = 0; i < images.Length; i++)
        {
            OpenGLESSwapChainImage? image = images[i];
            if (image is null)
                continue;

            if (image.PresentSync != 0 || image.Framebuffer != 0)
            {
                failures.Add(new GraphicsException(
                    "A staged GLES swap-chain image still owns native synchronization or framebuffer resources; its texture was retained for safe deferred cleanup."));
                continue;
            }

            try
            {
                image.Texture.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                failures.Add(cleanupFailure);
            }
        }

        if (failures.Count != failureCount)
            RetainImageSetForCleanup(images);
    }

    private void RetainImageSetForCleanup(OpenGLESSwapChainImage[] images)
    {
        (_retainedImageSets ??= new List<OpenGLESSwapChainImage[]>(1)).Add(images);
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
                OpenGLESSwapChainTestHooks? hooks = _device.SwapChainTestHooks;
                hooks?.BeforeTextureCreation?.Invoke();
                OpenGLESGraphicsTexture texture = (OpenGLESGraphicsTexture)_device.CreateTexture(textureDescription);
                OpenGLESSwapChainImage image = new() { Texture = texture };
                images[i] = image;
                hooks?.AfterTextureCreation?.Invoke(texture);

                using (OpenGLESContextScope context = _device.AcquireContext())
                {
                    GL gl = context.Gl;
                    hooks?.BeforeFramebufferCreation?.Invoke(gl);
                    uint framebuffer = gl.GenFramebuffer();
                    if (framebuffer == 0)
                        throw new GraphicsException("OpenGL ES failed to create a swapchain framebuffer.");

                    image.Framebuffer = framebuffer;
                    hooks?.AfterFramebufferCreated?.Invoke(framebuffer);
                    try
                    {
                        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                        gl.FramebufferTexture2D(
                            FramebufferTarget.Framebuffer,
                            GLEnum.ColorAttachment0,
                            TextureTarget.Texture2D,
                            texture.Handle,
                            0);
                        GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                        if (status != GLEnum.FramebufferComplete)
                            throw new GraphicsException($"GLES swapchain framebuffer is incomplete: {status}.");
                    }
                    finally
                    {
                        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                    }
                }
            }

            return images;
        }
        catch (Exception creationFailure)
        {
            List<Exception> cleanupFailures = new(2);
            try
            {
                using OpenGLESContextScope context = _device.AcquireContext();
                try
                {
                    ReleaseImageNative(context.Gl, images);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }

                for (int i = 0; i < images.Length; i++)
                {
                    OpenGLESSwapChainImage? image = images[i];
                    if (image is null)
                        continue;

                    if (image.PresentSync != 0 || image.Framebuffer != 0)
                    {
                        cleanupFailures.Add(new GraphicsException(
                            "A partially created GLES swap-chain image still owns a native sync or framebuffer; its texture was retained for safe deferred cleanup."));
                        continue;
                    }

                    try
                    {
                        image.Texture.Dispose();
                    }
                    catch (Exception cleanupFailure)
                    {
                        cleanupFailures.Add(cleanupFailure);
                    }
                }
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }

            if (cleanupFailures.Count != 0)
            {
                cleanupFailures.Insert(0, creationFailure);
                RetainImageSetForCleanup(images);
                throw new AggregateException(
                    "GLES swap-chain image creation failed and partial replacement cleanup was incomplete.",
                    cleanupFailures);
            }

            throw;
        }
    }

    private static void ReleaseImageNative(GL gl, OpenGLESSwapChainImage[] images)
    {
        List<Exception>? failures = null;
        for (int i = 0; i < images.Length; i++)
        {
            OpenGLESSwapChainImage? image = images[i];
            if (image is null)
                continue;

            if (image.PresentSync != 0)
            {
                try
                {
                    gl.DeleteSync(image.PresentSync);
                    image.PresentSync = 0;
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>(2)).Add(exception);
                }
            }

            if (image.Framebuffer != 0)
            {
                try
                {
                    _device.SwapChainTestHooks?.BeforeFramebufferDeletion?.Invoke(image.Framebuffer);
                    gl.DeleteFramebuffer(image.Framebuffer);
                    image.Framebuffer = 0;
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>(2)).Add(exception);
                }
            }

            image.Acquired = false;
        }

        if (failures is { Count: > 0 })
            throw new AggregateException("One or more GLES swap-chain image resources could not be released.", failures);
    }

    /// <summary>Closes the public swap-chain owner; native image resources are released under the GLES context.</summary>
    protected override void DisposeCore()
    {
        List<Exception>? failures = null;
        try
        {
            ReleaseOwnerReference();
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>(4)).Add(exception);
        }

        DisposeImageTextures(_images, ref failures);
        if (_retainedImageSets is not null)
        {
            for (int i = 0; i < _retainedImageSets.Count; i++)
                DisposeImageTextures(_retainedImageSets[i], ref failures);
        }

        if (failures is { Count: > 0 })
            throw new AggregateException("GLES swap-chain disposal encountered one or more cleanup failures.", failures);
    }

    private static void DisposeImageTextures(
        OpenGLESSwapChainImage[] images,
        ref List<Exception>? failures)
    {
        for (int i = 0; i < images.Length; i++)
        {
            OpenGLESSwapChainImage? image = images[i];
            if (image is null)
                continue;

            // Keep the texture owner reference until deferred or failed native attachment
            // teardown has completed.
            if (image.PresentSync != 0 || image.Framebuffer != 0)
                continue;

            try
            {
                image.Texture.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>(4)).Add(exception);
            }
        }
    }

    /// <summary>Deletes all image framebuffers and sync objects while the owning GLES context is current.</summary>
    /// <param name="gl">Current GLES dispatch object used for native deletion.</param>
    public override void ReleaseNative(GL gl)
    {
        List<Exception>? failures = null;
        try
        {
            ReleaseImageNative(gl, _images);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>(4)).Add(exception);
        }

        if (_retainedImageSets is not null)
        {
            for (int i = 0; i < _retainedImageSets.Count; i++)
            {
                try
                {
                    ReleaseImageNative(gl, _retainedImageSets[i]);
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>(4)).Add(exception);
                }
            }
        }

        DisposeImageTextures(_images, ref failures);
        if (_retainedImageSets is not null)
        {
            for (int i = 0; i < _retainedImageSets.Count; i++)
                DisposeImageTextures(_retainedImageSets[i], ref failures);
        }

        if (failures is { Count: > 0 })
            throw new AggregateException("GLES swap-chain native cleanup was incomplete and may be retried.", failures);
    }

}
