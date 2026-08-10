using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <para><b>EVERY NATIVE CALL IS BEHIND <see cref="IMetalSetupNative"/> AND EVERY DECISION IS HERE.</b> This
    /// type holds no Objective-C handle it messages and opens no autorelease pool: what is left in it is which
    /// uploads share a batch, when a batch is committed, what the staging budget does, and what a dead device
    /// releases. All of that runs under a plain <c>[Fact]</c> on a machine with no Metal at all, which is the
    /// split the timeline row already took for <c>MTLSharedEvent</c>.</para>
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
    ///
    /// <para><b>AND THE OPEN BATCH CARRIES A BYTE BUDGET, WHICH IS A SAFETY VALVE RATHER THAN THAT ARENA</b>
    /// (<see cref="DefaultStagingCapBytes"/>). The incumbent holds ONE staging allocation at a time, because it
    /// commits and releases per call. Batching trades that for holding every payload since the last flush, and
    /// the flush sites are all device-level READS, so a caller that only ever uploads never reaches one: peak
    /// footprint becomes the sum of every upload in the load rather than the largest of them.
    /// <c>Scene3D.LoadSplatMaterial</c> is the named case, ten mip-0 uploads with nothing between them, which is
    /// 40 MB of staging for a 1024-square layer set and 160 MB for a 2048-square one, and
    /// <c>Scene3D.LoadTexture</c> on a one-level policy is the unbounded one, since it drains only when it has
    /// mips to generate. So an append that would carry the open batch past the cap commits it first and starts a
    /// new one. That bounds residency and changes nothing about the allocation RATE, which is what the arena is
    /// for and what https://github.com/APKiwiOrg/KhaozEngine/issues/589 still tracks.</para>
    /// </summary>
    internal sealed class MetalSetupCommands : IDisposable
    {
        /// <summary>
        /// How many bytes of staging one open batch may hold before an append commits it and starts another.
        ///
        /// <para><b>64 MB, CHOSEN AGAINST THE CALL SITES RATHER THAN ROUND.</b> The engine's device-level uploads
        /// are <c>Render2DCore</c>'s texture create-and-upload, <c>SpriteFont.Build</c>'s atlas,
        /// <c>Scene3D.LoadTexture</c>, <c>Scene3D.LoadSplatMaterial</c>'s ten layer uploads and
        /// <c>WaterBathymetryMap</c>'s per-revision map. The largest burst with no drain in it is the splat set:
        /// five albedo plus five normal layers at mip 0, which is 40 MB at 1024 square and 160 MB at 2048 square.
        /// A cap has to sit above the first (a common material set should still be ONE batch, which is the whole
        /// of M-M9's claim) and below the second (160 MB of live staging for one material is the residency this
        /// bound exists to refuse). 64 MB is the only order of magnitude that does both, and it costs one extra
        /// commit on the 2048 case.</para>
        ///
        /// <para>It is a CEILING on an open batch and not a budget for the device: a single upload larger than
        /// this still goes through, in a batch of its own, because refusing it would refuse a texture the seam
        /// can legally describe.</para>
        /// </summary>
        internal const ulong DefaultStagingCapBytes = 64UL * 1024 * 1024;

        // The short lock (M-W8). Held for an append or a flush, and never across anything that blocks: the flush
        // COMMITS and does not wait, because the wait belongs to whichever drain the caller was going to do
        // anyway.
        readonly object _gate = new();

        readonly List<MTLBuffer> _staging = [];
        readonly IMetalSetupNative _native;
        readonly IMetalDeviceLiveness _liveness;
        readonly ulong _stagingCap;

        MTLCommandBuffer _buffer;
        MTLCommandBuffer _lastCommitted;
        ulong _stagedBytes;
        bool _open;
        bool _disposed;

        /// <param name="native">The batch's native half: the queue's command buffers, the staging allocations,
        /// the blit encode and the commit.</param>
        /// <param name="liveness">The device's liveness token: after death nothing is recorded and nothing is
        /// committed, which is the posture every path in this package takes.</param>
        internal MetalSetupCommands(IMetalSetupNative native, IMetalDeviceLiveness liveness)
            : this(native, liveness, DefaultStagingCapBytes) { }

        /// <param name="native">See the other constructor.</param>
        /// <param name="liveness">See the other constructor.</param>
        /// <param name="stagingCap">The open batch's staging ceiling. Only a test passes this, so the auto-flush
        /// can be driven with a handful of bytes instead of by allocating the real cap.</param>
        internal MetalSetupCommands(IMetalSetupNative native, IMetalDeviceLiveness liveness, ulong stagingCap)
        {
            ArgumentNullException.ThrowIfNull(native);
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentOutOfRangeException.ThrowIfZero(stagingCap);

            _native = native;
            _liveness = liveness;
            _stagingCap = stagingCap;
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

        /// <summary>How many staging bytes the OPEN batch is holding. Never above
        /// <see cref="DefaultStagingCapBytes"/> once an append has returned, which is what the budget
        /// means.</summary>
        internal ulong StagedBytes
        {
            get { lock (_gate) return _stagedBytes; }
        }

        /// <summary>How many staging buffers the open batch is holding. The residency <see cref="StagedBytes"/>
        /// bounds, counted rather than summed.</summary>
        internal int StagingCount
        {
            get { lock (_gate) return _staging.Count; }
        }

        /// <summary>
        /// The last committed batch's <c>-status</c> and <c>-error</c>, or null when nothing has been committed
        /// on a live device.
        ///
        /// <para><b>THIS IS WHAT MAKES THE GPU TEST AN ASSERTION ABOUT THE BLIT RATHER THAN ABOUT THE PROCESS.</b>
        /// The completion handler attached at <c>BeginBatch</c> is inert until the device registers its queue with
        /// it, which is the command-list row's wiring
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573), so until that lands nothing on the shipping path
        /// reads this batch's outcome at all: a failed setup buffer would be indistinguishable from a completed
        /// one, and a test that only checked the process had not aborted would be checking nothing about the
        /// eleven-argument copy. Reading it here closes that hole for the one path this row owns.</para>
        ///
        /// <para><b>IT IS ONLY MEANINGFUL AFTER A DRAIN.</b> The commit does not wait, so a reading taken
        /// immediately after <see cref="Flush"/> reports <c>Committed</c> rather than <c>Completed</c>. Every
        /// caller that wants an outcome calls it after <c>WaitForIdle</c> or after a <c>Map</c>, both of which
        /// flush and then drain.</para>
        ///
        /// <para>NULL ON A DEAD DEVICE, which is the same posture as every release in this type: the driver has
        /// given up on the work, and messaging a buffer it may already have torn down to ask how it went is the
        /// one call least worth making.</para>
        /// </summary>
        internal MetalCommandBufferFault? LastCommittedFault()
        {
            lock (_gate)
            {
                if (_disposed || _liveness.IsDead || _lastCommitted.IsNull) return null;
                return _native.ReadFault(_lastCommitted);
            }
        }

        /// <summary>
        /// Append a device-level texture upload: stage the tightly packed payload into a Shared buffer and record
        /// one blit into the destination texture's subresource.
        /// <para>
        /// THE PAYLOAD IS TIGHTLY PACKED, which is what the seam's <c>byte[]</c> overloads document, and a short
        /// array is refused by name rather than read past. The incumbent computes the same source row pitch
        /// (<c>FormatHelpers.GetRowPitch(width, format)</c>) and copies through a staging TEXTURE whose mip 0
        /// happens to have exactly that pitch, so the number is the same one arrived at by a shorter road.
        /// </para>
        /// </summary>
        /// <param name="destination">The Private texture being written.</param>
        /// <param name="shape">Its shape, which is what the source row pitch is computed from.</param>
        /// <param name="upload">Where in the destination the payload lands.</param>
        /// <param name="data">The tightly packed payload.</param>
        internal void Upload(MTLTexture destination, in MetalStagingShape shape, in MetalTextureUpload upload,
            ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            ulong required = MetalStagingLayout.RequiredUploadBytes(upload.Width, upload.Height, shape.Format);
            if (required == 0) return;

            // THE REGION, AGAINST THE DESTINATION. The length check below only says the SOURCE is long enough,
            // which says nothing about where the bytes land: an origin plus an extent past the mip's own
            // dimensions is a blit the driver refuses or applies somewhere else.
            MetalStagingLayout.RequireRegionFits(shape, upload.MipLevel, upload.ArrayLayer, upload.X, upload.Y,
                upload.Width, upload.Height);

            if ((ulong)data.Length < required)
            {
                throw new ArgumentException(
                    "A native Metal texture upload of "
                    + upload.Width.ToString(CultureInfo.InvariantCulture)
                    + " by "
                    + upload.Height.ToString(CultureInfo.InvariantCulture)
                    + " texels in "
                    + shape.Format
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

                // THE BUDGET, BEFORE THE BATCH IS CHOSEN. An append that would carry the open batch past the cap
                // commits it first, so the staging this type holds at once is bounded by the cap plus the one
                // payload that crossed it. See DefaultStagingCapBytes for the number and the call sites it was
                // chosen against.
                if (_open && _stagedBytes + required > _stagingCap) CommitOpenBatch();

                EnsureOpen();
                if (!_open) return;

                MTLBuffer staged = _native.Stage(data[..(int)required]);
                _staging.Add(staged);
                _stagedBytes += required;

                _native.Encode(_buffer, staged, destination,
                    MetalStagingLayout.RowPitch(upload.Width, shape.Format), upload);

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

            lock (_gate)
            {
                CommitOpenBatch();
            }
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

            lock (_gate)
            {
                if (_disposed) return;

                // An abandoned batch still owns the +1 from EnsureOpen, and an uncommitted command buffer is an
                // ordinary object nothing else references. On a DEAD device nothing is released at all, which is
                // the posture every wrapper in this package takes: the driver has given up on the work and a
                // release is a call into an object it may already have torn down.
                if (!_liveness.IsDead)
                {
                    if (_open) _native.ReleaseBatch(_buffer);

                    // And the committed batch this type still holds a claim on, which nothing else will release
                    // now that no further commit will happen (see CommitOpenBatch for why it is held at all).
                    if (!_lastCommitted.IsNull) _native.ReleaseBatch(_lastCommitted);
                    ReleaseStaging();
                }
                else
                {
                    _staging.Clear();
                    _stagedBytes = 0;
                }

                _buffer = default;
                _lastCommitted = default;
                _disposed = true;
                _open = false;
            }
        }

        // Called with the gate held. Commits the open batch, hands the +1 EnsureOpen took to _lastCommitted, and
        // releases the staging buffers the batch accumulated.
        void CommitOpenBatch()
        {
            if (!_open) return;

            _open = false;

            // A DEAD DEVICE ABANDONS THE BATCH AND RELEASES NOTHING. Committing to a queue whose device has gone
            // is a call into an object the driver has already given up on, and so is releasing one, which is why
            // this arm drops the managed references instead. That is the posture every wrapper in this package
            // takes and the one Dispose below documents, and this path used to contradict it by releasing the
            // command buffer and the staging buffers anyway.
            if (_liveness.IsDead)
            {
                _buffer = default;
                _staging.Clear();
                _stagedBytes = 0;
                return;
            }

            _native.Commit(_buffer);
            FlushCount++;

            // THE +1 TRAVELS RATHER THAN BEING RELEASED HERE, which is what keeps -status and -error readable
            // after the drain (see LastCommittedFault). The previous batch's claim is dropped as this one takes
            // its place, so exactly one committed buffer is held at a time. The driver keeps its own reference
            // until a buffer completes either way, so holding this one costs a reference and nothing else.
            if (!_lastCommitted.IsNull) _native.ReleaseBatch(_lastCommitted);
            _lastCommitted = _buffer;
            _buffer = default;
            ReleaseStaging();
        }

        // Called with the gate held. Opens a batch on a fresh command buffer, or leaves the batch closed when the
        // queue would not make one, which is a device already in trouble.
        void EnsureOpen()
        {
            if (_open) return;

            _buffer = _native.BeginBatch();
            if (_buffer.IsNull) return;

            _open = true;
        }

        // Called with the gate held. After the commit, so the command buffer has certainly retained everything it
        // references.
        void ReleaseStaging()
        {
            foreach (MTLBuffer staged in _staging) _native.ReleaseStaging(staged);
            _staging.Clear();
            _stagedBytes = 0;
        }
    }
}
