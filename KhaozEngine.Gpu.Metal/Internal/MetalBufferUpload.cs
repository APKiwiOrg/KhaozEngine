using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE RECORD-TIME UPLOAD DECISION, AND IT IS THE WHOLE OF ROW 8's CLAIM IN ONE FORK (2.1, M-M3, M-M8).
    ///
    /// <para><b>ONE SEAM MEMBER, TWO COMPLETELY DIFFERENT COSTS.</b> A ring-backed uniform buffer's write is
    /// <c>memcpy(contents + frameBase + offset, data, n)</c> and NOTHING ELSE: no staging buffer, no blit, no
    /// allocation, no release, and above all NO ENCODER. Every other buffer stages into the list's own arena and
    /// pays a blit encoder, which on this API means ending the render encoder and therefore discarding the bound
    /// pipeline, every argument-table entry, the viewport, the scissor and every vertex stream (M-R4), so the
    /// next draw pays a full re-activation for a copy of a few bytes. The incumbent routes EVERY record-time
    /// <c>UpdateBuffer</c> down the second path, uniform buffers included, and the shipped renderers write a
    /// uniform buffer per pass and often per draw. The saved work is not the copy. It is the encoder.</para>
    ///
    /// <para><b>IT IS A TYPE OF ITS OWN SO THE CLAIM CAN BE ASSERTED WITHOUT A DEVICE, and the claim is a
    /// NEGATIVE, which is exactly the kind a golden cannot see.</b> A backend that sent uniform writes down the
    /// staging path would render identical pixels and cost a full state re-activation per write. Everything this
    /// type needs from an <see cref="IGpuBuffer"/> is three facts (its ring or the absence of one, its native
    /// handle, its logical size), none of which needs an <c>MTLDevice</c> to state, so
    /// <c>MetalBufferUploadTests</c> drives it over a real <see cref="MetalEncoderScope"/> on a fake sink and
    /// counts the boundaries through the very seam M-T2 freezes its budget over.</para>
    /// </summary>
    internal static class MetalBufferUpload
    {
        /// <summary>
        /// Record one <c>UpdateBuffer</c>. <paramref name="ring"/> decides which of the two paths it takes and
        /// nothing else does.
        /// </summary>
        /// <param name="ring">The destination's uniform ring, or null for every buffer that is not
        /// <c>UniformBuffer</c> usage (M-M6).</param>
        /// <param name="segment">The ring segment the CALLING RECORDING captured at its <c>Begin</c>, which the
        /// ring path writes into. It travels down from the list rather than being read off the allocator here,
        /// because the allocator's current segment moves whenever any other list begins, and a recording's writes
        /// all belong to one version. Ignored by the staging path, which has no version.</param>
        /// <param name="destination">The destination's <c>MTLBuffer</c> handle, used only by the staging path.
        /// <see cref="IntPtr.Zero"/> for a buffer that has been disposed, which records nothing.</param>
        /// <param name="destinationSizeBytes">Its LOGICAL size, which is what the write is bounded against.
        /// </param>
        /// <param name="offsetBytes">Where in the logical buffer the write lands.</param>
        /// <param name="data">The payload. Empty is a no-op rather than a recorded copy of nothing.</param>
        /// <param name="encoders">The list's encoder scope. The ring path does not touch it AT ALL, which is the
        /// assertion.</param>
        /// <param name="arena">The list's staging arena, for the other path.</param>
        /// <param name="blit">Where the one copy is emitted.</param>
        internal static void Record(MetalUniformRing? ring, int segment, IntPtr destination,
            uint destinationSizeBytes, uint offsetBytes, ReadOnlySpan<byte> data, MetalEncoderScope encoders,
            MetalStagingArena arena, IMetalBlitApi blit)
        {
            ArgumentNullException.ThrowIfNull(encoders);
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(blit);

            if (data.IsEmpty) return;

            // THE RING PATH. The render encoder open across this call stays open, keeps its pipeline and keeps
            // every binding, because nothing here asks the scope for anything.
            if (ring is not null)
            {
                ring.Write(segment, offsetBytes, data);
                return;
            }

            StageAndCopy(destination, destinationSizeBytes, offsetBytes, data, encoders, arena, blit);
        }

        /// <summary>The payload size a copy of <paramref name="lengthBytes"/> bytes actually moves, and the
        /// refusal that goes with it. Split out so a test can pin the pad arithmetic on its own.</summary>
        internal static uint CopyBytesFor(uint offsetBytes, uint lengthBytes, uint destinationSizeBytes)
        {
            MetalBufferPolicy.RequireWriteFits(offsetBytes, lengthBytes, destinationSizeBytes);
            RequireCopyAlignedOffset(offsetBytes, lengthBytes);

            return MetalStagingArena.AlignedCopyBytes(lengthBytes);
        }

        // THE BULK PATH (M-M8): lease from the list's arena, copy the payload in, and encode ONE blit. Bulk
        // payloads are rare relative to the uniform sites and they genuinely need the copy command, so what this
        // removes is the incumbent's per-call MTLBuffer allocate-and-release rather than the encoder boundary.
        static void StageAndCopy(IntPtr destination, uint destinationSizeBytes, uint offsetBytes,
            ReadOnlySpan<byte> data, MetalEncoderScope encoders, MetalStagingArena arena, IMetalBlitApi blit)
        {
            // THE SIZE PAD IS THE INCUMBENT'S OWN, reproduced rather than improved (section 9.3): the copy moves
            // the payload rounded up to four bytes, and MetalBufferPolicy.AllocationBytes is what makes those
            // extra bytes land inside the destination's allocation rather than past its end. The proof is
            // arithmetic: the caller's offset is a multiple of four and offset + length is inside the logical
            // size, so offset + align4(length) is inside align4(size), which is what was allocated.
            uint copyBytes = CopyBytesFor(offsetBytes, (uint)data.Length, destinationSizeBytes);

            // A NIL DESTINATION RECORDS NOTHING, in the same shape the nil encoder below is handled and for a
            // reason that arrives at this method the same way: MetalBuffer.Handle answers nil once the buffer has
            // been disposed, deliberately, so a record-time write to a disposed buffer reaches here with a zero.
            // Encoding the copy anyway would name a nil MTLBuffer in a copy command, which the driver refuses with
            // an assertion that aborts the process rather than anything this backend can report, and it would take
            // a staging lease and an encoder boundary on the way. The refusals above still run first, because a
            // write that runs past the end of the buffer is the caller's mistake whether or not it was disposed.
            if (destination == IntPtr.Zero) return;

            MetalStagingLease lease = arena.Take(copyBytes);

            // AN INVALID LEASE IS A DEAD DEVICE, in the same shape the nil encoder below is handled: the arena
            // refuses to reach -newBufferWithLength:options: on a device that has gone, and the record-time write
            // becomes the no-op every other write on a dead device already is rather than a throw from inside a
            // frame that is already failing.
            if (!lease.IsValid) return;

            Span<byte> staged = lease.Span;
            data.CopyTo(staged);

            // THE PAD IS ZEROED rather than left holding whatever the block last carried. Those bytes reach the
            // destination either way, so leaving them stale would make a byte-for-byte readback depend on which
            // upload previously used that block, which is a nondeterminism a golden would find months later and
            // blame on something else. Three bytes at most.
            staged[data.Length..].Clear();

            // THE ENCODER, AND THIS IS THE COST THE RING PATH DOES NOT PAY. Ending a render encoder to open this
            // discards every piece of encoder-scoped state (M-R4), so the next draw re-activates everything.
            IntPtr encoder = encoders.EnsureBlitEncoder();

            // A NIL ENCODER IS NOT AN ERROR TO THROW ON, in the shape MetalEncoderScope already settled: Metal
            // answers nil when the command buffer is in a state it will not encode into, and the scope refuses to
            // adopt one. The lease is left leased, which costs a few staged bytes until the slot recycles and is
            // the right direction, since the alternative is throwing from inside a frame that is already failing.
            if (encoder == IntPtr.Zero) return;

            blit.CopyBufferToBuffer(encoder, lease.Buffer, lease.OffsetBytes, destination, offsetBytes, copyBytes);
        }

        // The destination offset is the CALLER's and this backend cannot align it for them. macOS requires both
        // offsets of copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size: to be multiples of four, and
        // section 9.3's ruling is that the SIZE half is padded while the OFFSET half throws by name: the
        // incumbent routes an unaligned copy through an embedded compute shader and a dedicated pipeline, and
        // shipping a second metallib for a case no consumer produces is the unreachable-code reproduction this
        // design declines. Every record-time UpdateBuffer site in the engine passes 0 or a multiple of the
        // element stride, so nothing legitimate reaches this.
        static void RequireCopyAlignedOffset(uint offsetBytes, uint lengthBytes)
        {
            if (offsetBytes % MetalStagingArena.CopyAlignment == 0) return;

            throw new ArgumentOutOfRangeException(nameof(offsetBytes), offsetBytes,
                "A record-time upload of " + lengthBytes + " bytes to a non-uniform native Metal buffer was "
                + "given a destination offset of " + offsetBytes + ", which is not a multiple of "
                + MetalStagingArena.CopyAlignment + ". The copy this becomes requires that on macOS. The "
                + "incumbent routes the unaligned case through an embedded compute shader and a dedicated "
                + "compute pipeline, which this backend declines to reproduce for a case no shipped call site "
                + "produces (section 9.3). Align the offset, or write the buffer through the device-level "
                + "UpdateBuffer instead, which is a plain copy with no blit behind it.");
        }
    }
}
