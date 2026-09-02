using System;
using System.Globalization;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// EVERYTHING A BIND NEEDS FROM A RESOURCE SET, AS PLAIN DATA, and the shape decision V-D2 obliges row 11 to
    /// hold instead of the set itself.
    /// <para>
    /// A <see cref="VulkanResourceSet"/> HOLDS THE DESCRIPTOR POOL, because it frees itself back into one. A
    /// per-slot record with a field of that type would therefore put <c>vkAllocateDescriptorSets</c> into the
    /// recorder's field graph and <c>VulkanRecordingUnreachabilityTests</c> would fail, which is the obligation row
    /// 10 wrote down for this row. Everything here is a handle, a handle, and an array of ring-plus-integers: no
    /// route to the pool, no route to the descriptor seam, nothing a draw could allocate through.
    /// </para>
    /// <para>
    /// THE ARRAY IS HELD BY REFERENCE AND NEVER COPIED, so a bind allocates nothing. Neither side mutates it: the
    /// set writes it once at creation and a bind reads it.
    /// </para>
    /// </summary>
    /// <param name="DescriptorSet">The <c>VkDescriptorSet</c> a bind names. Zero means the slot holds no set, which
    /// is what clause 5's skip is about.</param>
    /// <param name="SetLayout">The set's SHARED <c>VkDescriptorSetLayout</c>, which is what the validation build
    /// compares against the pipeline layout's own at that index (V-R7).</param>
    /// <param name="DynamicUniforms">The set's dynamic uniform descriptors in BINDING ORDER, which is the order
    /// <c>pDynamicOffsets</c> is positional in.</param>
    /// <param name="Images">Every image this set binds, with the layout that binding needs it in (row 15,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/525). Null for a set that binds none, which is most of
    /// them.</param>
    /// <param name="StorageBuffers">The <c>VkBuffer</c> of every STORAGE buffer this set binds, which is the
    /// buffer half of the dependent-dispatch hazard set (V-C2). Null when it binds none.</param>
    internal readonly record struct VulkanBoundSet(
        ulong DescriptorSet, ulong SetLayout, VulkanDynamicUniform[]? DynamicUniforms,
        VulkanBoundImage[]? Images, ulong[]? StorageBuffers)
    {
        /// <summary>Whether this record names a set at all. False for a slot never bound and for one bound to
        /// null, which the flush treats identically because they are.</summary>
        internal bool IsBound => DescriptorSet != 0;

        /// <summary>How many entries this set contributes to a run's <c>pDynamicOffsets</c>.</summary>
        internal int DynamicUniformCount => DynamicUniforms?.Length ?? 0;

        /// <summary>The images this set binds, empty rather than null when it binds none, so the pre-command
        /// transition loop has one shape.</summary>
        internal ReadOnlySpan<VulkanBoundImage> BoundImages => Images;

        /// <summary>The storage buffers this set binds, empty rather than null when it binds none.</summary>
        internal ReadOnlySpan<ulong> BoundStorageBuffers => StorageBuffers;
    }

    /// <summary>
    /// ONE IMAGE A RESOURCE SET BINDS, AS PLAIN DATA, resolved at SET CREATION and read at every draw and dispatch
    /// that binds it. What the compute rule 1 barrier is built out of (V-C1).
    ///
    /// <para><b>THE LAYOUT TRAVELS WITH THE BINDING RATHER THAN WITH THE TEXTURE</b>, because it is a property of
    /// HOW the set binds the image and not of what the image is: one <c>Storage | Sampled</c> texture is
    /// <c>GENERAL</c> where a compute set binds it as a storage image and <c>SHADER_READ_ONLY_OPTIMAL</c> where a
    /// graphics set samples it, and rule 1 is exactly the transition between those two.</para>
    ///
    /// <para><b>AND THE RANGE DOES TOO, WHICH IS WHAT MAKES THE TRACKER'S CONTAINS-THEN-COLLAPSE RULE FIRE
    /// CORRECTLY.</b> A sampled binding names the WHOLE mip chain, because the seam has no texture-view type and
    /// the sampled view is full-chain by construction (V-M11), and a storage binding names mip 0 and every layer,
    /// because a storage-image binding must cover exactly one level. So a texture written by compute and then
    /// sampled presents the tracker with a wider range containing a narrower tracked one, which
    /// <see cref="VulkanLayoutTracker"/> answers with one barrier per piece and then collapses.</para>
    ///
    /// <para><b>IT IS PLAIN DATA FOR THE REASON <see cref="VulkanBoundSet"/> IS.</b> A
    /// <see cref="VulkanTexture"/> field would put <see cref="IVulkanResourceApi"/> into the recorder's graph
    /// through <see cref="VulkanResourceOwner"/> and
    /// <c>VulkanRecordingUnreachabilityTests.TheRecordingType_ReachesNoViewFactory</c> would fail. Handles,
    /// integers and enums, with no route to a factory of any kind.</para>
    /// </summary>
    /// <param name="Image">The image and its range, with the resting layout <c>End</c> puts it back to.</param>
    /// <param name="Layout">The layout this binding needs it in: <c>SHADER_READ_ONLY_OPTIMAL</c> for a sampled
    /// image, <c>GENERAL</c> for a storage image.</param>
    /// <param name="Storage">Whether this binding can WRITE it, which is what puts it into the
    /// dependent-dispatch hazard set (V-C2). The seam carries no read-only storage flag, so every storage binding
    /// is treated as a write.</param>
    internal readonly record struct VulkanBoundImage(
        VulkanTrackedImage Image, ImageLayout Layout, bool Storage);

    /// <summary>
    /// THE SCHEDULE OF DECISIONS V-R5, V-R6 AND V-R7 (section 6.2), and nothing else. It decides WHICH
    /// <c>vkCmdBindDescriptorSets</c> calls to make and hands them to an <see cref="IVkCmdSink"/>, so the real sink
    /// and the counting sink run ONE implementation of it and the device-free budget measures the shipped schedule
    /// rather than a second copy.
    ///
    /// <list type="number">
    /// <item><description>A resource-set bind RECORDS ONLY, into a per-slot <c>(set, engineDynamicOffset)</c>
    /// array, marking the slot dirty when either differs from what is recorded
    /// (<see cref="Record"/>).</description></item>
    /// <item><description><c>Draw</c>, <c>DrawIndexed</c> and <c>Dispatch</c> flush every dirty slot through the
    /// pre-command hook (<see cref="Flush{TSink}"/>) and then issue.</description></item>
    /// <item><description>The flush emits ONE call per CONTIGUOUS RUN of dirty slots, with <c>firstSet</c> at the
    /// run's start, carrying <c>pDynamicOffsets</c> for every dynamic descriptor in those sets in set-then-binding
    /// order. A full activation of the engine's shapes is one call and an offsets-only rebind of one set is one
    /// call.</description></item>
    /// <item><description>A pipeline switch invalidates recorded slots from the first INCOMPATIBLE set onward
    /// (<see cref="SetPipelineLayout"/>), and a slot the incoming layout does not declare at all goes out of the
    /// flush's reach until some later layout declares it again. A rebind of the layout already current does
    /// nothing.</description></item>
    /// <item><description>A slot whose recorded set has gone null is SKIPPED.</description></item>
    /// <item><description>Repeated dirty marks between two draws collapse to one flush, which falls out of an
    /// array of slots rather than a list of binds.</description></item>
    /// </list>
    ///
    /// <para><b>TWO STATES, NOT THREE, AND THE THIRD IS NOT MISSING (V-R5).</b> The Direct3D 11 backend carries a
    /// <c>DynamicOffsetsOnly</c> state so an offsets-only rebind can push the constant buffers and skip the
    /// textures and samplers entirely, which on that API is a real saving in native calls. A Vulkan descriptor bind
    /// is ONE call whether one offset moved or every image in the set changed, and <c>pDynamicOffsets</c> is
    /// positional over every dynamic descriptor in the run while every ring-backed uniform's base moves every
    /// frame, so the array is recomposed on any bind regardless. A third state would change no call and skip no
    /// work. It would be bookkeeping, and the shared-code argument for keeping it died with the decision not to
    /// extract a shared recorder (2.2).</para>
    ///
    /// <para><b>WHICH IS ALSO WHY THE "WAS THERE AN OFFSET OVERLOAD" FLAG IS NOT PART OF THE COMPARISON HERE.</b>
    /// The other backend keeps it because a set bound WITH a dynamic offset of zero and one bound WITHOUT one have
    /// to choose different activation paths there. Here they compose the same array and emit the same call, so
    /// folding the no-offset overload into an offset of zero is not a shortcut, it is the absence of a distinction.
    /// The one thing that distinction still buys is a refusal: an offset passed to a set whose layout declares no
    /// dynamic element would be silently dropped, so <see cref="Record"/> refuses a NON-ZERO one by name.</para>
    ///
    /// <para><b>RULE 6 IS THE ONE THAT LOOKS LIKE BOOKKEEPING AND IS NOT.</b> The shadow pass does thousands of
    /// offsets-only rebinds of ONE set per frame. A record that appended per rebind, or compared against a growing
    /// list, would make the frame O(n squared) in the count of rebinds. The record is one struct per SLOT in an
    /// array indexed by slot, replaced in place, so a rebind is a constant-time compare-and-store and the record's
    /// size follows the highest slot ever used.</para>
    ///
    /// <para><b>IDENTITY IS THE <c>VkDescriptorSet</c> HANDLE, not the managed object.</b> One
    /// <see cref="VulkanResourceSet"/> owns exactly one handle for its whole life, so comparing handles compares
    /// sets. The one way that reads wrong is a set disposed and a later set allocated onto the same freed handle,
    /// and binding a disposed set is already undefined on this backend: the free is deferred behind the timeline
    /// precisely because a submission may still be reading it.</para>
    ///
    /// <para><b>A FLUSH REACHES ONLY AS FAR AS THE BOUND PIPELINE LAYOUT DECLARES, which is the half of clause 4 a
    /// switch to a SHORTER layout needs (https://github.com/APKiwiOrg/KhaozEngine/issues/625).</b> The compatible
    /// prefix is bounded by the shorter of the two handle sequences, so a switch from a two-set pipeline to a
    /// one-set one answers 1 and marks set 1 dirty, and set 1 is a slot the incoming layout has no entry for at all.
    /// Binding it is not a redundant bind, it is an invalid call: <c>vkCmdBindDescriptorSets</c> requires
    /// <c>firstSet</c> plus the set count to stay inside the layout's <c>setLayoutCount</c>, and V-R7's draw-time
    /// assertion reads it as a set carrying a layout the pipeline declares 0 for. So the walk stops at the declared
    /// count, and the slots past it stay DIRTY rather than going clean: they owe a bind to the next layout that
    /// declares them. Keeping the record is what makes the trip back one rebind of a set the caller never
    /// re-recorded, and it costs nothing to be sure of, because a prefix can never exceed the shorter length and so
    /// the switch back marks those slots again anyway.</para>
    ///
    /// <para><b>ONE PER BIND POINT PER LIST, AND NOT SYNCHRONISED.</b> Graphics and compute bindings are separate
    /// on this seam and separate in Vulkan, so each gets its own records with its own
    /// <see cref="PipelineBindPoint"/>. Nothing here is locked, on the same grounds as the list that owns it: one
    /// list records on one thread at a time, and the records are that list's alone.</para>
    ///
    /// <para><b>NOTHING HERE MAKES A NATIVE CALL and nothing here reaches a device</b>, which is what lets the
    /// whole schedule, the run cutting, the positional composition and the compatibility prefix be driven by plain
    /// <c>[Fact]</c>s on a machine with no Vulkan loader.</para>
    /// </summary>
    internal sealed class VulkanBindRecords
    {
        /// <summary>
        /// The highest set number a record will grow to cover. Well past anything a shipped pipeline declares (the
        /// widest uses two sets) and past Vulkan's own required minimum of 4 for
        /// <c>maxBoundDescriptorSets</c>, and small enough that a wild slot index cannot allocate its way to an
        /// <see cref="OutOfMemoryException"/>.
        /// </summary>
        internal const uint MaxSlot = 31;

        readonly VulkanDynamicOffsets _offsets = new();
        readonly PipelineBindPoint _bindPoint;
        readonly bool _assertsBoundSetLayouts;

        SlotRecord[] _slots = new SlotRecord[4];
        DescriptorSet[] _run = new DescriptorSet[4];

        // The set-layout sequence of the pipeline layout currently bound, or null when none is. NOT a
        // VulkanResourceLayout array: this is what a switch compares and what the validation build asserts against,
        // and both of those are handle questions.
        ulong[]? _pipelineSetLayouts;
        ulong _pipelineLayout;

        // One past the highest slot ever recorded, which bounds every walk. Follows the highest SLOT and never the
        // number of rebinds (rule 6).
        int _recorded;

        /// <param name="bindPoint">Which of the two bind points these records feed. Fixed for the object's life,
        /// because a record that could answer either would be a record two flushes could disagree about.</param>
        /// <param name="assertsBoundSetLayouts">V-R7's second guard: under <c>KE_VULKAN_VALIDATION</c> the flush
        /// additionally asserts that every bound set's layout IS the current pipeline layout's set layout at that
        /// index. Off by default, because the guard exists to catch a compatibility-prefix mistake and a run
        /// without validation has already accepted that class of risk everywhere else.</param>
        internal VulkanBindRecords(PipelineBindPoint bindPoint, bool assertsBoundSetLayouts = false)
        {
            _bindPoint = bindPoint;
            _assertsBoundSetLayouts = assertsBoundSetLayouts;
        }

        /// <summary>Which bind point a flush of these records names.</summary>
        internal PipelineBindPoint BindPoint => _bindPoint;

        /// <summary>Whether V-R7's draw-time layout assertion is armed on these records.</summary>
        internal bool AssertsBoundSetLayouts => _assertsBoundSetLayouts;

        /// <summary>The <c>VkPipelineLayout</c> a flush binds under, or 0 when no pipeline is bound.</summary>
        internal ulong PipelineLayout => _pipelineLayout;

        /// <summary>The bound pipeline layout's set-layout handles in slot order, empty when none is bound.</summary>
        internal ReadOnlySpan<ulong> PipelineSetLayouts => _pipelineSetLayouts;

        /// <summary>One past the highest slot ever recorded. Rule 6's assertion in one number: it follows the
        /// highest SLOT and never the count of rebinds, so a thousand rebinds of slot zero leave it where one
        /// did.</summary>
        internal int RecordedSlotCount => _recorded;

        /// <summary>
        /// HOW FAR THE BOUND PIPELINE LAYOUT REACHES: its own set count, capped by what has been recorded. One past
        /// the highest slot the next command can name at all, and therefore where every per-command walk over the
        /// records stops.
        /// <para>
        /// THE FLUSH STOPS HERE BECAUSE A BIND PAST IT IS AN INVALID CALL
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/625): <c>vkCmdBindDescriptorSets</c> requires
        /// <c>firstSet</c> plus the set count to stay inside <c>setLayoutCount</c>, and a slot past it has no entry
        /// to be bound against. It is read here rather than folded into the bindable predicate, because a slot out
        /// of reach must KEEP its dirty mark for the next layout that declares it, and every path through that
        /// predicate spends one.
        /// </para>
        /// <para>
        /// <see cref="VulkanDrawRecorder"/>'S BOUND-IMAGE WALK STOPS HERE TOO, from the other end
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/626): a transition past it is not invalid, it is work
        /// for an image no shader on the bound pipeline can read. This is deliberately NOT a bound on DIRTY slots,
        /// which is a different question entirely: dirty is what owes a bind, declared is what can be bound at all,
        /// and a set bound before a dispatch still owes the rule 1 transition at the draw after it without owing a
        /// bind.
        /// </para>
        /// <para>
        /// AND SO DO BOTH OF <see cref="VulkanComputeHazards"/>' WALKS, from a third end
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/632): a slot past it names no resource the dispatch can
        /// read or write, so counting one as written, or as a dependency, only puts back the serialisation the
        /// hazard set exists to avoid. That was the last walk over these records left unbounded, and it was the
        /// only one whose bound decides whether a BARRIER is emitted, which is why it landed on its own argument
        /// rather than with either sibling.
        /// </para>
        /// <para>
        /// WITH NO PIPELINE BOUND IT REACHES EVERYTHING, deliberately. That is the case <c>RequireLayout</c> refuses
        /// by name, and a limit of zero there would turn a draw issued before a pipeline into silence.
        /// </para>
        /// </summary>
        internal int BindableSlotLimit()
            => _pipelineSetLayouts is null ? _recorded : Math.Min(_recorded, _pipelineSetLayouts.Length);

        /// <summary>How many slots the record currently has room for. Grows to cover a slot and never per
        /// rebind.</summary>
        internal int SlotCapacity => _slots.Length;

        /// <summary>The <c>VkDescriptorSet</c> recorded at a slot, or 0 for a slot holding none. For a test and a
        /// diagnostic.</summary>
        internal ulong RecordedSet(uint slot)
            => slot < (uint)_recorded ? _slots[slot].Bound.DescriptorSet : 0;

        /// <summary>The per-draw offset recorded alongside it.</summary>
        internal uint RecordedOffset(uint slot) => slot < (uint)_recorded ? _slots[slot].DynamicOffset : 0;

        /// <summary>
        /// The whole plain-data record at a slot, default for one holding no set. Row 15
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525) walks these before a draw and before a dispatch
        /// for the images each set binds (the rule 1 transition) and the storage resources it names (the
        /// dependent-dispatch hazard set).
        /// <para>
        /// IT ANSWERS FOR EVERY RECORDED SLOT AND NOT ONLY THE DIRTY ONES, deliberately. A set bound before a
        /// dispatch that then moved one of its images to <c>GENERAL</c> is still bound at the next draw, and a
        /// dirty-only walk would miss exactly the case rule 1 exists for. Which slots a caller ASKS about is its
        /// own question, and since https://github.com/APKiwiOrg/KhaozEngine/issues/632 every caller answers it the
        /// same way: the image walk and both of <see cref="VulkanComputeHazards"/>' walks stop at
        /// <see cref="BindableSlotLimit"/>, which is a bound on DECLARED slots and still not on dirty ones.
        /// </para>
        /// </summary>
        internal VulkanBoundSet BoundAt(uint slot) => slot < (uint)_recorded ? _slots[slot].Bound : default;

        /// <summary>Whether the next flush owes this slot a bind. The whole of the state a slot has (V-R5).</summary>
        internal bool IsDirty(uint slot) => slot < (uint)_recorded && _slots[slot].Dirty;

        /// <summary>
        /// CLAUSE 1, RECORD ONLY. No native call, no device contact and no descriptor work: read what a bind needs
        /// out of the set as plain data, compare it against what the slot already holds, store it, and leave the
        /// slot dirty if either the set or the offset moved.
        /// <para>
        /// A MARK IS NEVER LOWERED, which is clause 6. Several records between two draws collapse to one flush, and
        /// a record that matches what is already there does NOT clean a slot that was already owing a bind: the
        /// pending bind has not happened yet.
        /// </para>
        /// <para>
        /// A NON-ZERO OFFSET AGAINST A SET THAT DECLARES NO DYNAMIC ELEMENT IS REFUSED, because nothing would carry
        /// it: the composition adds the caller's offset for the declared-dynamic element alone (V-D4), so an offset
        /// passed to a set without one is silently dropped and the draw reads slot zero of a buffer the caller
        /// meant to index into. A ZERO offset is accepted against any set, because dropping zero changes nothing
        /// and the seam's no-offset overload is exactly that call.
        /// </para>
        /// </summary>
        /// <param name="slot">The set number this binds at.</param>
        /// <param name="set">The set, or null to record that the slot holds none.</param>
        /// <param name="dynamicOffset">The caller's per-draw byte offset, 0 for the overload that passes none.</param>
        internal void Record(uint slot, VulkanResourceSet? set, uint dynamicOffset)
        {
            if (set is not null && dynamicOffset != 0 && set.Layout.DeclaredDynamicCount == 0)
            {
                throw new ArgumentException(
                    "A native Vulkan resource set was bound at set "
                    + slot.ToString(CultureInfo.InvariantCulture)
                    + " with a dynamic offset of "
                    + dynamicOffset.ToString(CultureInfo.InvariantCulture)
                    + " bytes, and its layout declares no dynamic element for that offset to apply to. Every "
                    + "uniform buffer becomes a dynamic descriptor on this backend so the per-frame ring base can "
                    + "be applied (decision V-D4), and the declared flag decides the one thing the caller's own "
                    + "offset attaches to. With no declared element the offset would be dropped and the draw would "
                    + "read the buffer's first slot. Declare the element dynamic in the resource layout, or bind "
                    + "through the overload that passes no offset.",
                    nameof(dynamicOffset));
            }

            EnsureSlot(slot);

            ref SlotRecord record = ref _slots[slot];
            VulkanBoundSet bound = set is null ? default : set.AsBound;

            if (record.Bound.DescriptorSet != bound.DescriptorSet || record.DynamicOffset != dynamicOffset)
            {
                record.Dirty = true;
            }

            record.Bound = bound;
            record.DynamicOffset = dynamicOffset;

            if (slot >= (uint)_recorded) _recorded = (int)slot + 1;
        }

        /// <summary>
        /// CLAUSE 4, THE PIPELINE-SWITCH INVALIDATION (V-R6). Adopt <paramref name="pipelineLayout"/> and mark
        /// every recorded slot from the first INCOMPATIBLE set onward dirty, returning that index so a caller (and
        /// a test) can see how much survived.
        ///
        /// <para><b>THIS IS THE WIRING POINT ROW 13 CALLS</b>
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523), from <c>SetPipeline</c> and
        /// <c>SetComputePipeline</c>, with the pipeline's own <c>VkPipelineLayout</c> and the set-layout sequence it
        /// was created from. The computation and its guard land here, one row early, because the prefix rule is
        /// this row's clause and because a rule landed with its consumer is a rule nobody tested on its own. Until
        /// that row lands, the shipped seam members still refuse and this is reached by the device-free tests
        /// alone.</para>
        ///
        /// <para><b>A REBIND OF THE LAYOUT ALREADY CURRENT DOES NOTHING</b>, which is the fork's pipeline-identity
        /// guard kept in the seat that matters here. Identity is taken on the <c>VkPipelineLayout</c> handle rather
        /// than on the pipeline, and V-D5's content dedup is what makes that the RIGHT question: two pipelines
        /// built from the same set layouts share one layout object, so switching between them invalidates nothing
        /// and must not. Without the dedup this method would answer zero every time, which is the incumbent's
        /// behaviour and the cost section 2.4 declines to pay.</para>
        ///
        /// <para><b>WITH NO PIPELINE PREVIOUSLY BOUND EVERY RECORDED SLOT IS INVALIDATED</b>, and that is correct
        /// rather than conservative: nothing is bound, so nothing survives. It needs no arm of its own because an
        /// empty outgoing sequence has an empty common prefix.</para>
        ///
        /// <para><b>AND A SHORTER INCOMING LAYOUT NEEDS NO ARM HERE EITHER, because the answer is the FLUSH's</b>
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/625). The prefix is bounded by the shorter sequence, so
        /// every slot the incoming layout drops is at or past it and is marked dirty by the loop below, which is the
        /// right mark: those sets really are disturbed. What they are not is bindable, and that is a question about
        /// the layout a bind names rather than about the switch that disturbed them, so the flush answers it by
        /// stopping at the declared count. Clearing them here instead would lose the record the trip back rebinds
        /// from.</para>
        /// </summary>
        /// <param name="pipelineLayout">The incoming <c>VkPipelineLayout</c>, non-zero.</param>
        /// <param name="setLayouts">Its set-layout handles in slot order. Held by reference and never mutated: the
        /// caller's pipeline object owns the array for its life, exactly as
        /// <see cref="VulkanPipelineLayoutCache"/> holds the one it keyed on.</param>
        /// <returns>The compatible prefix, which is also the index of the first set the switch invalidated.</returns>
        internal int SetPipelineLayout(ulong pipelineLayout, ulong[] setLayouts)
        {
            ArgumentNullException.ThrowIfNull(setLayouts);

            if (pipelineLayout == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pipelineLayout), pipelineLayout,
                    "A native Vulkan bind record was handed a null VkPipelineLayout. A pipeline layout is created "
                    + "once per distinct set-layout array and shared, so a null handle means the pipeline was "
                    + "built without one rather than that it declares no sets: a pipeline declaring no sets still "
                    + "has a real, empty pipeline layout.");
            }

            if (pipelineLayout == _pipelineLayout) return setLayouts.Length;

            int prefix = VulkanLayoutCompatibility.CompatiblePrefix(_pipelineSetLayouts, setLayouts);

            _pipelineLayout = pipelineLayout;
            _pipelineSetLayouts = setLayouts;

            InvalidateFrom(prefix);
            return prefix;
        }

        /// <summary>
        /// Mark every recorded slot at or past <paramref name="firstSet"/> dirty. Separated from
        /// <see cref="SetPipelineLayout"/> so the invalidation rule can be driven on its own, and because it is
        /// what any future reason to invalidate would reach for rather than reimplementing.
        /// </summary>
        internal void InvalidateFrom(int firstSet)
        {
            for (int slot = Math.Max(firstSet, 0); slot < _recorded; slot++) _slots[slot].Dirty = true;
        }

        /// <summary>
        /// CLAUSE 2 AND 3, THE PRE-COMMAND HOOK: one <c>vkCmdBindDescriptorSets</c> per CONTIGUOUS RUN of dirty
        /// slots, <c>firstSet</c> at the run's start, and a positional <c>pDynamicOffsets</c> recomposed for each.
        /// Row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525) calls this FIRST in <c>Draw</c>,
        /// <c>DrawIndexed</c> and <c>Dispatch</c>, before the vertex and index binds and before the command itself,
        /// then issues.
        ///
        /// <para><b>WHY A RUN AND NOT A SLOT.</b> <c>vkCmdBindDescriptorSets</c> takes an ARRAY starting at
        /// <c>firstSet</c>, so a full activation of the engine's shapes collapses into one call and an offsets-only
        /// rebind of one set is one call. That is the whole Vulkan argument for the descriptor model, and it is why
        /// <see cref="IVkCmdSink"/> deliberately has no single-set overload: a per-slot entry point would be the
        /// fan-out defect available as an API.</para>
        ///
        /// <para><b>A CLEAN SLOT AND A NULL-SET SLOT BOTH CUT THE RUN, for different reasons that reach the same
        /// place.</b> A clean slot is already bound, so including it would rebind it for nothing. A null-set slot
        /// has no handle to name at all (clause 5), and neither can be a hole in the middle of an array of sets
        /// starting at one index. Both go clean on the way past, so a skip happens once rather than at every
        /// draw.</para>
        ///
        /// <para><b>A SLOT THE BOUND LAYOUT DOES NOT DECLARE ENDS THE WALK INSTEAD, AND DOES NOT GO CLEAN.</b> It is
        /// the third way a slot leaves a run and the only one that is not a skip: a bind naming a set number the
        /// pipeline layout has no entry for is an invalid call rather than a wasted one, and the mark has to survive
        /// so the next layout that declares that set rebinds it. See the class note for the switch this is really
        /// about (https://github.com/APKiwiOrg/KhaozEngine/issues/625).</para>
        ///
        /// <para><b>THE SLOTS GO CLEAN ONLY AFTER THE CALL LANDS.</b> The composition refuses a window that would
        /// leave its own segment (V-M6) and a run's marks must survive that throw: clearing first would mean the
        /// exception escapes the draw, the slots read clean, and the SECOND draw issues nothing for them and
        /// renders against whatever the descriptor slots still hold. That turns a loud, repeatable refusal into one
        /// throw followed by silence.</para>
        /// </summary>
        internal void Flush<TSink>(ref TSink sink) where TSink : struct, IVkCmdSink
        {
            int limit = BindableSlotLimit();

            int slot = 0;
            while (slot < limit)
            {
                if (!IsBindable(slot))
                {
                    _slots[slot].Dirty = false;
                    slot++;
                    continue;
                }

                int runStart = slot;
                int count = 0;

                while (slot < limit && IsBindable(slot))
                {
                    EnsureRun(count + 1);
                    _run[count++] = new DescriptorSet(_slots[slot].Bound.DescriptorSet);
                    slot++;
                }

                // THE NO-PIPELINE REFUSAL COMES FIRST, BEFORE ANY OFFSET IS COMPOSED, and the order is the whole
                // reason the walk above does not compose them as it goes. Both refusals can be true of one flush,
                // and a draw before a pipeline is the earlier and more actionable mistake: reporting an
                // out-of-window offset against a recording that has no layout to bind the sets under would send
                // the reader after the wrong bug entirely.
                RequireLayout(runStart, count);

                // THE COUNT IS RESET ONCE PER RUN rather than once per flush. That single line is the incumbent's
                // own bug not being inherited: its flush resets the batch count and the first set but not the
                // accumulated offset count, so a second batch inside one flush passes a too-large count built from
                // stale entries.
                _offsets.Reset();
                for (int bound = runStart; bound < slot; bound++)
                {
                    _offsets.Append(in _slots[bound].Bound, _slots[bound].DynamicOffset, (uint)bound);
                }

                if (_assertsBoundSetLayouts) AssertBoundSetLayouts(runStart, count);

                sink.BindDescriptorSets(_bindPoint, new PipelineLayout(_pipelineLayout), (uint)runStart,
                    _run.AsSpan(0, count), _offsets.Composed);

                for (int bound = runStart; bound < slot; bound++) _slots[bound].Dirty = false;
            }
        }

        /// <summary>
        /// FORGET EVERYTHING, which is what a fresh <c>VkCommandBuffer</c> holds. Called from
        /// <c>VulkanCommandList.Begin</c>, between the native begin and the recording flag, which is the one
        /// correct place for it: a reset anywhere else is a reset a re-begun list can be observed without.
        /// <para>
        /// THE PIPELINE LAYOUT GOES TOO. A begun buffer has no pipeline bound, so a retained layout would let the
        /// first flush of the next recording claim a compatibility with a pipeline the driver has never seen on
        /// that buffer, and mark clean the very sets that recording has to bind first.
        /// </para>
        /// </summary>
        internal void Reset()
        {
            Array.Clear(_slots, 0, _recorded);
            _recorded = 0;
            _pipelineLayout = 0;
            _pipelineSetLayouts = null;
        }

        // A slot the flush will put into a run: owing a bind and holding a set to bind. Both halves are checked at
        // the run's start and again at every step, because a run is exactly the maximal span where both hold.
        bool IsBindable(int slot) => _slots[slot].Dirty && _slots[slot].Bound.IsBound;

        // A bind names a pipeline layout, so a run with no pipeline bound is not something to round down to
        // nothing: it would be vkCmdBindDescriptorSets with VK_NULL_HANDLE, which is invalid, and the caller's real
        // mistake is a draw before a pipeline.
        void RequireLayout(int firstSet, int count)
        {
            if (_pipelineLayout != 0) return;

            throw new InvalidOperationException(
                "A native Vulkan recording flushed "
                + count.ToString(CultureInfo.InvariantCulture)
                + " descriptor set(s) from set "
                + firstSet.ToString(CultureInfo.InvariantCulture)
                + " with no pipeline bound. vkCmdBindDescriptorSets names the pipeline layout the sets are bound "
                + "against, so there is nothing to bind them under. Bind the pipeline before the resource sets, or "
                + "before the draw that flushes them.");
        }

        // V-R7's SECOND GUARD, and the one that runs on a real device. The first is a device-free test asserting
        // the computed prefix never exceeds the true identical-handle prefix. This one asserts, at the point a draw
        // would consume it, that the sets about to be bound really do satisfy the layout they are being bound
        // under. Under KE_VULKAN_VALIDATION only: it is a per-bind loop over the run, and it exists to catch a
        // mistake in the prefix computation, which is exactly the class a validation run is looking for.
        //
        // ITS PAST-THE-END ARM IS A BACKSTOP NOW RATHER THAN A LIVE CASE, and it stays exactly as strict. The flush
        // stops at the layout's declared count, so a run cannot present a slot the layout has no entry for, and
        // reading such a slot as a declared 0 is what this assertion did about the defect rather than what the
        // defect was (https://github.com/APKiwiOrg/KhaozEngine/issues/625). Loosening it would only make the next
        // way into that state quiet.
        void AssertBoundSetLayouts(int firstSet, int count)
        {
            ulong[] layouts = _pipelineSetLayouts
                ?? throw new InvalidOperationException(
                    "A native Vulkan recording has a pipeline layout with no set-layout sequence behind it, which "
                    + "means something set the handle without the array. The two are adopted together by "
                    + "SetPipelineLayout precisely so this validation assertion always has something to compare.");

            for (int i = 0; i < count; i++)
            {
                int slot = firstSet + i;
                ulong bound = _slots[slot].Bound.SetLayout;
                ulong declared = slot < layouts.Length ? layouts[slot] : 0;

                if (bound == declared && bound != 0) continue;

                throw new InvalidOperationException(
                    "Set "
                    + slot.ToString(CultureInfo.InvariantCulture)
                    + " of a native Vulkan bind carries VkDescriptorSetLayout "
                    + bound.ToString(CultureInfo.InvariantCulture)
                    + " and the bound pipeline layout declares "
                    + declared.ToString(CultureInfo.InvariantCulture)
                    + " at that index. Set layouts are content-deduplicated (V-D5), so identical content is one "
                    + "handle and a mismatch is a real mismatch: either the set does not satisfy the pipeline it "
                    + "is being bound for, or the compatibility prefix computed at the last pipeline switch was "
                    + "too long and left a set marked clean that the driver had already invalidated. This "
                    + "assertion is decision V-R7's draw-time half and runs under "
                    + VulkanValidation.EnvVarName + " only.");
            }
        }

        // Grow the keyed record to cover a slot. Doubling, so a run of binds at rising slots reallocates a handful
        // of times and never per rebind: this is reached only by a slot the record has not seen.
        void EnsureSlot(uint slot)
        {
            if (slot < (uint)_slots.Length) return;

            if (slot > MaxSlot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot,
                    "Set "
                    + slot.ToString(CultureInfo.InvariantCulture)
                    + " is past the "
                    + MaxSlot.ToString(CultureInfo.InvariantCulture)
                    + " a native Vulkan bind record will grow to cover. A slot indexes the pipeline layout's "
                    + "set-layout array, whose length Vulkan itself caps at maxBoundDescriptorSets (required "
                    + "minimum 4), so a number this large is a mismatch rather than a deep binding model.");
            }

            int capacity = _slots.Length;
            while ((uint)capacity <= slot) capacity <<= 1;
            Array.Resize(ref _slots, capacity);
        }

        void EnsureRun(int required)
        {
            if (required <= _run.Length) return;

            int capacity = _run.Length;
            while (capacity < required) capacity <<= 1;
            Array.Resize(ref _run, capacity);
        }

        /// <summary>
        /// ONE SLOT'S RECORD, and the whole of rule 6. A struct in an array indexed by slot, replaced in place on
        /// every bind, so the record's size follows the highest slot ever used and never the number of rebinds.
        /// </summary>
        struct SlotRecord
        {
            /// <summary>What a bind needs from the set recorded here, as plain data. Default for a slot never bound
            /// or bound to null.</summary>
            internal VulkanBoundSet Bound;

            /// <summary>The per-draw byte offset recorded with it.</summary>
            internal uint DynamicOffset;

            /// <summary>Whether the next flush owes this slot a bind. TWO STATES (V-R5): there is no third.</summary>
            internal bool Dirty;
        }
    }
}
