using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE VERTEX-STREAM CACHE, AND THE POINT IS THAT IT IS ACTUALLY MAINTAINED (section 6.3). One record per
    /// stream slot, dirty when the buffer or the offset moved, invalidated wholesale at every encoder boundary,
    /// and flushed as one array call per contiguous run of the vertex stage's buffer table.
    ///
    /// <para><b>THE INCUMBENT HAS THE TRACKING AND NOT THE INVALIDATION, AND IS SAVED BY A SECOND DEFECT.</b>
    /// <c>MTLCommandList.EndCurrentRenderPass</c> sets the pipeline-changed flag, clears the active-set array and
    /// re-marks the viewport and scissor, and does NOT clear <c>_vertexBuffersActive</c>. What stops that being a
    /// corruption is that <c>PreDrawCommand</c>'s vertex-buffer loop issues <c>setVertexBuffer</c> when the flag
    /// is false and never sets it true, so the cache is permanently cold and every stream is re-bound on every
    /// draw. <b>Porting the redundancy tracking without porting the invalidation ships a corruption no golden
    /// would catch</b>, because the goldens do not restart a render pass mid-scene. So the two arrive together
    /// here or neither does, and <see cref="MetalEncoderMark"/> is what makes that structural rather than
    /// remembered.</para>
    ///
    /// <para><b>THE PER-DRAW MARGINAL IS A REGRESSION TARGET RATHER THAN A PARITY TARGET.</b> The incumbent pays
    /// one call per stream per draw unconditionally, so the native marginal is strictly LOWER and M-T2's budget
    /// test freezes the lower number. A future change that reintroduces the unconditional bind is a red test
    /// rather than an invisible cost, which is the whole reason the number is frozen at all.</para>
    ///
    /// <para><b>STREAMS EMIT THROUGH THE SAME ARRAY SETTER THE RESOURCE BUFFERS DO, which is why
    /// <see cref="IMetalEncoderSink"/> has no member of their own.</b> A vertex stream IS a
    /// <c>[[buffer(n)]]</c> binding of the vertex stage, pinned at the TOP of that space by M-B2
    /// (<see cref="MetalVertexStreamIndex"/>) while resource buffers grow from 0 upward. Two dirty streams are
    /// one <c>setVertexBuffers:offsets:withRange:</c> over their contiguous run, exactly as two dirty resource
    /// buffers are, and the two runs cannot overlap because the numberings come from opposite ends and neither
    /// depends on the other's count.</para>
    ///
    /// <para><b>THE OFFSET IS THE CALLER'S AND NOTHING COMPOSES IT.</b> A vertex stream is never ring-backed and
    /// has no set to carry a range, so <c>SetVertexBuffer(slot, buffer, offsetBytes)</c>'s offset is the whole of
    /// it. That is the difference from <see cref="MetalBindRecords"/>, whose every buffer bind composes
    /// <c>frameBase + rangeOffset + callerDynamicOffset</c>.</para>
    ///
    /// <para><b>WHAT ROW 14 ADDS (https://github.com/APKiwiOrg/KhaozEngine/issues/580).</b> The seam members.
    /// <c>IGpuCommandList.SetVertexBuffer</c> resolves its buffer and calls <see cref="Record"/>, and the draw
    /// path calls <see cref="Flush"/> after the resource-set flush and before the draw itself. The index buffer
    /// is not here at all: Metal takes it in the draw call rather than binding it beforehand, so there is no
    /// argument-table entry for a cache to be about.</para>
    /// </summary>
    internal sealed class MetalVertexStreamRecords
    {
        /// <summary>
        /// The highest stream slot a record will grow to cover. One stage's buffer table holds
        /// <see cref="MetalVertexStreamIndex.BufferTableSize"/> entries in total and the resource buffers grow
        /// into the same space from the other end, so a pipeline anywhere near this is already refused at
        /// creation.
        /// </summary>
        internal const uint MaxSlot = 15;

        readonly MetalArgumentBatch _batch = new();

        StreamRecord[] _streams = new StreamRecord[4];
        int _recorded;

        /// <summary>One past the highest stream slot ever recorded, which bounds every walk.</summary>
        internal int RecordedSlotCount => _recorded;

        /// <summary>The <c>MTLBuffer</c> recorded at a stream slot, or <see cref="IntPtr.Zero"/> for one holding
        /// none.</summary>
        internal IntPtr RecordedBuffer(uint slot) => slot < (uint)_recorded ? _streams[slot].Buffer : IntPtr.Zero;

        /// <summary>The byte offset recorded alongside it.</summary>
        internal uint RecordedOffset(uint slot) => slot < (uint)_recorded ? _streams[slot].Offset : 0;

        /// <summary>Whether the next flush owes this stream a bind.</summary>
        internal bool IsDirty(uint slot) => slot < (uint)_recorded && _streams[slot].Dirty;

        /// <summary>Whether this stream's binding is in the argument table of the encoder
        /// <paramref name="epoch"/> names. Exposed so the M-R4 invalidation is asserted rather than inferred from
        /// a call count.</summary>
        internal bool IsEmittedIn(uint slot, ulong epoch)
            => slot < (uint)_recorded && _streams[slot].Emitted.IsValidIn(epoch);

        /// <summary>
        /// RECORD ONLY, marking the stream dirty when the buffer or the offset differs from what is recorded. No
        /// native call and no device contact.
        /// <para>
        /// A MARK IS NEVER LOWERED, for <see cref="MetalBindRecords.Record"/>'s reason: a record that matches
        /// what is already there does not clean a stream that was already owing a bind.
        /// </para>
        /// </summary>
        /// <param name="slot">The seam's vertex-buffer slot.</param>
        /// <param name="buffer">The <c>MTLBuffer</c>, or <see cref="IntPtr.Zero"/> to record that the stream
        /// holds none. Nil is BOUND rather than skipped, unlike a null resource set: a stream is one index rather
        /// than a whole set's worth of them, and writing nil there is how a caller unbinds it.</param>
        /// <param name="offsetBytes">Where in that buffer the stream starts.</param>
        internal void Record(uint slot, IntPtr buffer, uint offsetBytes)
        {
            EnsureSlot(slot);

            ref StreamRecord record = ref _streams[slot];
            if (record.Buffer != buffer || record.Offset != offsetBytes) record.Dirty = true;

            record.Buffer = buffer;
            record.Offset = offsetBytes;

            if (slot >= (uint)_recorded) _recorded = (int)slot + 1;
        }

        /// <summary>
        /// Emit one <c>setVertexBuffers:offsets:withRange:</c> per contiguous run of dirty streams, and leave
        /// every stream clean and stamped against <paramref name="epoch"/>.
        /// <para>
        /// A STREAM IS DIRTY OR ITS STAMP IS STALE, and either one owes a bind. The second is M-R4: ending an
        /// encoder discards every vertex-stream binding along with the rest of the argument table, so the first
        /// draw after any boundary re-issues all of them however clean the flags read.
        /// </para>
        /// <para>
        /// THE STAMPS GO ON AFTER THE CALL LANDS, for the reason the resource-set flush gives.
        /// </para>
        /// </summary>
        /// <param name="sink">M-T2's seam. Streams are counted as the argument-table writes they are.</param>
        /// <param name="encoder">The open render encoder, never <see cref="IntPtr.Zero"/>.</param>
        /// <param name="epoch">The encoder scope's current epoch.</param>
        internal void Flush<TSink>(ref TSink sink, IntPtr encoder, ulong epoch)
            where TSink : struct, IMetalEncoderSink
        {
            bool any = false;
            for (int slot = 0; slot < _recorded; slot++)
            {
                _streams[slot].Owed = _streams[slot].Dirty || !_streams[slot].Emitted.IsValidIn(epoch);
                any |= _streams[slot].Owed;
            }

            if (!any) return;

            if (encoder == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "A native Metal vertex-stream flush was given a nil encoder. A message to nil is a silent "
                    + "no-op in Objective-C, so the streams would go nowhere and their records would read as "
                    + "bound. The pass schedule answers IntPtr.Zero for M-W5's orphan target, and a draw owes "
                    + "that arm before it reaches here.");
            }

            for (int slot = 0; slot < _recorded; slot++)
            {
                if (!_streams[slot].Owed) continue;

                _batch.Add(MetalIndexSpace.Buffer, (int)MetalVertexStreamIndex.ForSlot((uint)slot),
                    _streams[slot].Buffer, _streams[slot].Offset);
            }

            _batch.Emit(ref sink, MetalShaderStage.Vertex, encoder);

            for (int slot = 0; slot < _recorded; slot++)
            {
                if (!_streams[slot].Owed) continue;

                _streams[slot].Dirty = false;
                _streams[slot].Owed = false;
                _streams[slot].Emitted.Mark(epoch);
            }
        }

        /// <summary>FORGET EVERYTHING, from <c>MetalCommandList.Begin</c>'s one reset block.</summary>
        internal void Reset()
        {
            Array.Clear(_streams, 0, _recorded);
            _recorded = 0;
            _batch.Clear();
        }

        void EnsureSlot(uint slot)
        {
            if (slot > MaxSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot,
                    "A native Metal vertex stream was bound at slot "
                    + slot.ToString(CultureInfo.InvariantCulture) + ", past the "
                    + MaxSlot.ToString(CultureInfo.InvariantCulture) + " this backend records. Streams are "
                    + "pinned at the top of a buffer table holding "
                    + MetalVertexStreamIndex.BufferTableSize.ToString(CultureInfo.InvariantCulture)
                    + " entries, which the resource buffers grow into from the other end.");
            }

            if (slot < (uint)_streams.Length) return;

            int size = _streams.Length;
            while (slot >= (uint)size) size *= 2;
            Array.Resize(ref _streams, size);
        }

        struct StreamRecord
        {
            internal IntPtr Buffer;
            internal uint Offset;
            internal bool Dirty;
            internal MetalEncoderMark Emitted;

            // Transient, one flush wide, so "dirty or stale" is decided once rather than recomputed in the
            // gather and again in the stamping pass.
            internal bool Owed;
        }
    }
}
