using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE INPUT-ASSEMBLER HALF OF WHAT IS BOUND ON THE CONTEXT: the vertex streams and the index buffer, with
    /// decision R6's batching rule for the first and its redundancy rule for both. One per device, held by
    /// <see cref="D3D11DeviceState"/> exactly as <see cref="D3D11BindFlush"/> is, and reset with it by the one
    /// <c>ClearState</c> at the head of a replay.
    /// <para>
    /// A VERTEX BIND RECORDS ONLY AND THE DRAW ISSUES IT, which is what makes 5.3's
    /// <c>IASetVertexBuffers(0, 2, ...)</c> possible: two per-stream calls cannot be collapsed into one array call
    /// after they have been made. That deferral is not only an optimisation. <c>IASetVertexBuffers</c> takes the
    /// per-slot STRIDE, and the stride comes from the PIPELINE, so at the moment a vertex buffer is bound the
    /// value the call needs may not exist yet. Deferring to the draw is what makes the stride available at all,
    /// and the batching falls out of it.
    /// </para>
    /// <para>
    /// AN INDEX BIND IS NOT DEFERRED, because there is nothing to batch it with: <c>IASetIndexBuffer</c> binds one
    /// buffer and D3D11 has no array form of it. It carries a redundancy cache over the pair (buffer, format) and
    /// issues at the bind, which is where the incumbent issues it too.
    /// </para>
    /// <para>
    /// BOTH SPANS THIS TYPE ANSWERS WITH COVER SLOTS THE CALLER DID NOT TOUCH, AND THE CALLER WRITES THE RECORD
    /// ACROSS THEM. Slots 0 and 2 dirty with 1 clean is ONE call over [0, 3) that rebinds slot 1 to exactly what
    /// it already holds, rather than two calls with a gap between them, which is the same trade
    /// <see cref="D3D11SetActivation"/> makes for a hole in a register span. It is safe because what is written
    /// into the hole is WHAT THE RECORD HOLDS, and that is the rule rather than the flush's private arrangement:
    /// <see cref="Scrub"/> answers with a span too, having already nulled the records of the slots it forgot, so
    /// one write over that span unbinds exactly those and leaves any live slot between them holding what it held.
    /// An unbind that nulled the whole span instead would drop a live stream at the device while this record still
    /// called that slot bound and clean, and the next draw would issue nothing and the stream would read no data.
    /// </para>
    /// <para>
    /// NOTHING HERE ISSUES A NATIVE CALL and nothing here names a Direct3D type. It answers which slots to issue
    /// and with what, and the caller makes the call, so the real emitter and the device-free
    /// <see cref="D3D11NativeTraceEmitter"/> share ONE implementation of the batching and the caches rather than
    /// two that drift. Not thread-safe, on the same grounds as <see cref="D3D11DeviceState"/>.
    /// </para>
    /// </summary>
    internal sealed class D3D11VertexStreams
    {
        static readonly uint[] NoStrides = Array.Empty<uint>();

        Stream[] _streams = new Stream[4];
        uint[] _strides = NoStrides;

        IGpuBuffer? _indexBuffer;
        GpuIndexFormat _indexFormat;

        /// <summary>The per-slot strides of the pipeline currently bound, or an empty array when no pipeline is
        /// bound. The flush reads them, and a change of the ARRAY INSTANCE is what invalidates the streams.
        /// </summary>
        internal uint[] Strides => _strides;

        /// <summary>The index buffer currently bound, or null.</summary>
        internal IGpuBuffer? IndexBuffer => _indexBuffer;

        /// <summary>The format the index buffer was bound with. Meaningless while
        /// <see cref="IndexBuffer"/> is null.</summary>
        internal GpuIndexFormat IndexFormat => _indexFormat;

        /// <summary>How many slots the stream record currently spans. It follows the HIGHEST SLOT ever bound and
        /// never the number of rebinds, the same shape as the bind flush's keyed record.</summary>
        internal int RecordedSlotCapacity => _streams.Length;

        /// <summary>The buffer recorded at <paramref name="slot"/>, or null when the slot was never bound.
        /// </summary>
        internal IGpuBuffer? BufferAt(uint slot)
            => slot < (uint)_streams.Length ? _streams[slot].Buffer : null;

        /// <summary>The byte offset recorded at <paramref name="slot"/>.</summary>
        internal uint OffsetAt(uint slot) => slot < (uint)_streams.Length ? _streams[slot].OffsetBytes : 0u;

        /// <summary>The stride the current pipeline declares for <paramref name="slot"/>. Zero past the end of the
        /// declared array, which the flush never reaches: it covers only the slots the array declares.</summary>
        internal uint StrideAt(uint slot) => slot < (uint)_strides.Length ? _strides[slot] : 0u;

        /// <summary>Whether <paramref name="slot"/> is owed an issue at the next draw. For tests and diagnostics
        /// only, never needed to decide a flush.</summary>
        internal bool IsDirty(uint slot) => slot < (uint)_streams.Length && _streams[slot].Dirty;

        /// <summary>
        /// RECORD ONLY. Compare against what the slot already holds and mark it dirty when the pair (buffer,
        /// offset) differs, which is the whole of the vertex redundancy cache. A rebind of what is already there
        /// costs nothing at the next draw.
        /// </summary>
        internal void RecordVertexBuffer(uint slot, IGpuBuffer? buffer, uint offsetBytes)
        {
            EnsureSlot(ref _streams, slot);
            ref Stream stream = ref _streams[slot];

            if (ReferenceEquals(stream.Buffer, buffer) && stream.OffsetBytes == offsetBytes) return;

            stream.Buffer = buffer;
            stream.OffsetBytes = offsetBytes;
            stream.Dirty = true;
        }

        /// <summary>
        /// DECISION R6 FOR THE INDEX BUFFER: true when the pair (buffer, format) is not what is already bound, so
        /// the caller issues <c>IASetIndexBuffer</c>, and false when the rebind is redundant.
        /// <para>
        /// The FORMAT is part of the key rather than an argument carried along, because the same buffer bound as
        /// 16-bit indices and as 32-bit indices is two different binds and the second would otherwise be skipped.
        /// </para>
        /// </summary>
        internal bool BindIndexBuffer(IGpuBuffer? buffer, GpuIndexFormat format)
        {
            if (ReferenceEquals(_indexBuffer, buffer) && _indexFormat == format) return false;

            _indexBuffer = buffer;
            _indexFormat = format;
            return true;
        }

        /// <summary>
        /// ADOPT A PIPELINE'S STRIDES, and invalidate what they change. Called from
        /// <see cref="D3D11DeviceState.BindPipeline"/> on every pipeline bind, before the draw that follows it.
        /// <para>
        /// A DIFFERENT STRIDE ARRAY MARKS EVERY BOUND SLOT DIRTY, which is the correctness of this method rather
        /// than a conservative flourish. The stride is an argument of <c>IASetVertexBuffers</c>, so a pipeline
        /// switch between two vertex formats leaves the same buffer bound at the OUTGOING pipeline's stride and
        /// the incoming pass reads every vertex at the wrong span. Nothing throws and nothing logs, and the frame
        /// is geometry noise. Identity is taken on the ARRAY, so two pipelines that share one stride array
        /// invalidate nothing.
        /// </para>
        /// <para>
        /// ONLY THE SLOTS THE INCOMING ARRAY DECLARES ARE MARKED. A pipeline with no vertex inputs at all (the
        /// fullscreen passes) declares none, and the streams a previous pass left bound are read by nothing under
        /// it, so re-issuing them would be a native call spent on a slot no input layout references. Their records
        /// stay, so the pass after that re-issues them if its own strides differ.
        /// </para>
        /// </summary>
        internal void AdoptStrides(uint[] strides)
        {
            if (strides is null) throw new ArgumentNullException(nameof(strides));
            if (ReferenceEquals(_strides, strides)) return;

            _strides = strides;
            uint covered = (uint)Math.Min(_streams.Length, strides.Length);
            for (uint slot = 0; slot < covered; slot++)
            {
                if (_streams[slot].Buffer is not null) _streams[slot].Dirty = true;
            }
        }

        /// <summary>
        /// THE PRE-DRAW FLUSH: the contiguous span of dirty slots the current pipeline declares a stride for, or
        /// false when nothing is owed. Every slot in the span is left CLEAN, whether it was dirty or was swept in
        /// as a hole, because one call bound all of them.
        /// <para>
        /// A dirty slot PAST the declared strides is cleared without being issued, for the reason
        /// <see cref="AdoptStrides"/> gives: no input layout references it, so binding it would cost a call and
        /// change nothing a shader reads.
        /// </para>
        /// </summary>
        internal bool TakeFlush(out uint startSlot, out int count)
        {
            startSlot = 0;
            count = 0;

            uint covered = (uint)Math.Min(_streams.Length, _strides.Length);
            bool any = false;
            uint lo = 0;
            uint hi = 0;

            for (uint slot = 0; slot < (uint)_streams.Length; slot++)
            {
                if (!_streams[slot].Dirty) continue;

                // Past the declared strides: nothing reads it, so drop the mark rather than issue it.
                if (slot >= covered)
                {
                    _streams[slot].Dirty = false;
                    continue;
                }

                if (!any)
                {
                    lo = slot;
                    any = true;
                }

                hi = slot;
            }

            if (!any) return false;

            for (uint slot = lo; slot <= hi; slot++) _streams[slot].Dirty = false;

            startSlot = lo;
            count = (int)(hi - lo) + 1;
            return true;
        }

        /// <summary>
        /// DECISION R8 FOR THE INPUT ASSEMBLER: forget <paramref name="resource"/> wherever it is bound and report
        /// the span the caller writes, so exactly the slots that named it are unbound.
        /// <para>
        /// A scrubbed vertex slot is left holding null and NOT marked dirty, because the caller is about to write
        /// it now rather than at the next draw. Marking it would issue the same unbind a second time.
        /// </para>
        /// <para>
        /// THE SPAN IS THE OUTERMOST PAIR AND MAY STRADDLE A LIVE SLOT: slots 0 and 2 named the disposed buffer
        /// and slot 1 names another that is still very much alive. The caller writes the RECORD over the whole
        /// span (see the type remarks), so slot 1 is rebound to exactly what it already holds. Writing nulls
        /// across the span instead would unbind it at the device while this record still called it bound and
        /// clean, and the next draw would issue nothing, leaving that stream reading no data with nothing thrown
        /// and nothing logged.
        /// </para>
        /// </summary>
        internal D3D11StateChange Scrub(object resource, out uint startSlot, out int count)
        {
            if (resource is null) throw new ArgumentNullException(nameof(resource));

            startSlot = 0;
            count = 0;
            D3D11StateChange scrubbed = D3D11StateChange.None;

            bool any = false;
            uint lo = 0;
            uint hi = 0;
            for (uint slot = 0; slot < (uint)_streams.Length; slot++)
            {
                if (!ReferenceEquals(_streams[slot].Buffer, resource)) continue;

                _streams[slot].Buffer = null;
                _streams[slot].OffsetBytes = 0;
                _streams[slot].Dirty = false;

                if (!any)
                {
                    lo = slot;
                    any = true;
                }

                hi = slot;
            }

            if (any)
            {
                startSlot = lo;
                count = (int)(hi - lo) + 1;
                scrubbed |= D3D11StateChange.VertexBuffers;
            }

            if (ReferenceEquals(_indexBuffer, resource))
            {
                _indexBuffer = null;
                scrubbed |= D3D11StateChange.IndexBuffer;
            }

            return scrubbed;
        }

        /// <summary>Forget everything, which is what the one <c>ClearState</c> does to the input assembler. The
        /// strides go with the records: <c>ClearState</c> unbinds the shaders and the input layout, so no pipeline
        /// is current afterwards and a retained stride array would let the first draw of the next replay issue a
        /// stream at the last replay's stride.</summary>
        internal void Reset()
        {
            Array.Clear(_streams);
            _strides = NoStrides;
            _indexBuffer = null;
            _indexFormat = default;
        }

        // Grow the stream record to cover a slot. Doubling, and reached only by a slot the record has not seen.
        static void EnsureSlot(ref Stream[] streams, uint slot)
        {
            if (slot < (uint)streams.Length) return;
            if (slot > MaxSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot,
                    $"Vertex buffer slot {slot} is past the {MaxSlot} Direct3D 11 addresses. The input assembler "
                    + "has 32 slots, so a number this large is a mismatch rather than a deep vertex layout.");
            }

            int capacity = streams.Length;
            while (capacity <= slot) capacity <<= 1;
            Array.Resize(ref streams, capacity);
        }

        // D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT is 32, so 31 is the highest addressable slot.
        const uint MaxSlot = 31;

        /// <summary>One vertex slot's record. A struct in an array indexed by slot, replaced in place, so a rebind
        /// is a constant-time compare-and-store no matter how many came before it.</summary>
        struct Stream
        {
            /// <summary>The buffer last recorded here, or null when the slot has never been bound.</summary>
            internal IGpuBuffer? Buffer;

            /// <summary>The byte offset recorded with it.</summary>
            internal uint OffsetBytes;

            /// <summary>Whether the next draw owes this slot an issue.</summary>
            internal bool Dirty;
        }
    }
}
