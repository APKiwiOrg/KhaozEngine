using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE SCHEDULE OF DECISION R5, PORTED INTACT AND NOT NEGOTIABLE. This is the shape that produced the 40x
    /// shadow-encode collapse and 125 fps flat, so it is reproduced clause for clause rather than reinterpreted:
    /// <list type="number">
    /// <item><description>A resource-set bind RECORDS ONLY, comparing what it was handed against what the slot
    /// already holds and marking the slot <see cref="D3D11SlotDirty.Clean"/>,
    /// <see cref="D3D11SlotDirty.DynamicOffsetsOnly"/> or <see cref="D3D11SlotDirty.Full"/>.</description></item>
    /// <item><description>A draw or a dispatch flushes every dirty slot through the pre-command hook
    /// (<see cref="FlushGraphics{TSink}"/>, <see cref="FlushCompute{TSink}"/>) and then issues.</description></item>
    /// <item><description>A full slot activates fully. An offsets-only slot pushes ONLY the dynamic constant
    /// buffers and skips textures and samplers entirely.</description></item>
    /// <item><description>The flush walks slots in SLOT ORDER. The one observable difference from bind order is a
    /// resource bound two incompatible ways at once, which Direct3D 11 cannot honour either way.</description></item>
    /// <item><description>A pipeline switch DRAINS the pending sets under the OUTGOING layouts and then FORGETS
    /// the records, before adopting the incoming ones, because the layout array decides register numbering. A
    /// re-bind of the pipeline already current does neither.</description></item>
    /// <item><description>A slot whose recorded set has gone null is skipped.</description></item>
    /// <item><description>Repeated dirty marks on one slot between two draws collapse to one flush.</description></item>
    /// <item><description>The bound record is KEYED by slot and does not grow per rebind.</description></item>
    /// </list>
    /// <para>
    /// RULE 8 IS THE ONE THAT LOOKS LIKE BOOKKEEPING AND IS NOT. The hot path is thousands of offsets-only rebinds
    /// of ONE set per frame, so a record that appended per rebind, or that compared against a growing list, would
    /// make the frame O(n squared) in the count of rebinds. The record here is one struct per SLOT in an array
    /// indexed by slot, replaced in place, so a rebind is a constant-time compare-and-store no matter how many
    /// came before it and the record's size is bounded by the highest slot ever used.
    /// </para>
    /// <para>
    /// THE COMPARISON IS AGAINST WHAT WAS RECORDED, not against what was last activated, which is the ported
    /// behaviour and worth naming because the two differ. After every flush they are the same thing, since a
    /// flush activates exactly what the record holds. Between a record and the next draw the record runs ahead,
    /// and recording set A over a pending set B marks the slot fully dirty even when A is what is physically
    /// bound. That costs one redundant full activation in a sequence no renderer writes, and comparing against
    /// the activated value instead would mean carrying a second record for the sake of it.
    /// </para>
    /// <para>
    /// ONE PER DEVICE, held by <see cref="D3D11DeviceState"/> and reset by the one <c>ClearState</c> at the head
    /// of each replay along with the redundancy caches, for the same reason: after a <c>ClearState</c> the context
    /// holds nothing, so a record that still described the last replay would let a rebind of the same set be
    /// skipped as clean and the draw would read whatever the context now has, which is nothing.
    /// </para>
    /// <para>
    /// NOTHING HERE ISSUES A NATIVE CALL and nothing here names a Direct3D type. It decides which calls to make
    /// and hands them to an <see cref="ID3D11BindSink"/>, which is what lets the real emitter and the device-free
    /// <see cref="D3D11NativeTraceEmitter"/> share ONE implementation of the schedule and the fan-out. See
    /// <see cref="ID3D11BindSink"/> for the sink decision that shape is.
    /// </para>
    /// <para>
    /// NOT THREAD-SAFE, and it does not need to be, on the same grounds as <see cref="D3D11DeviceState"/>: under
    /// the deferred driver every mutation happens inside a replay under the one submit lock, and under
    /// <c>KE_D3D11_RECORD=immediate</c> every mutation happens during lock-free record, where decision W5's "one
    /// thread records at a time" is what makes it safe.
    /// </para>
    /// </summary>
    internal sealed class D3D11BindFlush
    {
        static readonly D3D11ResourceLayout[] NoLayouts = Array.Empty<D3D11ResourceLayout>();

        readonly D3D11SetActivation _activation = new();
        readonly D3D11RingAllocator? _rings;
        readonly bool _unsetConstantBuffersBeforeSet;

        SlotRecord[] _graphics = new SlotRecord[4];
        SlotRecord[] _compute = new SlotRecord[4];

        // NULL means no pipeline has been bound since the last ClearState, which is a different thing from a
        // pipeline that declares no layouts and is why this is not just an empty array. With no pipeline there is
        // no numbering to drain against, so a switch leaves the marks pending. With a pipeline that declares none,
        // a pending mark IS a mismatch and says so at the flush.
        D3D11ResourceLayout[]? _graphicsLayouts;
        D3D11ResourceLayout[]? _computeLayouts;

        /// <summary>
        /// Build the device's one bind flush.
        /// </summary>
        /// <param name="unsetConstantBuffersBeforeSet">Decision R7's <c>!DriverCommandLists</c> workaround: true
        /// when the device's threading probe reports that the Direct3D 11 runtime is EMULATING command lists, in
        /// which case every constant-buffer bind is preceded by an explicit unset of the same span. Both arms are
        /// asserted, because the workaround doubles the constant-buffer call count and a budget that only ever saw
        /// the fast arm would read the slow one as a regression.</param>
        /// <param name="ringsUnmappedBeforeCommands">The device's constant-buffer ring allocator, and ONLY under
        /// <c>KE_D3D11_RECORD=immediate</c>. See <see cref="RingsFor"/> for why the deferred driver passes null.
        /// </param>
        internal D3D11BindFlush(bool unsetConstantBuffersBeforeSet = false,
            D3D11RingAllocator? ringsUnmappedBeforeCommands = null)
        {
            _unsetConstantBuffersBeforeSet = unsetConstantBuffersBeforeSet;
            _rings = ringsUnmappedBeforeCommands;
        }

        /// <summary>Whether decision R7's unset-before-set workaround is in force on this device.</summary>
        internal bool UnsetsConstantBuffersBeforeSet => _unsetConstantBuffersBeforeSet;

        /// <summary>Whether this flush unmaps the constant-buffer rings before every command, which is true on the
        /// immediate driver alone.</summary>
        internal bool UnmapsRingsBeforeCommands => _rings is not null;

        /// <summary>
        /// WHICH DRIVER OWES THE RING AN UNMAP AT THE FLUSH POINT, and the answer is the immediate one only.
        /// <para>
        /// The deferred driver's <c>Submit</c> already unmaps every mapped ring inside the submit lock, before the
        /// replay that this flush runs inside (decision U2), and nothing can re-map during that replay, so an
        /// unmap here would be a no-op taking one uncontended lock per draw. At a thousand draws a frame that is a
        /// real cost for a call that can never do anything, and it would also make T2's "zero <c>Map</c> or
        /// <c>Unmap</c> during replay" an invariant the code contradicts by trying.
        /// </para>
        /// <para>
        /// The immediate driver has no such point: it issues draws AS the seam is called, so a ring mapped by a
        /// record-time uniform write is still mapped when the next draw binds it, and Direct3D 11 does not permit
        /// a draw against a mapped resource. Unmapping at every flush point is the per-FLUSH mapping the spec
        /// names as that driver's degradation, and it is why <see cref="D3D11RingAllocator.MapScopeFor"/> now
        /// answers <see cref="D3D11RingMapScope.AcrossRecording"/> for both drivers.
        /// </para>
        /// </summary>
        internal static D3D11RingAllocator? RingsFor(D3D11RecordMode mode, D3D11RingAllocator? rings)
            => mode == D3D11RecordMode.Immediate ? rings : null;

        /// <summary>The graphics slot's recorded set, for a test and for a diagnostic. Never needed to decide a
        /// flush.</summary>
        internal IGpuResourceSet? RecordedGraphicsSet(uint slot)
            => slot < (uint)_graphics.Length ? _graphics[slot].Set : null;

        /// <summary>How dirty the graphics slot is right now.</summary>
        internal D3D11SlotDirty GraphicsDirty(uint slot)
            => slot < (uint)_graphics.Length ? _graphics[slot].Dirty : D3D11SlotDirty.Clean;

        /// <summary>How dirty the compute slot is right now.</summary>
        internal D3D11SlotDirty ComputeDirty(uint slot)
            => slot < (uint)_compute.Length ? _compute[slot].Dirty : D3D11SlotDirty.Clean;

        /// <summary>How many slots the keyed record currently spans. Rule 8's assertion in one number: it follows
        /// the HIGHEST SLOT ever bound and never the count of rebinds, so a thousand rebinds of slot zero leave it
        /// where one did.</summary>
        internal int RecordedSlotCapacity => _graphics.Length + _compute.Length;

        /// <summary>
        /// RECORD ONLY (rule 1). No native call, no register arithmetic, no device contact: compare against what
        /// this slot already holds, store, and leave the slot owing the greater of what it owed and what this bind
        /// asks for.
        /// <para>
        /// <paramref name="hasDynamicOffset"/> is part of the comparison rather than folded into an offset of
        /// zero, because a set bound WITH a dynamic offset of zero and a set bound WITHOUT one are different
        /// binds: the seam carries them as two overloads and the op stream as two opcodes, and treating them as
        /// equal would let a switch between the two forms take the offsets-only path and leave the textures of a
        /// full activation unbound.
        /// </para>
        /// </summary>
        internal void RecordGraphics(uint slot, IGpuResourceSet? set, uint dynamicOffset, bool hasDynamicOffset)
            => Record(ref _graphics, slot, set, dynamicOffset, hasDynamicOffset);

        /// <inheritdoc cref="RecordGraphics"/>
        internal void RecordCompute(uint slot, IGpuResourceSet? set, uint dynamicOffset, bool hasDynamicOffset)
            => Record(ref _compute, slot, set, dynamicOffset, hasDynamicOffset);

        /// <summary>
        /// RULE 5, THE PIPELINE-SWITCH DRAIN: flush whatever is pending under the OUTGOING layouts, FORGET the
        /// records, then adopt the incoming ones. Called by the emitter's <c>SetPipeline</c> BEFORE the redundancy
        /// caches see the new pipeline, so the drained binds appear in the trace ahead of the state calls they
        /// belong behind.
        /// <para>
        /// WHY IT CANNOT WAIT FOR THE DRAW. A set's absolute register is its layout-relative register plus the sum
        /// of the counts of every layout before it in the PIPELINE'S array, so the same set bound at the same slot
        /// numbers differently under two pipelines. A mark recorded while pipeline A was current therefore has to
        /// be issued under A's array. Flushing it after the switch would number it under B's, which compiles,
        /// binds and renders the wrong resources.
        /// </para>
        /// <para>
        /// AND WHY THE RECORDS DIE WITH THE OUTGOING LAYOUTS, which is the half that is silent when it is left
        /// out. <see cref="Record"/> compares against what the slot already holds, so a record that survived the
        /// switch would mark a rebind of the SAME set at the SAME slot <see cref="D3D11SlotDirty.Clean"/> and the
        /// next draw would issue nothing for it. Under a pipeline whose layout array renumbers that slot the set
        /// is then still physically at the OUTGOING registers while the incoming pipeline reads the new ones:
        /// wrong constants, nothing thrown, nothing logged, which is the exact class rule 5 exists to prevent. So
        /// the drain is followed by a wipe of the records and the marks together (the fork's <c>ClearSets</c> plus
        /// <c>ClearArray</c>). The price is that a switch leaves every slot owing a full activation, and it is the
        /// only safe one: whether the incoming array renumbers a given slot is not knowable slot by slot.
        /// </para>
        /// <para>
        /// A REDUNDANT RE-BIND OF THE PIPELINE ALREADY CURRENT DRAINS NOTHING AND WIPES NOTHING, and the guard
        /// belongs HERE rather than at the caller: <see cref="D3D11NativeTraceEmitter.SetPipeline"/> reaches this
        /// BEFORE the redundancy caches see the pipeline, so a caller-side guard would need a second cache of its
        /// own. Identity is taken on the layout ARRAY by reference, which is exactly the question being asked
        /// (two pipelines sharing one array number their sets identically, so neither the drain nor the wipe
        /// could change anything) and a pipeline answers the same array instance every time. Without the guard a
        /// renderer that rebinds its pipeline defensively between two draws would wipe the records it just made
        /// and pay a full activation per bind, which is the #418 cost arriving through another door.
        /// </para>
        /// <para>
        /// WITH NO PIPELINE BOUND THERE IS NOTHING TO DRAIN AGAINST, so the marks stay pending, the records stay
        /// with them, and both are paid at the next draw under the incoming pipeline. That is the only answer
        /// available: a set recorded before any pipeline has no numbering yet, and inventing one would be worse
        /// than deferring it.
        /// </para>
        /// </summary>
        internal void SetGraphicsPipeline<TSink>(ref TSink sink, IGpuPipeline pipeline)
            where TSink : struct, ID3D11BindSink
        {
            if (pipeline is null) throw new ArgumentNullException(nameof(pipeline));

            _graphicsLayouts = Switch(ref sink, _graphics, _graphicsLayouts, LayoutsOf(pipeline));
        }

        /// <inheritdoc cref="SetGraphicsPipeline{TSink}"/>
        internal void SetComputePipeline<TSink>(ref TSink sink, IGpuComputePipeline pipeline)
            where TSink : struct, ID3D11BindSink
        {
            if (pipeline is null) throw new ArgumentNullException(nameof(pipeline));

            _computeLayouts = Switch(ref sink, _compute, _computeLayouts, LayoutsOf(pipeline));
        }

        /// <summary>
        /// THE PRE-COMMAND HOOK (rule 2), and the shape work-breakdown row 10 consumes: call it FIRST in
        /// <c>Draw</c> and <c>DrawIndexed</c>, before the vertex and index binds and before the draw itself, then
        /// issue. It flushes every dirty graphics slot in slot order and leaves them all clean.
        /// <para>
        /// THE RING UNMAP IS UNCONDITIONAL AND COMES FIRST, on the driver that has rings wired in. It is not
        /// inside the dirty check, and that placement is the correctness of it: a draw with NO dirty slot still
        /// draws against the constant buffers a previous flush bound, and a record-time uniform write since then
        /// has re-mapped the ring underneath them. Unmapping only when something is dirty would leave exactly that
        /// draw reading a mapped resource.
        /// </para>
        /// </summary>
        internal void FlushGraphics<TSink>(ref TSink sink) where TSink : struct, ID3D11BindSink
        {
            _rings?.UnmapMappedRings();
            Drain(ref sink, _graphics, _graphicsLayouts ?? NoLayouts);
        }

        /// <inheritdoc cref="FlushGraphics{TSink}"/>
        internal void FlushCompute<TSink>(ref TSink sink) where TSink : struct, ID3D11BindSink
        {
            _rings?.UnmapMappedRings();
            Drain(ref sink, _compute, _computeLayouts ?? NoLayouts);
        }

        /// <summary>
        /// FORGET EVERYTHING, which is what the one <c>ClearState</c> at the head of a replay does to the context.
        /// Called by <see cref="D3D11DeviceState.Reset"/>, so the schedule and the redundancy caches are dropped
        /// together and neither can survive a boundary the other did not.
        /// <para>
        /// The layouts go too. <c>ClearState</c> unbinds the shaders, so no pipeline is current afterwards, and a
        /// retained layout array would let the first flush of the next replay number a set under the last
        /// replay's pipeline.
        /// </para>
        /// </summary>
        internal void Reset()
        {
            Array.Clear(_graphics);
            Array.Clear(_compute);
            _graphicsLayouts = null;
            _computeLayouts = null;
        }

        static void Record(ref SlotRecord[] records, uint slot, IGpuResourceSet? set, uint dynamicOffset,
            bool hasDynamicOffset)
        {
            EnsureSlot(ref records, slot);
            ref SlotRecord record = ref records[slot];

            D3D11SlotDirty mark;
            if (!ReferenceEquals(record.Set, set) || record.HasDynamicOffset != hasDynamicOffset)
                mark = D3D11SlotDirty.Full;
            else if (record.DynamicOffset != dynamicOffset)
                mark = D3D11SlotDirty.DynamicOffsetsOnly;
            else
                mark = D3D11SlotDirty.Clean;

            record.Set = set;
            record.DynamicOffset = dynamicOffset;
            record.HasDynamicOffset = hasDynamicOffset;

            // Rule 7: several marks between two draws collapse to one flush, and the flush owes the MOST any of
            // them asked for. A Clean mark never lowers a pending one, because the pending activation has not
            // happened yet.
            if (mark > record.Dirty) record.Dirty = mark;
        }

        // One pipeline switch on one arm: drain under the outgoing layouts, wipe the records, and answer the array
        // to adopt. The two arms differ in nothing but which record and which layout array they hand in, so the
        // rule lives once and neither arm can drift into being the careful one.
        D3D11ResourceLayout[] Switch<TSink>(ref TSink sink, SlotRecord[] records, D3D11ResourceLayout[]? outgoing,
            D3D11ResourceLayout[] incoming)
            where TSink : struct, ID3D11BindSink
        {
            // The same numbering, so there is nothing to drain under and nothing the records could be wrong about.
            if (ReferenceEquals(outgoing, incoming)) return incoming;

            // No pipeline yet: no numbering to drain against, so the marks AND the records both stay, and the
            // first draw under the incoming pipeline pays them.
            if (outgoing is null) return incoming;

            Drain(ref sink, records, outgoing);
            Array.Clear(records);
            return incoming;
        }

        // The flush walk, shared by the draw hook and the pipeline drain. Slot order (rule 4), one activation per
        // dirty slot, and the slot is clean afterwards whether or not it issued anything.
        void Drain<TSink>(ref TSink sink, SlotRecord[] records, D3D11ResourceLayout[] layouts)
            where TSink : struct, ID3D11BindSink
        {
            for (uint slot = 0; slot < (uint)records.Length; slot++)
            {
                ref SlotRecord record = ref records[slot];
                if (record.Dirty == D3D11SlotDirty.Clean) continue;

                D3D11SlotDirty dirty = record.Dirty;

                // Rule 6. A slot whose recorded set has gone null has nothing to activate, and it is SKIPPED
                // rather than unbound: the registers it used belong to this slot alone, so leaving them is
                // invisible to every shader the next draw runs, and unbinding them would be a native call spent
                // on a slot nobody is reading. It still goes clean, so the skip happens once.
                if (record.Set is null)
                {
                    record.Dirty = D3D11SlotDirty.Clean;
                    continue;
                }

                if (record.Set is not D3D11ResourceSet set)
                {
                    throw new ArgumentException(
                        $"A {record.Set.GetType().Name} was bound at slot {slot} of the native Direct3D 11 "
                        + "backend. A resource set this backend created carries the register numbering the bind "
                        + "flush needs, and a set from another backend carries another backend's.");
                }

                D3D11RegisterCounts baseCounts = D3D11RegisterScheme.BaseFor(layouts, slot);
                _activation.Activate(ref sink, set, baseCounts, dirty == D3D11SlotDirty.DynamicOffsetsOnly,
                    record.DynamicOffset, _unsetConstantBuffersBeforeSet);

                // CLEAN ONLY AFTER THE ACTIVATION LANDED. Both throws above are caller mistakes that a next draw
                // would hit again, and clearing the mark first is what would stop it: the exception escapes the
                // draw, the slot reads clean, and the SECOND draw issues nothing for it and renders against
                // whatever the registers still hold. Losing the mark turns a loud, repeatable refusal into one
                // throw followed by silence.
                record.Dirty = D3D11SlotDirty.Clean;
            }
        }

        // Grow the keyed record to cover a slot. Doubling, so a run of binds at rising slots reallocates a
        // handful of times, and never per rebind: this is reached only by a slot the record has not seen.
        static void EnsureSlot(ref SlotRecord[] records, uint slot)
        {
            if (slot < (uint)records.Length) return;
            if (slot > MaxSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot,
                    $"Slot {slot} is past the {MaxSlot} a Direct3D 11 pipeline can address. A slot indexes the "
                    + "pipeline's resource-layout array, so a number this large is a mismatch rather than a deep "
                    + "binding model.");
            }

            int capacity = records.Length;
            while (capacity <= slot) capacity <<= 1;
            Array.Resize(ref records, capacity);
        }

        // A pipeline that answers no layouts binds no sets. Deliberately not an exception: the mismatch a caller
        // actually made is "a set at slot k under a pipeline with fewer layouts than that", and BaseFor says so
        // in those terms at the flush.
        static D3D11ResourceLayout[] LayoutsOf(object pipeline)
            => pipeline is ID3D11PipelineLayouts layouts ? layouts.ResourceLayouts : NoLayouts;

        // Well above anything a pipeline declares (the widest shipped pipeline has three layouts) and small
        // enough that a wild slot index cannot allocate its way to an OutOfMemoryException.
        const uint MaxSlot = 63;

        /// <summary>
        /// ONE SLOT'S RECORD, and the whole of rule 8. A struct in an array indexed by slot: replaced in place on
        /// every bind, so the record's size follows the highest slot ever used and never the number of rebinds.
        /// </summary>
        struct SlotRecord
        {
            /// <summary>The set last recorded here, or null when the slot has never been bound or was bound to
            /// null.</summary>
            internal IGpuResourceSet? Set;

            /// <summary>The per-draw byte offset last recorded with it.</summary>
            internal uint DynamicOffset;

            /// <summary>Whether the bind that recorded it used the offset-carrying overload at all.</summary>
            internal bool HasDynamicOffset;

            /// <summary>How much the next draw owes this slot.</summary>
            internal D3D11SlotDirty Dirty;
        }
    }
}
