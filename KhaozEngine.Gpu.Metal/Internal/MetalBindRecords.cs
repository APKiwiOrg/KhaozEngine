using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SCHEDULE OF DECISIONS M-R5 TO M-R9 (section 6.3), and nothing else. It decides WHICH argument-table
    /// calls to make and hands them to an <see cref="IMetalEncoderSink"/>, so the real sink and the counting sink
    /// run ONE implementation of it and M-T2's device-free budget measures the shipped schedule rather than a
    /// second copy of it.
    ///
    /// <list type="number">
    /// <item><description>A resource-set bind RECORDS ONLY, into a per-slot <c>(set, engineDynamicOffset)</c>
    /// array, marking the slot dirty when either differs from what is recorded
    /// (<see cref="Record"/>).</description></item>
    /// <item><description><c>Draw</c>, <c>DrawIndexed</c> and <c>Dispatch</c> flush every dirty slot through the
    /// pre-command hook (<see cref="Flush"/>) and then issue.</description></item>
    /// <item><description>The flush assembles, per (kind, stage), a contiguous range of argument-table indices
    /// and emits ONE array call for it (M-R6, <see cref="MetalArgumentBatch"/>).</description></item>
    /// <item><description>A slot whose only change is its dynamic offset emits ONE
    /// <c>setVertexBufferOffset:</c> or <c>setFragmentBufferOffset:</c> per VISIBLE stage (M-R7).</description></item>
    /// <item><description>A pipeline switch invalidates recorded slots only where the incoming program's INDEX
    /// TABLE differs from the outgoing one's (M-R9, <see cref="SetIndexTable"/>).</description></item>
    /// <item><description>A slot whose recorded set has gone null is SKIPPED.</description></item>
    /// <item><description>Repeated dirty marks between two draws collapse to one flush, which falls out of an
    /// array of slots rather than a list of binds.</description></item>
    /// <item><description>Any encoder boundary invalidates everything (M-R4), through the
    /// <see cref="MetalEncoderMark"/> each slot carries.</description></item>
    /// </list>
    ///
    /// <para><b>TWO STATES, NOT THREE, AND THE OFFSETS-ONLY CALL IS STILL CHOSEN (M-R5).</b> The Direct3D 11
    /// backend carries a third <c>DynamicOffsetsOnly</c> state so a rebind can push the constant buffers and skip
    /// the textures and samplers, which on that API is a real saving inside the SAME activation path. Here the
    /// offsets-only rebind is a DIFFERENT SELECTOR, so it is not a cheaper variant of anything and does not need a
    /// state to select it: a dirty slot whose recorded bindings array IS the one this encoder's table already
    /// holds needs its offsets moved and nothing else, and that is a comparison the flush makes against what it
    /// EMITTED rather than a third thing <see cref="Record"/> has to classify correctly. A record that classified
    /// it would have to be right about the encoder epoch too, which is the half a recorder gets wrong.</para>
    ///
    /// <para><b>WHICH IS WHY EACH SLOT REMEMBERS THE ARRAY IT LAST WROTE INTO THE TABLE, AND NOT ONLY THE ONE IT
    /// HOLDS.</b> <c>setBufferOffset:atIndex:</c> adjusts an EXISTING binding and has nothing to adjust
    /// otherwise, so its precondition is exactly "the resources of this set are in this encoder's table right
    /// now". The emitted array plus the epoch stamp is that sentence, and a slot bound to B and then back to A
    /// with no draw between correctly takes the offsets-only arm, because A never left the table.</para>
    ///
    /// <para><b>AND THE STAMP IS PER SLOT RATHER THAN ONE FLAG FOR THE RECORDS.</b> A record-time
    /// <c>UpdateBuffer</c> big enough to take the staging path opens a blit encoder MID-PASS, so the next
    /// <c>PrepareDraw</c> reopens the pass on a new encoder whose argument table is empty (2.1). One shared flag
    /// would be right about that and wrong about M-R9, which invalidates some flushes and not others.</para>
    ///
    /// <para><b>THE INDEX TABLE IS THE AUTHORITY ON WHICH STAGES GET A BIND, AND NOT THE DECLARED VISIBILITY
    /// (2.2b).</b> <see cref="MetalShaderIndexTable.TryGetIndex"/> answering false means that stage's emitted
    /// function does not reference the element, so it MUST NOT be bound for that stage: the cross-compiler omits
    /// an argument a stage does not reference, and binding one anyway is what an index-counting backend does that
    /// produces the off-by-one. Over the shipped set 95 of 254 stage/element slots are unreferenced, so the
    /// partial-stage path is the common case rather than the corner.</para>
    ///
    /// <para><b>RULE 7 IS THE ONE THAT LOOKS LIKE BOOKKEEPING AND IS NOT.</b> The shadow pass does thousands of
    /// offsets-only rebinds of ONE set per frame. A record that appended per rebind would make the frame O(n
    /// squared) in the count of rebinds. The record is one struct per SLOT in an array indexed by slot, replaced
    /// in place, so a rebind is a constant-time compare-and-store.</para>
    ///
    /// <para><b>ONE PER BIND POINT PER LIST, AND NOT SYNCHRONISED.</b> Graphics and compute bindings are separate
    /// on this seam and reach different encoders here, so each gets its own records with its own stage set.
    /// Nothing is locked, on the same grounds as the list that owns it: one list records on one thread at a time
    /// (M-R3).</para>
    ///
    /// <para><b>NOTHING HERE MAKES A NATIVE CALL AND NOTHING HERE REACHES A DEVICE</b>, which is what lets the
    /// whole schedule, the run cutting, the stage fork and the offset composition be driven by plain
    /// <c>[Fact]</c>s on a machine with no Metal at all.</para>
    /// </summary>
    internal sealed class MetalBindRecords
    {
        /// <summary>
        /// The highest set number a record will grow to cover. Well past anything a shipped pipeline declares
        /// (the widest uses two sets), and small enough that a wild slot index cannot allocate its way to an
        /// <see cref="OutOfMemoryException"/>.
        /// </summary>
        internal const uint MaxSlot = 15;

        readonly MetalShaderStage[] _stages;
        readonly MetalArgumentBatch _batch = new();

        SlotRecord[] _slots = new SlotRecord[4];

        // One past the highest slot ever recorded, which bounds every walk. Follows the highest SLOT and never
        // the number of rebinds (rule 7).
        int _recorded;

        MetalShaderIndexTable? _table;

        MetalBindRecords(MetalShaderStage[] stages) => _stages = stages;

        /// <summary>The records a <c>Draw</c> flushes: the vertex and fragment stages of a render encoder.</summary>
        internal static MetalBindRecords ForGraphics()
            => new([MetalShaderStage.Vertex, MetalShaderStage.Fragment]);

        /// <summary>The records a <c>Dispatch</c> flushes: a compute encoder's single stage. Separate records
        /// because a graphics bind does not feed a dispatch and this does not feed a draw, which is the seam's own
        /// split.</summary>
        internal static MetalBindRecords ForCompute() => new([MetalShaderStage.Compute]);

        /// <summary>Which stages a flush of these records writes into.</summary>
        internal ReadOnlySpan<MetalShaderStage> Stages => _stages;

        /// <summary>One past the highest slot ever recorded. Rule 7's assertion in one number: it follows the
        /// highest SLOT and never the count of rebinds, so a thousand rebinds of slot zero leave it where one
        /// did.</summary>
        internal int RecordedSlotCount => _recorded;

        /// <summary>How many slots the record currently has room for. Grows to cover a slot and never per
        /// rebind.</summary>
        internal int SlotCapacity => _slots.Length;

        /// <summary>The index table a flush resolves through, or null when no pipeline is bound.</summary>
        internal MetalShaderIndexTable? IndexTable => _table;

        /// <summary>The set recorded at a slot, default for one holding none. For a test and a diagnostic.</summary>
        internal MetalBoundSet RecordedSet(uint slot)
            => slot < (uint)_recorded ? _slots[slot].Bound : default;

        /// <summary>The per-draw offset recorded alongside it.</summary>
        internal uint RecordedOffset(uint slot) => slot < (uint)_recorded ? _slots[slot].DynamicOffset : 0;

        /// <summary>Whether the next flush owes this slot anything. The whole of the state a slot has
        /// (M-R5).</summary>
        internal bool IsDirty(uint slot) => slot < (uint)_recorded && _slots[slot].Dirty;

        /// <summary>Whether this slot's resources are in the argument table of the encoder <paramref name="epoch"/>
        /// names. What separates the offsets-only arm from a full rebind, exposed so a test can assert the
        /// invalidation rather than infer it from a call count.</summary>
        internal bool IsEmittedIn(uint slot, ulong epoch)
            => slot < (uint)_recorded && _slots[slot].Emitted.IsValidIn(epoch);

        /// <summary>
        /// CLAUSE 1, RECORD ONLY. No native call, no device contact and no table lookup: store the set's plain
        /// data and the caller's offset, and leave the slot dirty if either moved.
        /// <para>
        /// A MARK IS NEVER LOWERED, which is clause 7. Several records between two draws collapse to one flush,
        /// and a record that matches what is already there does NOT clean a slot that was already owing a bind:
        /// the pending bind has not happened yet.
        /// </para>
        /// <para>
        /// A NON-ZERO OFFSET AGAINST A SET THAT DECLARES NO DYNAMIC ELEMENT IS REFUSED, because nothing would
        /// carry it. The composition adds the caller's offset only where the element was declared dynamic, so an
        /// offset passed to a set without one is silently dropped and the draw reads the buffer's first slot. A
        /// ZERO offset is accepted against any set, because dropping zero changes nothing and the seam's
        /// no-offset overload is exactly that call.
        /// </para>
        /// <para>
        /// A SLOT RECORDED AS HOLDING NO SET FORGETS WHAT IT EMITTED. Clause 6 skips it, so nothing unbinds the
        /// resources already in the table, and another slot's flush may overwrite those indices before this one
        /// is bound again. Forgetting is what makes the later rebind a FULL one rather than an offsets-only call
        /// against entries that have since moved.
        /// </para>
        /// </summary>
        /// <param name="slot">The set number this binds at.</param>
        /// <param name="set">The set's plain data, or default to record that the slot holds none.</param>
        /// <param name="dynamicOffset">The caller's per-draw byte offset, 0 for the overload that passes
        /// none.</param>
        internal void Record(uint slot, in MetalBoundSet set, uint dynamicOffset)
        {
            if (set.IsBound && dynamicOffset != 0 && !set.HasDynamicElement)
            {
                throw new ArgumentException(
                    "A native Metal resource set was bound at set " + slot.ToString(CultureInfo.InvariantCulture)
                    + " with a dynamic offset of " + dynamicOffset.ToString(CultureInfo.InvariantCulture)
                    + " bytes, and its layout declares no dynamic element for that offset to apply to. The "
                    + "declared flag is the ONE thing GpuResourceLayoutElement.Dynamic decides on this backend, "
                    + "and it decides what the caller's own offset attaches to. With no declared element the "
                    + "offset would be dropped and the draw would read the buffer's first slot. Declare the "
                    + "element dynamic in the resource layout, or bind through the overload that passes no "
                    + "offset.",
                    nameof(dynamicOffset));
            }

            EnsureSlot(slot);

            ref SlotRecord record = ref _slots[slot];
            if (!record.Bound.SameSetAs(set) || record.DynamicOffset != dynamicOffset) record.Dirty = true;

            if (!set.IsBound)
            {
                record.EmittedBindings = null;
                record.Emitted.Clear();
            }

            record.Bound = set;
            record.DynamicOffset = dynamicOffset;

            if (slot >= (uint)_recorded) _recorded = (int)slot + 1;
        }

        /// <summary>
        /// CLAUSE 5, THE PIPELINE-SWITCH INVALIDATION (M-R9). Adopt <paramref name="table"/> and, when it is not
        /// the table already current, invalidate every recorded slot.
        ///
        /// <para><b>THIS IS THE WIRING POINT ROW 11 CALLS</b>
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577), from <c>SetPipeline</c> and
        /// <c>SetComputePipeline</c>, with the pipeline's own index table, AFTER M-R8's identity guard has
        /// decided the pipeline really is changing. The two guards compose rather than duplicate: M-R8 skips the
        /// whole pipeline-state block for a rebind of the pipeline already bound, and this skips the bind
        /// invalidation for a DIFFERENT pipeline whose program maps every element to the same index. Until that
        /// row lands, the shipped seam members refuse and this is reached by the tests alone.</para>
        ///
        /// <para><b>THE COMPARISON IS REFERENCE IDENTITY AND THAT IS SOUND RATHER THAN CHEAP.</b>
        /// <see cref="MetalShaderIndexTable.SameIndicesAs"/> is a handle compare because every table a pipeline
        /// can reach came through <see cref="MetalIndexTableCache"/> at shader-set creation, so equal content IS
        /// one instance. Row 10 measured what it buys over the shipped catalog: 42 programs produce 17 distinct
        /// tables and 25 of the 42 share a table with an earlier program, so the invalidate-nothing path is the
        /// shadow pass and the post chain rather than a rare coincidence.</para>
        ///
        /// <para><b>INVALIDATION CLEARS THE EPOCH STAMP AND DOES NOT ONLY MARK DIRTY</b>, which is the one thing
        /// this method must not get wrong. The incoming program expects each element at a DIFFERENT index, so the
        /// resources sitting in the table are at the wrong places and the offsets-only arm would move an offset on
        /// a binding the new program never reads. Clearing the stamp is what forces the full rebind, and it is
        /// exactly the case <see cref="MetalEncoderMark.Clear"/> exists for.</para>
        ///
        /// <para><b>WITH NO TABLE PREVIOUSLY ADOPTED EVERYTHING IS INVALIDATED</b>, and that is correct rather
        /// than conservative: nothing is bound, so nothing survives.</para>
        /// </summary>
        /// <param name="table">The incoming program's index table.</param>
        /// <returns>Whether anything was invalidated, so a caller and a test can see that the switch was free.</returns>
        internal bool SetIndexTable(MetalShaderIndexTable table)
        {
            ArgumentNullException.ThrowIfNull(table);

            if (_table is not null && _table.SameIndicesAs(table)) return false;

            _table = table;
            InvalidateAll();
            return true;
        }

        /// <summary>
        /// Mark every recorded slot as owing a FULL rebind. Separated from <see cref="SetIndexTable"/> so the
        /// invalidation can be driven on its own, and because it is what any future reason to invalidate would
        /// reach for rather than reimplementing.
        /// </summary>
        internal void InvalidateAll()
        {
            for (int slot = 0; slot < _recorded; slot++)
            {
                _slots[slot].Dirty = true;
                _slots[slot].Emitted.Clear();
                _slots[slot].EmittedBindings = null;
            }
        }

        /// <summary>
        /// FORGET EVERYTHING, which is what a fresh <c>MTLCommandBuffer</c> holds: no bound program and no
        /// recorded slot. Called from <c>MetalCommandList.Begin</c>, in the one reset block that file keeps.
        /// <para>
        /// THE ENCODER SCOPE'S OWN EPOCH BUMP AT <c>BeginRecording</c> ALREADY MAKES EVERY STAMP STALE, so this
        /// is belt and braces on the invalidation and load-bearing on the rest: the recorded SETS belong to a
        /// recording that was discarded, and a Begin discards a recording by contract.
        /// </para>
        /// </summary>
        internal void Reset()
        {
            Array.Clear(_slots, 0, _recorded);
            _recorded = 0;
            _table = null;
            _batch.Clear();
        }

        /// <summary>
        /// CLAUSES 2, 3 AND 4, THE PRE-COMMAND HOOK. Row 14
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580) calls this AFTER
        /// <c>MetalRenderPassSchedule.PrepareDraw</c> has opened the pass and BEFORE the vertex and index binds
        /// and the command itself, with the encoder that returned.
        ///
        /// <para><b>GENERIC OVER THE SINK AND <c>ref</c> RATHER THAN <c>in</c></b>, so the JIT monomorphizes
        /// M-T2's seam away and no defensive copy is made per call on the per-draw path.</para>
        ///
        /// <para><b>THE MARKS GO CLEAN ONLY AFTER EVERY CALL HAS LANDED.</b> The composition refuses a window
        /// that would leave its own segment (M-M4), and a flush's marks must survive that throw: clearing first
        /// would mean the exception escapes the draw, the slots read clean, and the SECOND draw issues nothing for
        /// them and renders against whatever the argument tables still hold. That turns a loud, repeatable
        /// refusal into one throw followed by silence.</para>
        ///
        /// <para><b>A FLUSH WITH NOTHING OWED TOUCHES NO TABLE AND MAKES NO CALL</b>, which is what makes a draw
        /// after a draw free. It is also why the no-pipeline refusal is inside the work check rather than in
        /// front of it: a draw that binds no resource set at all is legal.</para>
        /// </summary>
        /// <param name="sink">M-T2's seam.</param>
        /// <param name="encoder">The open encoder, never <see cref="IntPtr.Zero"/>.</param>
        /// <param name="epoch">The encoder scope's current epoch, which every stamp is compared against
        /// (M-R4).</param>
        /// <param name="segment">The ring segment THIS RECORDING captured at its <c>Begin</c>
        /// (<c>MetalCommandList.RingSegment</c>), which is what a ring-backed bind composes its base from. Never
        /// the allocator's live segment: a concurrent list's <c>Begin</c> moves that one and a bind composed
        /// against it would name a version this recording never wrote.</param>
        /// <exception cref="InvalidOperationException">There is work owed and no pipeline is bound, or the
        /// encoder is nil.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A composed bind window leaves its own ring segment
        /// (M-M4).</exception>
        internal void Flush<TSink>(ref TSink sink, IntPtr encoder, ulong epoch, int segment)
            where TSink : struct, IMetalEncoderSink
        {
            bool any = false;
            for (int slot = 0; slot < _recorded; slot++)
            {
                _slots[slot].Work = WorkFor(ref _slots[slot], epoch);
                any |= _slots[slot].Work != SlotWork.None;
            }

            if (!any)
            {
                for (int slot = 0; slot < _recorded; slot++) _slots[slot].Dirty = false;
                return;
            }

            MetalShaderIndexTable table = RequireIndexTable();
            RequireEncoder(encoder);

            foreach (MetalShaderStage stage in _stages)
            {
                EmitFullRebinds(ref sink, table, stage, encoder, segment);
                EmitOffsetsOnly(ref sink, table, stage, encoder, segment);
            }

            for (int slot = 0; slot < _recorded; slot++)
            {
                ref SlotRecord record = ref _slots[slot];
                if (record.Work == SlotWork.Full)
                {
                    record.EmittedBindings = record.Bound.Bindings;
                    record.Emitted.Mark(epoch);
                }

                record.Dirty = false;
                record.Work = SlotWork.None;
            }
        }

        void EmitFullRebinds<TSink>(ref TSink sink, MetalShaderIndexTable table, MetalShaderStage stage,
            IntPtr encoder, int segment)
            where TSink : struct, IMetalEncoderSink
        {
            for (int slot = 0; slot < _recorded; slot++)
            {
                ref SlotRecord record = ref _slots[slot];
                if (record.Work != SlotWork.Full) continue;

                ReadOnlySpan<MetalBoundResource> bindings = record.Bound.Resources;
                for (int binding = 0; binding < bindings.Length; binding++)
                {
                    // FALSE MEANS DO NOT BIND IT FOR THIS STAGE, which is 2.2b's rule and not a miss to work
                    // around. The emission omits an argument the stage does not reference.
                    if (!table.TryGetIndex(slot, binding, stage, out MetalIndexTableEntry entry)) continue;

                    RequireSpaceAgrees(entry, bindings[binding], slot, binding, stage);
                    _batch.Add(entry.Space, entry.Index, bindings[binding].Handle,
                        ComposedOffset(bindings[binding], segment, record.DynamicOffset));
                }
            }

            _batch.Emit(ref sink, stage, encoder);
        }

        void EmitOffsetsOnly<TSink>(ref TSink sink, MetalShaderIndexTable table, MetalShaderStage stage,
            IntPtr encoder, int segment)
            where TSink : struct, IMetalEncoderSink
        {
            for (int slot = 0; slot < _recorded; slot++)
            {
                ref SlotRecord record = ref _slots[slot];
                if (record.Work != SlotWork.OffsetsOnly) continue;

                ReadOnlySpan<MetalBoundResource> bindings = record.Bound.Resources;
                for (int binding = 0; binding < bindings.Length; binding++)
                {
                    // ONLY THE BINDINGS THE CALLER'S OFFSET REACHES. A slot takes this arm because its set did
                    // not change, so every other binding's composed offset is the value already in the table:
                    // the range offset is fixed at creation and the frame base is fixed for this recording.
                    if (!bindings[binding].AppliesCallerOffset) continue;
                    if (!table.TryGetIndex(slot, binding, stage, out MetalIndexTableEntry entry)) continue;

                    RequireSpaceAgrees(entry, bindings[binding], slot, binding, stage);
                    sink.SetBufferOffset(stage, encoder,
                        ComposedOffset(bindings[binding], segment, record.DynamicOffset), (uint)entry.Index);
                }
            }
        }

        // THE ONLY ARITHMETIC ON THIS PATH (M-M4), and the one place the row 8 correction lands: the base is the
        // segment THIS RECORDING captured, read through the ring rather than from the allocator's live segment.
        //
        // THE REFUSAL IS THE LIVE ONE. The set's own creation-time call passes a zero caller offset and is a
        // tautology there, because the window check already bounds rangeOffset + range by the logical size and
        // the stride is that size rounded up. Here the caller's real per-draw offset is in hand, and it fails on
        // the last frame slot for an offset five shipped renderers pass.
        //
        // AN UNRINGED BINDING HAS NO SEGMENT TO LEAVE, so it composes the last two terms and is not checked here:
        // its window was bounded against the buffer's own size at set creation.
        static nuint ComposedOffset(in MetalBoundResource bound, int segment, uint callerDynamicOffset)
        {
            uint caller = bound.AppliesCallerOffset ? callerDynamicOffset : 0;

            MetalUniformRing? ring = bound.Ring;
            if (ring is null) return (nuint)((ulong)bound.RangeOffset + caller);

            MetalRingStride.RequireBindWindowFits(
                bound.RangeOffset, caller, bound.Range, ring.SegmentStrideBytes);

            return (nuint)(ring.SegmentBaseBytes(segment) + bound.RangeOffset + caller);
        }

        // WHAT SEPARATES THE THREE ARMS, IN ONE PLACE. Read the order: an invalid stamp beats a clean flag,
        // because a slot that was flushed and never re-recorded is still owed a rebind after an encoder boundary
        // took the whole argument table with it (M-R4).
        static SlotWork WorkFor(ref SlotRecord record, ulong epoch)
        {
            if (!record.Bound.IsBound) return SlotWork.None;
            if (!record.Emitted.IsValidIn(epoch)) return SlotWork.Full;
            if (!record.Dirty) return SlotWork.None;

            return ReferenceEquals(record.EmittedBindings, record.Bound.Bindings)
                ? SlotWork.OffsetsOnly
                : SlotWork.Full;
        }

        // THE KIND CHECK AT THE BIND, which is pin 4's question asked one layer down. Row 11 compares a
        // pipeline's whole declared layout array against the table's reflected one at creation, and that is the
        // right place for it. This is the same disagreement seen through ONE binding, and it costs one enum
        // compare: the space decides which of Metal's three argument tables the resource is written into, so a
        // disagreement puts a texture where a buffer was expected and nothing downstream reports it.
        static void RequireSpaceAgrees(in MetalIndexTableEntry entry, in MetalBoundResource bound, int slot,
            int binding, MetalShaderStage stage)
        {
            if (entry.Space == bound.Space) return;

            throw new InvalidOperationException(
                "A native Metal bind at set " + slot.ToString(CultureInfo.InvariantCulture) + " binding "
                + binding.ToString(CultureInfo.InvariantCulture) + " holds a resource resolved into the "
                + bound.Space.Word() + " argument table, and the " + stage.ToString().ToLowerInvariant()
                + " stage's binding table puts that element at [[" + entry.Space.Word() + "("
                + entry.Index.ToString(CultureInfo.InvariantCulture) + ")]]. The set was built against a "
                + "resource layout that disagrees with the shader's own reflection, which is what the pipeline's "
                + "layout shape check refuses at creation. Binding it anyway would put a resource of one kind "
                + "where another was expected, with nothing downstream to report it.");
        }

        MetalShaderIndexTable RequireIndexTable()
            => _table ?? throw new InvalidOperationException(
                "A native Metal draw or dispatch flushed resource-set binds with no pipeline bound. The index a "
                + "resource lands at is a fact about the emission rather than about the layout (M-B1), so there "
                + "is no table to resolve a bind through until a pipeline names its program. Call SetPipeline "
                + "before the first draw of a pass.");

        static void RequireEncoder(IntPtr encoder)
        {
            if (encoder != IntPtr.Zero) return;

            throw new InvalidOperationException(
                "A native Metal bind flush was given a nil encoder. A message to nil is a silent no-op in "
                + "Objective-C, so the binds would go nowhere and the slots would be marked clean, which is a "
                + "frame that renders against stale argument tables with nothing reported. The pass schedule "
                + "answers IntPtr.Zero for M-W5's orphan target, and a draw owes that arm before it reaches "
                + "here.");
        }

        void EnsureSlot(uint slot)
        {
            if (slot > MaxSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot,
                    "A native Metal resource set was bound at set " + slot.ToString(CultureInfo.InvariantCulture)
                    + ", past the " + MaxSlot.ToString(CultureInfo.InvariantCulture) + " this backend records. "
                    + "The widest shipped pipeline declares two, and the cap exists so a wild slot cannot size "
                    + "an array by itself.");
            }

            if (slot < (uint)_slots.Length) return;

            int size = _slots.Length;
            while (slot >= (uint)size) size *= 2;
            Array.Resize(ref _slots, size);
        }

        // A THIRD ARM AT FLUSH TIME AND NOT A THIRD STATE ON THE RECORD. See the class note: this is derived from
        // what was EMITTED, which is the question setBufferOffset:'s precondition actually asks.
        enum SlotWork
        {
            None = 0,
            Full = 1,
            OffsetsOnly = 2,
        }

        struct SlotRecord
        {
            internal MetalBoundSet Bound;
            internal uint DynamicOffset;
            internal bool Dirty;

            // WHAT IS IN THE ARGUMENT TABLE RIGHT NOW, and the epoch it got there in. The pair is the
            // offsets-only arm's precondition: this set's resources are in THIS encoder's table.
            internal MetalBoundResource[]? EmittedBindings;
            internal MetalEncoderMark Emitted;

            // Transient, one flush wide, so the three arms are classified once rather than once per stage.
            internal SlotWork Work;
        }
    }
}
