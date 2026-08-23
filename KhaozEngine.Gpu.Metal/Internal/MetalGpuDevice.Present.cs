using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SWAPCHAIN SURFACE OF THE NATIVE DEVICE: the stable framebuffer identity, vsync, the queued resize and
    /// <see cref="Present"/> as the frame boundary. Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581), split from the seam surface the same way the
    /// creation and resource halves are, because a device that must stay under the file-size cap has room for
    /// none of them together. The machinery is in <see cref="MetalPresentBoundary"/>: this partial is the
    /// seam-facing shell over it plus the two things only the device can supply, the orphan target's creation call
    /// and the drain.
    ///
    /// <para><b>EVERY MEMBER HERE IS NULL-SAFE ON A HEADLESS DEVICE, and that is a reading rather than a
    /// guard.</b> A headless device has no swapchain by definition, so it has no framebuffer to hand out, nothing
    /// to resize, and no frame boundary at all. Both siblings landed the same shape. What is NOT reproduced from
    /// them is a throw: the incumbent's <c>SwapBuffers</c> on a device with no main swapchain is a silent no-op,
    /// and a golden run that started throwing at a stray <c>Present</c> would be this backend inventing a
    /// contract.</para>
    ///
    /// <para><b>THE RING IS NOT ROTATED HERE, AND THAT IS ROW 8's HANDOFF HONOURED.</b> Both sibling backends
    /// call their ring allocator's frame boundary from <c>Present</c>, and both can, because each carries a second
    /// per-frame index that advances at the list's own <c>Begin</c>. M-R2 removes that second index here, so the
    /// rotation depth exists for the uniform ring and the staging arena and nothing else, and both rotate at
    /// <c>MetalCommandList.Begin</c>. A second advance at this boundary would rotate for something that is not a
    /// recording at all: segments would be skipped, the gate would wait on the wrong value, and it would present
    /// as another recording's uniforms being read, intermittently.</para>
    /// </summary>
    internal sealed partial class MetalGpuDevice
    {
        // M-W4's ACCUMULATOR, ON THE DEVICE RATHER THAN ON THE BOUNDARY, so the counter fill has something to
        // read on a headless device without asking whether a swapchain exists, and so the pair survives for the
        // life of the device the way the drain and backpressure pairs do.
        readonly WaitAccumulator _acquireWaits = new();

        MetalPresentBoundary? _present;

        /// <inheritdoc/>
        /// <remarks>
        /// THE SAME OBJECT FOR THE WHOLE LIFE OF A WINDOWED DEVICE (M-W7), so it may be cached by anything: an
        /// acquire and a resize both change what it points at and never its identity. On this API that is free
        /// rather than built, because there is no view object per backbuffer image to invalidate.
        /// <para>
        /// NULL ON A HEADLESS DEVICE, which is correct rather than unbuilt: the headless path attaches no layer at
        /// all, which is what lets the golden suite run with no window server.
        /// </para>
        /// </remarks>
        public IGpuFramebuffer? SwapchainFramebuffer => _present?.Framebuffer;

        /// <summary>The present boundary, for the rows that read its counters and for the <c>[GpuFact]</c> that
        /// drives a real layer through it. Null on a headless device.</summary>
        internal MetalPresentBoundary? PresentBoundary => _present;

        /// <inheritdoc/>
        /// <remarks>
        /// A CHANGE QUEUES AND APPLIES AT THE NEXT PRESENT BOUNDARY (M-W7), like a resize, because both are
        /// property writes on a layer the submit thread owns. That is a smaller change than the Vulkan sibling's
        /// (which cannot alter a present mode in place at all and recreates the whole swapchain) and a larger one
        /// than Direct3D 11's (where vsync is an argument of the present call and a setter changes only an
        /// interval).
        /// <para>
        /// THE WRITE IS UNCONDITIONAL WHEN IT LANDS (M-W2). The incumbent's write sat inside an equality against
        /// three values of <c>MTLFeatureSet</c>, deprecated since macOS 10.15, so on a machine outside that set a
        /// vsync toggle silently does nothing. Reproducing a fragility whose failure is silent is not parity.
        /// </para>
        /// <para>
        /// On a HEADLESS device it is a plain backing value, which is what the seam asks for: there is no layer to
        /// reconfigure.
        /// </para>
        /// </remarks>
        public bool SyncToVerticalBlank
        {
            get => _present?.SyncToVerticalBlank ?? _syncToVerticalBlank;
            set
            {
                _syncToVerticalBlank = value;
                if (_present is not null) _present.SyncToVerticalBlank = value;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// QUEUED AND APPLIED AT THE NEXT PRESENT BOUNDARY (M-W7), never here. This stores a number and returns:
        /// no lock, no native call, nothing that can block, so a window callback arriving on any thread while the
        /// submit thread is inside a commit is safe.
        /// <para>
        /// THE INCUMBENT APPLIES IT INLINE ON THE CALLING THREAD, recreating its depth texture (releasing the one
        /// in-flight frames may still be reading) and taking a fresh drawable, with no drain anywhere. The Silk
        /// framebuffer-resize callback fires on the render thread today, so nothing observable changes in the
        /// shipped loop and the contract hardens against a consumer that does otherwise.
        /// </para>
        /// <para>
        /// A ZERO IN EITHER DIMENSION IS SAFE. A minimised window reports (0, 0) through that same callback, the
        /// clamp turns it into one by one, and a layer that then vends no drawable puts the frame on the orphan
        /// target with its present skipped until the window comes back.
        /// </para>
        /// <para>
        /// A no-op on a headless device. Silent rather than a throw, matching the incumbent, because teardown
        /// order is a consumer's business and a window can report a size change while it is being destroyed.
        /// </para>
        /// </remarks>
        public void ResizeSwapchain(uint w, uint h) => _present?.QueueResize(w, h);

        /// <inheritdoc/>
        /// <remarks>
        /// THE FRAME BOUNDARY (M-W4 to M-W7). It presents the drawable the frame just rendered into, applies any
        /// queued resize or vsync change after a drain, services an armed frame capture, and acquires the drawable
        /// the NEXT frame will render into. <see cref="MetalPresentBoundary"/> carries the order and the reasons.
        /// <para>
        /// IT NEVER THROWS AND NEVER REPORTS FAILURE UPWARD. A nil drawable is M-W5's orphan path, and a dead
        /// device returns rather than presenting into a queue nothing can advance, which is the same posture
        /// <c>Submit</c> and <see cref="WaitForIdle"/> already take.
        /// </para>
        /// <para>
        /// A HEADLESS DEVICE DOES NOTHING AT ALL, including counting: <c>FramesBegun</c> is the present boundary's
        /// number and a device with no boundary never opened a frame at this seam. That is the position the
        /// counter fill already documented before this row existed.
        /// </para>
        /// </remarks>
        public void Present()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            _present?.Present();
        }

        /// <summary>
        /// Build this device's swapchain over <paramref name="host"/>, which is the whole of what makes a device
        /// windowed. Called from creation, inside the same try that unwinds a failed construction.
        /// </summary>
        /// <param name="host">The resolved layer and its size. Ownership of the layer transfers to the api this
        /// creates, which releases it at teardown.</param>
        /// <param name="syncToVerticalBlank">The consumer's requested vsync, written unconditionally (M-W2).</param>
        /// <param name="framesInFlight">MM4's depth, which becomes <c>maximumDrawableCount</c> (M-W4).</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void AttachSwapchain(in MetalSwapchainHost host, bool syncToVerticalBlank, int framesInFlight)
        {
            _syncToVerticalBlank = syncToVerticalBlank;

            _present = new MetalPresentBoundary(
                new MetalSwapchainApi(host.Layer, _device, _commandBuffers),
                new MetalOrphanTarget(CreateOrphanTarget),
                _uncommitted,
                _acquireWaits,
                _submitLock,

                // THE TIMELINE'S DRAIN AND NOT THE PUBLIC WaitForIdle, and the difference is a LOCK ORDER rather
                // than a cost. WaitForIdle flushes the device's setup batch first, which takes the SETUP lock,
                // and the setup lock is taken BEFORE the submit lock and never after it (M-W8). The apply runs
                // holding the submit lock, so calling the public member there would invert the one ordering rule
                // this backend has between two locks. M-W7 asks for the timeline to be drained and that is
                // exactly what this is, counted into DrainCount and DrainMs like every other drain.
                _timeline.WaitForIdle,

                // ROW 16's BOUNDARY CALL, unconditional. Nothing armed is one flag read and a return.
                ServiceFrameCaptureAtPresentBoundary,
                host.Size,
                MetalSwapchainPolicy.ColourSrgbRequested,
                syncToVerticalBlank,
                framesInFlight);
        }

        // M-W5's ORPHAN TARGET, created through this device's own resource path so it is an ordinary Private
        // MTLTexture with a render-target usage bit and is released like any other. Handed to MetalOrphanTarget
        // as a delegate rather than reached through a field, which keeps MetalResourceFactory out of the
        // boundary's field graph: M-M10's walk asserts no view factory is reachable from the recording type, and
        // a resource path hanging off the present boundary is one edge away from being reachable from one.
        //
        // THE FORMAT TRAVELS IN rather than being read off the swapchain here, because the orphan has to match
        // the framebuffer's published Outputs or every pipeline bound while it is up is validated against the
        // wrong output description on its first draw.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        MetalTexture CreateOrphanTarget(MetalDrawableSize size, GpuPixelFormat format)
            => MetalTexture.Create(_device, _liveness,
                GpuTextureDescription.Texture2D(size.Width, size.Height, format,
                    GpuTextureUsage.RenderTarget));
    }
}
