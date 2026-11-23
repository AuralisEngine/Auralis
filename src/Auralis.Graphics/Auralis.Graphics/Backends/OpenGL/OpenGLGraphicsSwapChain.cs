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
    private readonly object _stateGate = new();
    private readonly object _resizeGate = new();
    private OpenGLSwapChainImage[] _images;
    private List<OpenGLSwapChainImage[]>? _retainedImageSets;
    private Exception? _faulted;
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
        _images = Array.Empty<OpenGLSwapChainImage>();

        // Register before allocating so a failed constructor can still retain and retry cleanup
        // of any partially created native framebuffer set through the device resource registry.
        using OpenGLContextScope context = _device.AcquireContext();
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
                    "OpenGL swap-chain creation failed and native rollback could not complete.",
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

    /// <summary>Acquires the next reusable offscreen image, waiting for its previous presentation blit when necessary.</summary>
    /// <param name="timeout">Maximum CPU wait for an image to become reusable, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <returns>An acquired image on success, or a timeout, suspended, or out-of-date status without an image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout" /> is negative and is not infinite.</exception>
    /// <exception cref="GraphicsException">The driver fails while waiting on a presentation sync object.</exception>
    public GraphicsAcquireResult AcquireNextImage(TimeSpan timeout)
    {
        ThrowIfDisposed();
        ThrowIfFaulted();
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        // The target refresh, context-gate contention, native waits, and acquisition commit all
        // consume this one monotonic budget. A zero timeout still performs a nonblocking scan.
        OpenGLSwapChainTestHooks? hooks = _device.SwapChainTestHooks;
        GraphicsAcquireDeadline deadline = hooks?.TimestampProvider is { } timestampProvider
            ? new GraphicsAcquireDeadline(timeout, timestampProvider)
            : new GraphicsAcquireDeadline(timeout);
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

        ulong observedGeneration;
        lock (_stateGate)
        {
            observedGeneration = _generation;
            if (snapshot.Revision != _surfaceRevision ||
                snapshot.Extent.Width != _description.Width ||
                snapshot.Extent.Height != _description.Height)
            {
                return new GraphicsAcquireResult(GraphicsAcquireStatus.OutOfDate, default);
            }
        }

        if (!_device.TryAcquireContext(
                deadline.GetRemainingTime(),
                CancellationToken.None,
                out OpenGLContextScope context))
        {
            return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);
        }

        using (context)
        {
            // Resize commits while holding this same device gate, so this recheck prevents a scan
            // against a newly published ring using an older presentation-target snapshot.
            if (observedGeneration != _generation ||
                snapshot.Revision != _surfaceRevision ||
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
                        // Revalidate immediately before publishing acquisition.
                        if (!allowImmediatePoll && deadline.IsExpired)
                            return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

                        return AcquireImage(index, image);
                    }

                    ARB pollStatus = hooks?.PollClientWaitSync is { } poll
                        ? poll(image.PresentSync)
                        : _device.SyncApi.ClientWaitSync(
                            image.PresentSync,
                            (SyncObjectMask)0,
                            0);

                    if (pollStatus is ARB.AlreadySignaled or ARB.ConditionSatisfied)
                    {
                        // A successful zero-timeout poll proves the dependency is complete. Always
                        // retire the sync first, even when the overall deadline expired during the
                        // poll; the image remains reusable by the next acquisition.
                        nint completedSync = image.PresentSync;
                        _device.SyncApi.DeleteSync(completedSync);
                        image.PresentSync = 0;
                        hooks?.AfterAcquireSyncDeleted?.Invoke(completedSync);
                        hooks?.AfterPresentationSyncDeleted?.Invoke(completedSync);
                        if (!allowImmediatePoll && deadline.IsExpired)
                            return new GraphicsAcquireResult(GraphicsAcquireStatus.Timeout, default);

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
                ARB waitStatus = hooks?.WaitClientWaitSync is { } wait
                    ? wait(pending.PresentSync, (SyncObjectMask)1, remainingNanoseconds)
                    : _device.SyncApi.ClientWaitSync(
                        pending.PresentSync,
                        (SyncObjectMask)1, // GL_SYNC_FLUSH_COMMANDS_BIT
                        remainingNanoseconds);

                if (waitStatus is ARB.AlreadySignaled or ARB.ConditionSatisfied)
                {
                    // The sync is complete even if the deadline expired while the native call was
                    // returning. Retire it, but leave the image unacquired for the next operation.
                    nint completedSync = pending.PresentSync;
                    _device.SyncApi.DeleteSync(completedSync);
                    pending.PresentSync = 0;
                    hooks?.AfterAcquireSyncDeleted?.Invoke(completedSync);
                    hooks?.AfterPresentationSyncDeleted?.Invoke(completedSync);

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

    private void ThrowIfFaulted()
    {
        Exception? failure = Volatile.Read(ref _faulted);
        if (failure is not null)
            throw new GraphicsException(
                "The OpenGL swap chain is unusable because a previous resize could not retire its resources.",
                failure);
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
        ThrowIfFaulted();

        if (waitFence is null && waitValue != 0)
            throw new ArgumentException("A non-zero wait value requires a fence.", nameof(waitValue));

        OpenGLSwapChainImage slot;
        lock (_stateGate)
        {
            if (image.Generation != _generation || image.ImageIndex >= (uint)_images.Length)
                return GraphicsPresentStatus.OutOfDate;

            slot = _images[image.ImageIndex];
        }

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
            // Revalidate under the device gate: Resize may have committed while this Present call
            // was preparing its target/fence state, and another thread may have presented the same lease.
            if (image.Generation != _generation ||
                image.ImageIndex >= (uint)_images.Length ||
                !ReferenceEquals(_images[image.ImageIndex], slot))
            {
                return GraphicsPresentStatus.OutOfDate;
            }

            if (!slot.Acquired || !ReferenceEquals(slot.Texture, image.Texture))
                throw new GraphicsValidationException(
                    "The acquired OpenGL image was already presented or invalidated before presentation.");

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

            if (slot.PresentSync != 0)
                throw new GraphicsValidationException(
                    "An acquired OpenGL swap-chain image unexpectedly still owns a presentation sync.");

            nint sync = _device.SyncApi.FenceSync(
                SyncCondition.SyncGpuCommandsComplete,
                SyncBehaviorFlags.None);
            if (sync == 0)
                throw new GraphicsException("OpenGL failed to create a presentation completion sync.");

            _device.SwapChainTestHooks?.AfterPresentationSyncCreated?.Invoke(sync);

            // Publish the completion dependency before the host swap. SwapBuffers may throw after
            // the blit and fence have already been submitted; the image must retain sync ownership
            // and be marked unacquired so a later acquisition waits before reuse.
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
        lock (_resizeGate)
        {
            ThrowIfDisposed();
            ThrowIfFaulted();

            GraphicsSwapChainDescription copy = description;
            copy.Validate();

            if (!_surface.SupportsPresentation(_queue))
                throw new GraphicsValidationException("The swapchain's queue is no longer compatible with its surface.");

            OpenGLPresentationTargetValidation.ValidatePresentMode(_device.ContextProvider, copy.PresentMode);
            GraphicsPresentationTargetSnapshot snapshot = _surface.RefreshSnapshot();
            if (snapshot.IsSuspended)
                throw new GraphicsValidationException("A swapchain cannot be resized to a suspended presentation target.");

            // Calculate this before creating or retiring anything so generation overflow cannot
            // occur after the previous ring has already been destroyed.
            ulong nextGeneration = checked(_generation + 1);

            // Stage the complete replacement before changing provider state or touching the active ring.
            OpenGLSwapChainImage[] replacement = CreateImages(copy);
            OpenGLSwapChainImage[] previousImages = _images;
            GraphicsPresentMode previousMode = _description.PresentMode;

            using OpenGLContextScope context = AcquireResizeContext(replacement);
            List<Exception> failures = new(4);

            try
            {
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
                        new AggregateException("The provider could not restore the previous present mode after a failed resize.", failures));
                }

                throw new AggregateException(
                    "The swap-chain present mode could not be changed; the original image ring was preserved.",
                    failures);
            }

            try
            {
                // No previous sync or framebuffer is released until the driver confirms all work is idle.
                _device.SwapChainTestHooks?.BeforeGpuFinish?.Invoke();
                context.Gl.Finish();
            }
            catch (Exception waitFailure)
            {
                failures.Clear();
                failures.Add(waitFailure);
                bool restored = TryRestorePresentMode(previousMode, failures);
                CleanupImageSetUnderContext(context.Gl, replacement, failures);
                if (!restored)
                {
                    Volatile.Write(
                        ref _faulted,
                        new AggregateException("The provider could not restore the previous present mode after a failed GPU wait.", failures));
                }

                throw new AggregateException(
                    "The previous swap-chain resources were not retired because waiting for GPU completion failed.",
                    failures);
            }

            // From this point onward retirement has begun. Every release is attempted. If any release
            // fails, the previous ring may be partially destroyed and this swap chain is permanently
            // faulted rather than continuing to return potentially disposed images.
            failures.Clear();
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
                OpenGLSwapChainImage image = previousImages[i];
                if (image.PresentSync != 0 || image.Framebuffer != 0)
                {
                    failures.Add(new GraphicsException(
                        "A previous swap-chain image still owns native synchronization or framebuffer resources; its texture cannot be retired safely."));
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
                    "The previous swap-chain image ring could not be retired completely.",
                    failures);
                Volatile.Write(ref _faulted, retirementError);

                List<Exception> allFailures = new(failures);
                CleanupImageSetUnderContext(context.Gl, replacement, allFailures);
                throw new AggregateException(
                    "Swap-chain resize failed during resource retirement. The swap chain is faulted and cannot be used again.",
                    allFailures);
            }

            // Publish the replacement as one state transition while holding the device context gate.
            // Public description/generation readers take _stateGate so they cannot observe half a commit.
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

    /// <summary>
    /// Acquires the context required to retire the active ring. If activation fails, the complete
    /// staged ring remains referenced so device shutdown can retry its native cleanup.
    /// </summary>
    private OpenGLContextScope AcquireResizeContext(OpenGLSwapChainImage[] replacement)
    {
        try
        {
            return _device.AcquireContext();
        }
        catch (Exception contextFailure)
        {
            RetainImageSetForCleanup(replacement);
            throw new GraphicsException(
                "The replacement OpenGL image ring was retained because the context could not be acquired for resize retirement.",
                contextFailure);
        }
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
    /// Attempts to release every native and managed resource in a staged image set. Failed cleanup
    /// sets remain referenced by the swap chain so device shutdown can retry their native releases.
    /// </summary>
    private void CleanupImageSetUnderContext(
        GL gl,
        OpenGLSwapChainImage[] images,
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
            OpenGLSwapChainImage? image = images[i];
            if (image is null)
                continue;

            if (image.PresentSync != 0 || image.Framebuffer != 0)
            {
                failures.Add(new GraphicsException(
                    "A staged swap-chain image still owns native synchronization or framebuffer resources; its texture was retained for safe deferred cleanup."));
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

    private void RetainImageSetForCleanup(OpenGLSwapChainImage[] images)
    {
        (_retainedImageSets ??= new List<OpenGLSwapChainImage[]>(1)).Add(images);
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
                OpenGLSwapChainTestHooks? hooks = _device.SwapChainTestHooks;
                hooks?.BeforeTextureCreation?.Invoke();
                IGraphicsTexture created = _device.CreateTexture(textureDescription);
                if (created is not OpenGLGraphicsTexture texture)
                {
                    created.Dispose();
                    throw new GraphicsException("The OpenGL swapchain texture was created by an incompatible backend.");
                }

                OpenGLSwapChainImage image = new() { Texture = texture };
                images[i] = image;
                hooks?.AfterTextureCreation?.Invoke(texture);

                using (OpenGLContextScope context = _device.AcquireContext())
                {
                    GL gl = context.Gl;
                    hooks?.BeforeFramebufferCreation?.Invoke(gl);
                    _device.DirectStateAccess.CreateFramebuffers(1, out uint framebuffer);
                    if (framebuffer == 0)
                        throw new GraphicsException("OpenGL failed to create a swapchain framebuffer.");

                    // Store the handle before initialization so the outer rollback path owns it
                    // even when attachment setup or completeness validation fails.
                    image.Framebuffer = framebuffer;
                    hooks?.AfterFramebufferCreated?.Invoke(framebuffer);
                    _device.DirectStateAccess.NamedFramebufferTexture(
                        framebuffer,
                        FramebufferAttachment.ColorAttachment0,
                        texture.Handle,
                        0);
                    _device.DirectStateAccess.NamedFramebufferDrawBuffer(
                        framebuffer,
                        ColorBuffer.ColorAttachment0);
                    _device.DirectStateAccess.NamedFramebufferReadBuffer(
                        framebuffer,
                        ColorBuffer.ColorAttachment0);

                    ARB status = _device.DirectStateAccess.CheckNamedFramebufferStatus(
                        framebuffer,
                        FramebufferTarget.Framebuffer);
                    if (status != ARB.FramebufferComplete)
                        throw new GraphicsException($"Swapchain framebuffer is incomplete: {status}.");
                }
            }

            return images;
        }
        catch (Exception creationFailure)
        {
            List<Exception> cleanupFailures = new(2);
            try
            {
                using OpenGLContextScope context = _device.AcquireContext();
                try
                {
                    ReleaseImageNative(context.Gl, images);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }

                // Attempt every texture release even when framebuffer/sync retirement failed.
                for (int i = 0; i < images.Length; i++)
                {
                    OpenGLSwapChainImage? image = images[i];
                    if (image is null)
                        continue;

                    if (image.PresentSync != 0 || image.Framebuffer != 0)
                    {
                        cleanupFailures.Add(new GraphicsException(
                            "A partially created swap-chain image still owns a native sync or framebuffer; its texture was retained for safe deferred cleanup."));
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
                    "OpenGL swap-chain image creation failed and partial replacement cleanup was incomplete.",
                    cleanupFailures);
            }

            throw;
        }
    }

    private void ReleaseImageNative(GL gl, OpenGLSwapChainImage[] images)
    {
        List<Exception>? failures = null;
        for (int i = 0; i < images.Length; i++)
        {
            OpenGLSwapChainImage? image = images[i];
            if (image is null)
                continue;

            if (image.PresentSync != 0)
            {
                try
                {
                    _device.SyncApi.DeleteSync(image.PresentSync);
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
                    uint deletedFramebuffer = image.Framebuffer;
                    gl.DeleteFramebuffer(deletedFramebuffer);
                    image.Framebuffer = 0;
                    _device.SwapChainTestHooks?.AfterFramebufferDeleted?.Invoke(deletedFramebuffer);
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>(2)).Add(exception);
                }
            }

            image.Acquired = false;
        }

        if (failures is { Count: > 0 })
            throw new AggregateException("One or more OpenGL swap-chain image resources could not be released.", failures);
    }

    /// <summary>Releases owner references; actual framebuffer, sync, and texture teardown is performed under the current GL context.</summary>
    protected override void DisposeCore()
    {
        lock (_resizeGate)
        {
        List<Exception>? failures = null;
        try
        {
            // Enqueue framebuffer/sync deletion before dropping texture owner references.
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
            throw new AggregateException("OpenGL swap-chain disposal encountered one or more cleanup failures.", failures);
        }
    }

    private static void DisposeImageTextures(
        OpenGLSwapChainImage[] images,
        ref List<Exception>? failures)
    {
        for (int i = 0; i < images.Length; i++)
        {
            OpenGLSwapChainImage? image = images[i];
            if (image is null)
                continue;

            // A deferred or failed framebuffer/sync release must complete before the
            // attached texture owner reference is dropped.
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

    /// <summary>Deletes per-image framebuffers and presentation syncs while the owning context is current.</summary>
    /// <param name="gl">Current GL dispatch object used for native deletion.</param>
    public override void ReleaseNative(GL gl)
    {
        if (Volatile.Read(ref _nativeReleased) != 0)
            return;

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
            throw new AggregateException("OpenGL swap-chain native cleanup was incomplete and may be retried.", failures);

        Volatile.Write(ref _nativeReleased, 1);
    }

}
