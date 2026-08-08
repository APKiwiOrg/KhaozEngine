using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE VERTEX AND INDEX BIND SCHEDULE: what <c>SetVertexBuffer</c> and <c>SetIndexBuffer</c> RECORD, and which
    /// <c>vkCmdBindVertexBuffers</c> and <c>vkCmdBindIndexBuffer</c> calls the next draw makes out of those
    /// records. Work-breakdown row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <list type="number">
    /// <item><description><b>A bind RECORDS ONLY</b>, into a per-slot <c>(buffer, offset)</c> array, marking the
    /// slot dirty when either differs from what is recorded. Nothing native happens until a draw.</description>
    /// </item>
    /// <item><description><b>A draw flushes</b>: ONE <c>vkCmdBindVertexBuffers</c> per CONTIGUOUS RUN of dirty
    /// slots, with <c>firstBinding</c> at the run's start, then the index buffer if it moved.</description></item>
    /// <item><description><b>A rebind of what is already recorded does NOTHING AT ALL</b>, buffer and offset both.
    /// That is the same identity guard <c>SetPipeline</c> and <c>SetFramebuffer</c> keep, and here it is what makes
    /// a run of draws over one mesh cost one vertex bind rather than one per draw.</description></item>
    /// <item><description><b>Repeated marks between two draws collapse to one emission</b>, which falls out of an
    /// array of slots rather than a list of binds, and is rule 6 of the descriptor schedule arriving at the other
    /// bind class.</description></item>
    /// </list>
    ///
    /// <para><b>WHY DEFERRED AT ALL, WHEN THE INCUMBENT EMITS AT THE CALL.</b> Veldrid's Vulkan backend issues
    /// <c>vkCmdBindVertexBuffers</c> inside <c>SetVertexBufferCore</c> with no guard, so a renderer that rebinds
    /// the same mesh buffer before every draw of that mesh pays a native call per draw for a state change that did
    /// not happen. Deferring costs one array read per draw and makes the redundant case free, and it is the shape
    /// every other schedule in this backend already has: the descriptor binds record and flush, the framebuffer
    /// bind records and the begin is deferred to the first draw, the viewport and scissor are values a draw
    /// emits.</para>
    ///
    /// <para><b>THE RUN CUTTING IS THE DESCRIPTOR FLUSH'S, NOT A SECOND IDEA.</b> Both bind classes take an array
    /// plus a first index, so a shape that binds slots 0 and 1 in one call is available in both and a per-slot
    /// entry point is the fan-out defect available as an API in both. <see cref="IVulkanDrawEmitter"/> therefore
    /// has no single-slot overload, exactly as <see cref="IVkCmdSink"/> has none.</para>
    ///
    /// <para><b>NONE OF THIS IS ON THE BUDGET SEAM (V-T2).</b> A vertex bind is not one of the three call classes
    /// the frozen marginals gate on, so <see cref="VulkanCountingDrawEmitter"/> tallies nothing here. The schedule
    /// still gets a device-free line to be observed on, for the reason
    /// <see cref="IVulkanRenderApi"/> has one: the run cutting is a decision that can be wrong, and a wrong one
    /// reads the right buffer at the wrong binding, which renders plausible garbage rather than throwing.</para>
    ///
    /// <para><b>NOTHING HERE IS SYNCHRONISED</b>, on the same grounds as the list that owns it: one list records on
    /// one thread at a time and this schedule is that list's alone.</para>
    /// </summary>
    internal sealed class VulkanGeometryBinds
    {
        /// <summary>
        /// The highest vertex binding a record will grow to cover. Vulkan's required minimum for
        /// <c>maxVertexInputBindings</c> is 16, so slots 0 to 15 are the whole portable range, the widest shipped
        /// vertex layout uses two, and a wild slot index cannot allocate its way to an
        /// <see cref="OutOfMemoryException"/>.
        /// </summary>
        internal const uint MaxSlot = 15;

        SlotRecord[] _slots = new SlotRecord[2];

        // GROWN TO THE WIDEST RUN EVER FLUSHED rather than allocated per draw, so a frame that binds the same two
        // vertex buffers every mesh allocates nothing after the first flush.
        ulong[] _runBuffers = new ulong[2];
        ulong[] _runOffsets = new ulong[2];

        ulong _indexBuffer;
        ulong _indexOffset;
        bool _indexSixteenBit;
        bool _indexDirty;

        /// <summary>How many vertex slots have ever been recorded, which is how far a flush scans. For the
        /// tests.</summary>
        internal int SlotCount => _slots.Length;

        /// <summary>Whether the next draw owes a <c>vkCmdBindIndexBuffer</c>.</summary>
        internal bool IndexDirty => _indexDirty;

        /// <summary>The <c>VkBuffer</c> currently recorded at <paramref name="slot"/>, or 0 for a slot never bound.
        /// For the tests and for the diagnostics.</summary>
        internal ulong BufferAt(uint slot) => slot < (uint)_slots.Length ? _slots[slot].Buffer : 0;

        /// <summary>Whether <paramref name="slot"/> owes a bind at the next draw.</summary>
        internal bool IsDirty(uint slot) => slot < (uint)_slots.Length && _slots[slot].Dirty;

        /// <summary>
        /// RECORD a vertex buffer at <paramref name="slot"/>. Marks the slot dirty only when the buffer or the
        /// offset moved.
        /// </summary>
        /// <param name="slot">The vertex binding number.</param>
        /// <param name="buffer">The <c>VkBuffer</c>, non-zero.</param>
        /// <param name="offsetBytes">Where this slot's first vertex lives inside it.</param>
        /// <exception cref="ArgumentOutOfRangeException">The slot is above <see cref="MaxSlot"/>.</exception>
        internal void RecordVertex(uint slot, ulong buffer, ulong offsetBytes)
        {
            RequireSlot(slot);
            EnsureSlots(slot + 1);

            SlotRecord current = _slots[slot];
            if (current.Buffer == buffer && current.Offset == offsetBytes && current.Bound) return;

            _slots[slot] = new SlotRecord(buffer, offsetBytes, Bound: true, Dirty: true);
        }

        /// <summary>
        /// RECORD the index buffer. Marks it dirty only when the buffer, the offset or the element width moved.
        /// </summary>
        /// <param name="buffer">The <c>VkBuffer</c>, non-zero.</param>
        /// <param name="offsetBytes">Where the first index lives.</param>
        /// <param name="sixteenBit">True for <c>VK_INDEX_TYPE_UINT16</c>.</param>
        internal void RecordIndex(ulong buffer, ulong offsetBytes, bool sixteenBit)
        {
            if (_indexBuffer == buffer && _indexOffset == offsetBytes && _indexSixteenBit == sixteenBit
                && buffer != 0)
            {
                return;
            }

            _indexBuffer = buffer;
            _indexOffset = offsetBytes;
            _indexSixteenBit = sixteenBit;
            _indexDirty = true;
        }

        /// <summary>
        /// EMIT WHAT THE RECORDS OWE: one <c>vkCmdBindVertexBuffers</c> per contiguous run of dirty slots, then the
        /// index bind if it moved. Every slot is left clean.
        /// <para>
        /// A RUN IS CUT BY A CLEAN SLOT AND BY AN UNBOUND ONE ALIKE, because <c>vkCmdBindVertexBuffers</c> takes a
        /// dense array from <c>firstBinding</c> and there is no way to skip a binding inside one call. A slot never
        /// bound therefore ends the run rather than being filled with a null handle, which the driver would read as
        /// an unbind of a binding the pipeline may declare.
        /// </para>
        /// </summary>
        /// <param name="emitter">Where the calls go.</param>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        internal void Flush(IVulkanDrawEmitter emitter, ulong commandBuffer)
        {
            ArgumentNullException.ThrowIfNull(emitter);

            int start = -1;
            int count = 0;

            for (int slot = 0; slot <= _slots.Length; slot++)
            {
                bool dirty = slot < _slots.Length && _slots[slot].Dirty && _slots[slot].Bound;

                if (dirty)
                {
                    if (start < 0) start = slot;

                    EnsureRun(count + 1);
                    _runBuffers[count] = _slots[slot].Buffer;
                    _runOffsets[count] = _slots[slot].Offset;
                    count++;
                    _slots[slot] = _slots[slot] with { Dirty = false };
                    continue;
                }

                if (count == 0) continue;

                emitter.BindVertexBuffers(commandBuffer, (uint)start, _runBuffers.AsSpan(0, count),
                    _runOffsets.AsSpan(0, count));
                start = -1;
                count = 0;
            }

            if (!_indexDirty) return;

            _indexDirty = false;
            emitter.BindIndexBuffer(commandBuffer, _indexBuffer, _indexOffset, _indexSixteenBit);
        }

        /// <summary>
        /// FORGET EVERY BIND, which is what a fresh <c>VkCommandBuffer</c> holds: no vertex buffer at any binding
        /// and no index buffer. Called from <c>VulkanCommandList.Begin</c> for the reason
        /// <see cref="VulkanBindRecords.Reset"/> and <see cref="VulkanRenderingSchedule.Reset"/> are called there.
        /// <para>
        /// KEEPING THE RECORDS WOULD LET THE NEXT RECORDING'S FIRST BIND TAKE THE IDENTITY GUARD'S REDUNDANT PATH
        /// and draw out of whatever the driver's own state happened to hold, which is the same argument the
        /// pipeline handles and the bound framebuffer are reset on.
        /// </para>
        /// </summary>
        internal void Reset()
        {
            Array.Clear(_slots, 0, _slots.Length);
            _indexBuffer = 0;
            _indexOffset = 0;
            _indexSixteenBit = false;
            _indexDirty = false;
        }

        void EnsureSlots(uint required)
        {
            if (required <= (uint)_slots.Length) return;

            int capacity = _slots.Length;
            while ((uint)capacity < required) capacity <<= 1;

            Array.Resize(ref _slots, capacity);
        }

        void EnsureRun(int required)
        {
            if (required <= _runBuffers.Length) return;

            int capacity = _runBuffers.Length;
            while (capacity < required) capacity <<= 1;

            Array.Resize(ref _runBuffers, capacity);
            Array.Resize(ref _runOffsets, capacity);
        }

        static void RequireSlot(uint slot)
        {
            if (slot <= MaxSlot) return;

            throw new ArgumentOutOfRangeException(nameof(slot), slot,
                "A native Vulkan vertex buffer was bound at slot "
                + slot.ToString(CultureInfo.InvariantCulture) + ", above the highest this backend records ("
                + MaxSlot.ToString(CultureInfo.InvariantCulture)
                + "). Vulkan's required minimum for maxVertexInputBindings is 16, so a binding above 15 is not "
                + "portable, and the widest shipped vertex layout uses two. Refusing by name beats growing an "
                + "array to whatever index a caller computed wrong.");
        }

        /// <summary>One vertex binding's record.</summary>
        /// <param name="Buffer">The <c>VkBuffer</c> recorded, or 0 for a slot never bound.</param>
        /// <param name="Offset">Its byte offset.</param>
        /// <param name="Bound">Whether anything was ever recorded here, which is what cuts a run at a gap.</param>
        /// <param name="Dirty">Whether the next draw owes this slot a bind.</param>
        readonly record struct SlotRecord(ulong Buffer, ulong Offset, bool Bound, bool Dirty);
    }
}
