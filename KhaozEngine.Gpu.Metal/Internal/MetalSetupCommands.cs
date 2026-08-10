using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>Where a device-level <c>UpdateTexture</c> is writing, as plain numbers.</summary>
    /// <param name="MipLevel">Destination mip level.</param>
    /// <param name="ArrayLayer">Destination array layer.</param>
    /// <param name="X">Left edge of the written rectangle.</param>
    /// <param name="Y">Top edge.</param>
    /// <param name="Width">Rectangle width in texels.</param>
    /// <param name="Height">Rectangle height in texels.</param>
    internal readonly record struct MetalTextureUpload(
        uint MipLevel, uint ArrayLayer, uint X, uint Y, uint Width, uint Height);

    /// <summary>
    /// THE DEVICE-OWNED SETUP COMMAND BUFFER of decision M-M9, and its own short lock (M-W8). Section 9.3.
    ///
    /// <para><b>WHAT IT REMOVES IS A QUEUE SUBMIT PER CALL.</b>
    /// <c>MTLGraphicsDevice.UpdateTextureCore</c> on a NON-staging texture creates a staging texture, creates a
    /// whole <c>CommandList</c>, records one copy, calls <c>SubmitCommands</c> and then disposes both. Every
    /// device-level texture upload is therefore its own queue submission. Here they accumulate into ONE command
    /// buffer that is committed once.</para>
    ///
    /// <para><b>TEXTURE CREATION IS NOT ON THIS PATH AT ALL, which is where Metal differs from the Vulkan
    /// sibling.</b> V-M10 exists mostly because <c>VkTexture</c>'s constructor clears and transitions, each with
    /// its own <c>vkQueueSubmit</c>, so a scene load was two hundred submissions before a frame was drawn. Metal
    /// has no layout to transition and the incumbent does not clear at creation, so M-M9 says texture creation
    /// issues no command buffer and this type only ever sees uploads. That is why it has no <c>Prepare</c>
    /// member: there is nothing to prepare.</para>
    ///
    /// <para><b>THE FLUSH IS LAZY, AT THE NEXT SUBMIT OR AT ANY DEVICE-LEVEL READ, AND THE READ HALF IS WHAT MAKES
    /// THE CLAIM TRUE WITHOUT A HOLE.</b> A texture uploaded and immediately read back must see the uploaded
    /// bytes, and a design that only flushed at the next submit would leave that case reading memory nothing had
    /// written yet. So both <c>Map</c> overloads and <c>WaitForIdle</c> flush first, and the command-list row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573) adds the third site by flushing at the top of
    /// <c>IGpuDevice.Submit</c>.</para>
    ///
    /// <para><b>THE LOCK IS SHORT AND IT IS NOT NESTED INSIDE ANY OTHER.</b> An <c>MTLCommandBuffer</c> and its
    /// encoders are NOT thread-safe, so two threads uploading two textures may not append to one setup buffer at
    /// once, and creation stays free-threaded everywhere else. Nothing here takes a second lock, and the
    /// obligation that leaves for the command-list row is stated once, in <see cref="Flush"/>.</para>
    ///
    /// <para><b>THE STAGING BUFFERS ARE ONE PER UPLOAD AND THE BATCH OWNS THEM.</b> M-M8's pooled arena is the
    /// RECORD-TIME path (a per-list arena, sub-allocated and recycled at slot retirement) and belongs to the ring
    /// row, https://github.com/APKiwiOrg/KhaozEngine/issues/574. The device-level path is the incumbent's own
    /// one-allocation-per-call shape, which M-M9 does not ask to change, and the allocation it replaces was a
    /// whole staging TEXTURE. They are released after the commit returns rather than after the encode, which is
    /// belt and braces: an <c>MTLCommandBuffer</c> retains everything its encoders reference from the moment they
    /// reference it (M-H3), so either point is safe, and releasing after the commit does not depend on that
    /// reading being exactly right.</para>
    /// </summary>
    internal sealed class MetalSetupCommands : IDisposable
    {
        // The short lock (M-W8). Held for an append or a flush, and never across anything that blocks: the flush
        // COMMITS and does not wait, because the wait belongs to whichever drain the caller was going to do
        // anyway.
        readonly object _gate = new();

        readonly List<MTLBuffer> _staging = [];
        readonly MTLCommandQueue _queue;
        readonly IMetalDeviceLiveness _liveness;

        MTLCommandBuffer _buffer;
        bool _open;
        bool _disposed;

        /// <param name="queue">The device's one queue (M-N2), which every setup batch is committed to.</param>
        /// <param name="liveness">The device's liveness token: after death nothing is recorded and nothing is
        /// committed, which is the posture every path in this package takes.</param>
        internal MetalSetupCommands(MTLCommandQueue queue, IMetalDeviceLiveness liveness)
        {
            ArgumentNullException.ThrowIfNull(liveness);

            _queue = queue;
            _liveness = liveness;
        }

        /// <summary>Whether a batch is open and uncommitted. What the flush sites test before paying for a
        /// lock.</summary>
        internal bool HasPendingWork
        {
            get { lock (_gate) return _open; }
        }

        /// <summary>How many batches this buffer has committed. A reading rather than a gate: M-M9's whole claim
        /// is that this number stays below the upload count, and the incumbent's equivalent is exactly one per
        /// upload.</summary>
        internal int FlushCount { get; private set; }

        /// <summary>How many uploads have been appended since construction, across every batch. Paired with
        /// <see cref="FlushCount"/> it is the ratio M-M9 is about.</summary>
        internal int AppendCount { get; private set; }

        /// <summary>
        /// Append a device-level texture upload: stage the tightly packed payload into a Shared buffer and record
        /// one blit into the destination texture's subresource.
        /// <para>
        /// THE PAYLOAD IS TIGHTLY PACKED, which is what the seam's <c>byte[]</c> overloads document, and a short
        /// array is refused by name rather than read past. The incumbent computes the same source row pitch
        /// (<c>FormatHelpers.GetRowPitch(width, format)</c>) and copies through a staging TEXTURE whose mip 0
        /// happens to have exactly that pitch, so the number is the same one arrived at by a shorter road.
        /// </para>
        /// <para>
        /// THE POOL IS OPENED HERE (M-N5) and it is not decoration: this body reaches <c>-commandBuffer</c>,
        /// <c>-blitCommandEncoder</c> and <c>-newBufferWithLength:options:</c>, and the first two are
        /// AUTORELEASED. A device-level upload happens on whatever thread a consumer loads content on, which is
        /// exactly the thread whose implicit pool drains next never.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Upload(MTLDevice device, MetalTexture destination, in MetalTextureUpload upload,
            ReadOnlySpan<byte> data)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            ArgumentNullException.ThrowIfNull(destination);
            ObjectDisposedException.ThrowIf(_disposed, this);

            ulong required = MetalStagingLayout.RequiredUploadBytes(upload.Width, upload.Height,
                destination.Format);
            if (required == 0) return;

            if ((ulong)data.Length < required)
            {
                throw new ArgumentException(
                    "A native Metal texture upload of "
                    + upload.Width.ToString(CultureInfo.InvariantCulture)
                    + " by "
                    + upload.Height.ToString(CultureInfo.InvariantCulture)
                    + " texels in "
                    + destination.Format
                    + " needs "
                    + required.ToString(CultureInfo.InvariantCulture)
                    + " tightly packed bytes and was given "
                    + data.Length.ToString(CultureInfo.InvariantCulture)
                    + ". The seam's byte[] overloads carry the region's rows with no padding between them, so a "
                    + "short array would be read past its end.", nameof(data));
            }

            lock (_gate)
            {
                if (_liveness.IsDead) return;

                EnsureOpen();
                if (!_open) return;

                MTLBuffer staged = Stage(device, data[..(int)required]);
                _staging.Add(staged);

                MTLBlitCommandEncoder encoder = _buffer.BlitCommandEncoder();
                encoder.CopyFromBufferToTexture(
                    staged,
                    0,
                    (nuint)MetalStagingLayout.RowPitch(upload.Width, destination.Format),
                    // ZERO for a 2D texture, which is what MTLCommandList.CopyTextureCore passes for anything
                    // that is not a 3D texture, and this seam has no 3D texture.
                    0,
                    new MTLSize(upload.Width, upload.Height, 1),
                    destination.Handle,
                    upload.ArrayLayer,
                    upload.MipLevel,
                    new MTLOrigin(upload.X, upload.Y, 0));
                encoder.EndEncoding();

                AppendCount++;
            }
        }

        /// <summary>
        /// Commit the open batch. A no-op with nothing open, which is the common case at every frame boundary
        /// after the first load.
        ///
        /// <para><b>IT COMMITS AND DOES NOT WAIT.</b> The caller that asked for a flush is a caller who was about
        /// to submit or about to drain, and a wait here would be a second one. <c>Map</c> drains AFTER flushing
        /// (M-C6), which is what makes an upload-then-read-back visible.</para>
        ///
        /// <para><b>THE OBLIGATION THIS LEAVES FOR THE COMMAND-LIST ROW</b>
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573): <c>IGpuDevice.Submit</c> must call this BEFORE
        /// taking its own submit lock, not inside it. Two sequential acquisitions rather than a nested pair is
        /// what keeps this lock and that one free of an ordering rule, and calling it first is what puts the
        /// uploads ahead of the frame that reads them in the queue's enqueue order.</para>
        /// </summary>
        internal void Flush()
        {
            // NO ObjectDisposedException. A flush after disposal is a teardown-order straggler rather than a
            // defect, which is the posture every Dispose on this backend takes.
            if (_disposed) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            FlushOnMacOs();
        }

        /// <summary>
        /// Abandon any open batch and release the staging buffers. Called by the device's teardown, after its
        /// drain and before the queue and device are released.
        /// <para>
        /// AN OPEN BATCH IS DISCARDED RATHER THAN COMMITTED. Teardown has already drained, so committing here
        /// would mean waiting again, and the textures that batch was filling are being destroyed in the same
        /// breath. The command buffer itself is autoreleased and needs nothing.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            if (KhaozEngineMetal.IsPlatformSupported) DisposeOnMacOs();

            _disposed = true;
            _open = false;
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void FlushOnMacOs()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            lock (_gate)
            {
                if (!_open) return;

                _open = false;

                // A dead device abandons the batch: committing to a queue whose device has gone is a call into
                // an object the driver has already given up on.
                if (!_liveness.IsDead)
                {
                    _buffer.Commit();
                    FlushCount++;
                }

                // The +1 EnsureOpen took. The driver holds its own reference to a committed buffer until it
                // completes, so this releases the holder's claim rather than the buffer.
                _buffer.Release();
                _buffer = default;
                ReleaseStaging();
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void DisposeOnMacOs()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            lock (_gate)
            {
                if (_disposed) return;

                // An abandoned batch still owns the +1 from EnsureOpen, and an uncommitted command buffer is an
                // ordinary object nothing else references. On a DEAD device nothing is released at all, which is
                // the posture every wrapper in this package takes: the driver has given up on the work and a
                // release is a call into an object it may already have torn down.
                if (!_liveness.IsDead)
                {
                    if (_open) _buffer.Release();
                    ReleaseStaging();
                }
                else
                {
                    _staging.Clear();
                }

                _buffer = default;
                _open = false;
            }
        }

        // Called with the gate held and inside a pool. Opens a batch on a fresh command buffer, because an
        // MTLCommandBuffer is single-use and there is no reset, no pool object and no allocator to choose between
        // (M-R2).
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void EnsureOpen()
        {
            if (_open) return;

            _buffer = _queue.CommandBuffer();
            if (_buffer.IsNull) return;

            // RETAINED, because this buffer outlives the pool the queue handed it out in: the whole point of
            // M-M9 is that uploads from separate calls share one batch, and the pop at the end of THIS call
            // would otherwise free it under the next append. Released once at the commit or at teardown.
            _buffer.Retain();

            // The completion handler's only job is M-G4's error latch (M-F2), and a setup batch can fail exactly
            // as a frame can. It is inert until the device registers its queue with the handler, which is the
            // command-list row's wiring.
            _ = MetalCompletionHandler.AttachTo(_buffer.Handle);
            _open = true;
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        MTLBuffer Stage(MTLDevice device, ReadOnlySpan<byte> data)
        {
            MTLBuffer staged = device.NewBuffer((nuint)data.Length, MTLResourceOptions.SharedDefaultCache);
            if (staged.IsNull)
            {
                throw new InvalidOperationException(
                    "The native Metal device would not allocate a " + data.Length
                    + "-byte Shared staging buffer for a device-level texture upload.");
            }

            IntPtr contents = staged.Contents();
            if (contents == IntPtr.Zero)
            {
                staged.Release();
                throw new InvalidOperationException(
                    "A Shared MTLBuffer staging a texture upload answered a null -contents pointer.");
            }

            unsafe { data.CopyTo(new Span<byte>((byte*)contents, data.Length)); }
            return staged;
        }

        // Called with the gate held and inside a pool. After the commit, so the command buffer has certainly
        // retained everything it references.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReleaseStaging()
        {
            foreach (MTLBuffer staged in _staging) staged.Release();
            _staging.Clear();
        }
    }
}
