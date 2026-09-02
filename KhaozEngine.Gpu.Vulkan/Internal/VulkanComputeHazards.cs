using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHICH RESOURCES A DISPATCH IN THIS RECORDING HAS WRITTEN, and therefore whether the NEXT dispatch owes a
    /// read-after-write barrier before it runs (V-C2). Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>A SET OF WRITTEN RESOURCES, NOT A BARRIER PER DISPATCH, and that is the whole decision.</b> A
    /// barrier before every dispatch would serialise a run of independent dispatches that share nothing, which is
    /// the common shape (a particle update and an unrelated reduction in one list). A barrier before a dispatch
    /// that BINDS something an earlier one wrote is the dependency the classic ping-pong actually has, and it is
    /// the one the design asks for in as many words.</para>
    ///
    /// <para><b>EVERY STORAGE BINDING COUNTS AS A WRITE, because the seam cannot say otherwise.</b>
    /// <see cref="GpuResourceKind"/> has one structured-buffer kind for both directions and one storage-image
    /// kind, so a read-only storage binding is not expressible and assuming a write is the only safe reading.
    /// Assuming a read would drop the barrier on exactly the chain it exists for.</para>
    ///
    /// <para><b>AND EVERY BINDING COUNTS AS A READ</b>, sampled and storage alike, which is why the check below
    /// walks all of a set's images rather than its storage ones. A dispatch that SAMPLES what an earlier dispatch
    /// wrote as a storage image is the same hazard, and a second dispatch writing what the first wrote is a
    /// write-after-write with the same answer.</para>
    ///
    /// <para><b>THE SET IS CLEARED BY THE BARRIER RATHER THAN DECREMENTED.</b>
    /// <see cref="VulkanDispatchBarrier.ReadAfterWrite"/> is a GLOBAL memory barrier, so it orders every earlier
    /// shader write against every later shader access at once: once one is emitted there is no resource left in
    /// this recording whose earlier write is still unordered. Keeping the entries would emit a second barrier for
    /// a dependency the first one already covered.</para>
    ///
    /// <para><b>THIS IS NOT A SEAM CONTRACT CHANGE.</b> Rule 2 is honoured AS WRITTEN and no seam member is added:
    /// the portable contract still says a dependent dispatch chain needs <c>End</c>, <c>Submit</c> and
    /// <c>WaitForIdle</c>, because the drain is what the SEAM guarantees and not what any one backend happens to
    /// need. All three engine-owned backends order a dependent chain natively, by three different mechanisms
    /// (hazard tracking on Direct3D 11, this barrier on Vulkan, the compute encoder's default SERIAL dispatch type
    /// on Metal), so a consumer that drops the drain because the machine it was written on tolerated it is relying
    /// on a backend property the seam never promised. That quorum is recorded in full on
    /// <c>IGpuCommandList.SetComputePipeline</c>. What this is, is EVIDENCE for the automatic-hazard seam
    /// capability (https://github.com/APKiwiOrg/KhaozEngine/issues/461).</para>
    ///
    /// <para><b>BOTH WALKS BELOW STOP AT THE BOUND PIPELINE LAYOUT'S DECLARED SET COUNT</b>
    /// (<see cref="VulkanBindRecords.BindableSlotLimit"/>), which is where the bind flush stops because a bind past
    /// it is an invalid call (https://github.com/APKiwiOrg/KhaozEngine/issues/625) and where
    /// <see cref="VulkanDrawRecorder"/>'s image walk stops because a transition past it is wasted work
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/626). This was the last walk over the records left
    /// unbounded (https://github.com/APKiwiOrg/KhaozEngine/issues/632), and it was left deliberately: it is the
    /// only one whose bound decides whether a BARRIER is EMITTED, so it owes an argument of its own rather than
    /// either of theirs.</para>
    ///
    /// <para><b>AND THE ARGUMENT IS THAT THE STORAGE-BINDING CLAUSE ABOVE IS ABOUT DIRECTION, NOT REACH.</b> That
    /// clause assumes a WRITE because the seam cannot tell a read-only storage binding from a read-write one, and
    /// guessing the other way would drop the barrier the whole chain exists for. It says nothing about a set the
    /// dispatch cannot access AT ALL, which is what a slot past the limit is: it is not bound to the dispatch, the
    /// bound layout has no entry at that index, and no shader on the pipeline can name it. The slots this bound
    /// drops are therefore not bindings of unknown direction, they are bindings of no access, and dropping them
    /// cannot lose a hazard. The one thing it must not become is a bound on DIRTY slots, for the reason
    /// <see cref="VulkanBindRecords.BoundAt"/> gives: a set bound before an earlier dispatch is still bound at
    /// this one and its writes are still this recording's.</para>
    ///
    /// <para><b>WHICH IS WHY THE OLD BOUND WAS CONSERVATIVE RATHER THAN WRONG, AND WHY IT STILL COST.</b> A compute
    /// pipeline switch to a layout declaring fewer sets leaves the dropped slots recording their sets on purpose,
    /// so the trip back rebinds them. Walking those could only ADD a resource to the written set or ANSWER yes to a
    /// dependency, so the error was an extra global memory barrier and never a missing one. But V-C2 exists
    /// precisely so a run of independent dispatches is not serialised by a barrier each, and a stale slot put one
    /// back. <c>VulkanComputeHazardWalkTests</c> pins both halves.</para>
    ///
    /// <para><b>LIST-LOCAL AND UNSYNCHRONISED</b>, on the same grounds as everything else a recording holds: one
    /// list records on one thread at a time, and a set shared between two lists would be exactly the shared
    /// record-time state V-F7 eliminates on the layout side.</para>
    /// </summary>
    internal sealed class VulkanComputeHazards
    {
        readonly HashSet<ulong> _written = new();

        /// <summary>How many distinct resources an earlier dispatch in this recording wrote and no barrier has
        /// ordered yet. For the tests, and the number that reading zero means the next dispatch owes
        /// nothing.</summary>
        internal int WrittenCount => _written.Count;

        /// <summary>
        /// Whether a dispatch binding <paramref name="records"/> reads or overwrites something an earlier dispatch
        /// in this recording wrote, and therefore owes a barrier before it runs.
        /// </summary>
        /// <param name="records">The COMPUTE bind records as they stand, whose DECLARED slots are what the
        /// dispatch will bind.</param>
        internal bool NeedsBarrier(VulkanBindRecords records)
        {
            if (_written.Count == 0) return false;

            // READ ONCE, like every other walk over the records: nothing this one does can move the limit, because
            // asking a question binds no pipeline.
            uint limit = (uint)records.BindableSlotLimit();

            for (uint slot = 0; slot < limit; slot++)
            {
                VulkanBoundSet bound = records.BoundAt(slot);
                if (!bound.IsBound) continue;

                foreach (VulkanBoundImage image in bound.BoundImages)
                {
                    if (_written.Contains(image.Image.Image)) return true;
                }

                foreach (ulong buffer in bound.BoundStorageBuffers)
                {
                    if (_written.Contains(buffer)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Record everything the dispatch about to run can write: every STORAGE image and every storage buffer the
        /// sets it really binds name. Called AFTER the dispatch is emitted, because a dispatch does not depend on
        /// its own writes.
        /// </summary>
        /// <param name="records">The compute bind records as they stand.</param>
        internal void NoteWrites(VulkanBindRecords records)
        {
            // BOUNDED IDENTICALLY TO THE QUESTION ABOVE, and that is load-bearing rather than tidy: a write walk
            // that reached further would put a resource back into the set the barrier had just cleared, and the
            // next dispatch to bind it would pay for a write no dispatch in this recording made.
            uint limit = (uint)records.BindableSlotLimit();

            for (uint slot = 0; slot < limit; slot++)
            {
                VulkanBoundSet bound = records.BoundAt(slot);
                if (!bound.IsBound) continue;

                foreach (VulkanBoundImage image in bound.BoundImages)
                {
                    if (image.Storage) _written.Add(image.Image.Image);
                }

                foreach (ulong buffer in bound.BoundStorageBuffers) _written.Add(buffer);
            }
        }

        /// <summary>
        /// Forget every write, which is what a barrier has just made unnecessary and what a fresh
        /// <c>VkCommandBuffer</c> has none of. Called from the dispatch path immediately after a barrier is
        /// emitted, and from <c>VulkanCommandList.Begin</c> for the reason every other recorder state is reset
        /// there: the writes belonged to a recording that was discarded.
        /// </summary>
        internal void Clear() => _written.Clear();
    }
}
