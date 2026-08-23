using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE REAL <see cref="IMetalSwapchainApi"/>: six members, each one Objective-C message or a short fixed
    /// sequence of them, with NO decisions in it at all. Everything that decides anything lives above this line in
    /// <see cref="MetalPresentBoundary"/> and <see cref="MetalSwapchainPolicy"/>, where a headless leg can drive
    /// it (MM7).
    ///
    /// <para><b>THE LAYER IS OWNED, AND THAT IS ONE DELIBERATE DIVERGENCE FROM THE INCUMBENT.</b>
    /// <c>MTLSwapchain.Dispose</c> releases <c>_metalLayer</c> unconditionally, which is balanced on the CREATE
    /// path (its <c>alloc</c>/<c>init</c> +1) and an OVER-RELEASE on the ADOPT path, where the layer belongs to
    /// the host view and the swapchain never retained it. M-W1 reproduces the CONFIGURATION field for field, and a
    /// reference-count bug is not part of a configuration: <see cref="MetalLayerHost"/> hands this type a layer it
    /// already holds exactly one reference to on both paths, so the release below is balanced either way. The
    /// incumbent's version is a crash on teardown for a host view that was already layer-backed by a
    /// <c>CAMetalLayer</c>, which is a shape nothing in this fleet produces today and which a consumer embedding
    /// the engine in an existing Cocoa app would produce immediately.</para>
    ///
    /// <para><b>THE PRESENT COMMAND BUFFER GETS THE COMPLETION HANDLER LIKE EVERY OTHER COMMITTED BUFFER
    /// (M-G4).</b> The design says <c>status</c> and <c>error</c> are read at completion in EVERY configuration,
    /// and this buffer is a committed buffer: a present that failed because the device was revoked is exactly the
    /// class of failure the latch exists for, and it would otherwise be the one committed buffer in the backend
    /// nothing reads. It still signals NO timeline value, which is why teardown drains the QUEUE rather than the
    /// timeline (<see cref="MetalQueueDrain"/>) and why row 7 kept that arm for this row.</para>
    ///
    /// <para><b>EVERY BODY OPENS ITS OWN AUTORELEASE POOL (M-N5).</b> <c>-nextDrawable</c> and
    /// <c>-commandBuffer</c> both hand back autoreleased objects and both are called once per frame forever, which
    /// is the exact shape the rule exists for.</para>
    ///
    /// <para><b>THE PRESENT IS TWO MEMBERS BECAUSE ONE OF THEM BLOCKS.</b> <c>-commandBuffer</c> blocks at the
    /// queue's own maximum of uncommitted buffers, and the caller holds the submit lock across the present, so
    /// <see cref="AcquirePresentBuffer"/> is taken FIRST and outside that lock. Folding the two together would
    /// put the one blocking call in this type inside the lock every commit needs to release one, which is a
    /// deadlock rather than a stall. See <see cref="MetalPresentBoundary"/> for the ordering.</para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class MetalSwapchainApi : IMetalSwapchainApi
    {
        readonly CAMetalLayer _layer;
        readonly MTLDevice _device;
        readonly IMetalCommandBufferSource _buffers;

        /// <param name="layer">The layer, at +1 held by this object. <see cref="MetalLayerHost"/> is what makes
        /// that true on both the adopt and the create path.</param>
        /// <param name="device">The device the layer vends drawables for.</param>
        /// <param name="buffers">The device's command-buffer source, for M-W6's present buffer.</param>
        internal MetalSwapchainApi(CAMetalLayer layer, MTLDevice device, IMetalCommandBufferSource buffers)
        {
            ArgumentNullException.ThrowIfNull(buffers);
            _layer = layer;
            _device = device;
            _buffers = buffers;
        }

        /// <summary>The layer, for the <c>[GpuFact]</c> rows that read the configuration back off it by
        /// value.</summary>
        internal CAMetalLayer Layer => _layer;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Configure(MetalDrawableSize size, bool colourSrgb, bool syncToVerticalBlank,
            int maximumDrawableCount)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            // THE INCUMBENT'S ORDER AND THE INCUMBENT'S FIELDS (M-W1), plus M-W4's one addition at the end.
            _layer.SetDevice(_device.Handle);
            _layer.SetPixelFormat(MetalSwapchainPolicy.LayerPixelFormat(colourSrgb));
            _layer.SetFramebufferOnly(true);
            _layer.SetDrawableSize(size.ToCGSize());

            // UNCONDITIONALLY (M-W2), where the incumbent wrote it only inside three values of an enum
            // deprecated since macOS 10.15.
            _layer.SetDisplaySyncEnabled(syncToVerticalBlank);

            // M-W4, and the one consumer of KE_METAL_FRAMES_IN_FLIGHT that was not live until this row: the depth
            // of the drawable queue and the depth of the uniform ring are one number.
            _layer.SetMaximumDrawableCount(maximumDrawableCount);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetDrawableSize(MetalDrawableSize size)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            _layer.SetDrawableSize(size.ToCGSize());
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetDisplaySyncEnabled(bool enabled)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            _layer.SetDisplaySyncEnabled(enabled);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public MetalAcquiredDrawable NextDrawable()
        {
            // ITS OWN POOL, and it is load-bearing rather than habitual: -nextDrawable autoreleases, so without
            // one the drawable would live until the calling thread's implicit pool drained, which under a frame
            // loop is never, and the layer would run out of drawables and block forever.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            CAMetalDrawable drawable = _layer.NextDrawable();
            if (drawable.IsNull) return default;

            // RETAINED BEFORE THE POOL POPS, which is what makes the drawable and its texture live across the
            // whole of the next frame's recording.
            drawable.Retain();
            return new MetalAcquiredDrawable(drawable.Handle, drawable.Texture().Handle);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReleaseDrawable(IntPtr drawable) => new CAMetalDrawable(drawable).Release();

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr AcquirePresentBuffer()
        {
            // ITS OWN POOL, because -commandBuffer autoreleases and the source retains what it hands back. The
            // pool pops here and the retain is what carries the buffer to the present below.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            // A QUEUE THAT WILL NOT MAKE A BUFFER IS A DEVICE ALREADY IN TROUBLE, and the caller skipping the
            // present is the honest answer rather than throwing out of a frame loop: the same condition is what
            // MetalQueueDrain reports as completed, and whatever went wrong has already been latched by the
            // buffer that saw it.
            return _buffers.Acquire();
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void PresentDrawable(IntPtr commandBuffer, IntPtr drawable)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            try
            {
                // BEFORE THE COMMIT, for the reason the submit path gives: Metal refuses a handler added to a
                // buffer that has already been committed.
                MetalCompletionHandler.AttachTo(commandBuffer);

                var buffer = new MTLCommandBuffer(commandBuffer);
                buffer.PresentDrawable(drawable);
                buffer.Commit();
            }
            finally
            {
                // The release of the retain the acquire took. A committed buffer is retained by the queue until
                // it completes, so this is never the last reference to one the GPU is running.
                _buffers.Release(commandBuffer);
            }
        }

        /// <inheritdoc/>
        /// <remarks>Releases the one reference this object holds to the layer. See the type remarks for why that
        /// is balanced on both the adopt path and the create path here and is not on the incumbent.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose() => _layer.Release();
    }
}
