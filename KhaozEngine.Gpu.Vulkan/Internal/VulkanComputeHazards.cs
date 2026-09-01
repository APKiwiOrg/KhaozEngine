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
    /// <para><b>BOTH WALKS BELOW COVER EVERY RECORDED SLOT, INCLUDING ONES THE BOUND PIPELINE LAYOUT DOES NOT
    /// DECLARE</b>, which is the one place in this backend where that is still true
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/632). The bind flush stops at the declared set count
    /// because a bind past it is invalid (https://github.com/APKiwiOrg/KhaozEngine/issues/625) and
    /// <see cref="VulkanDrawRecorder"/>'s image walk stops there because a transition past it is wasted
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/626). Here the error runs the safe way: a slot a compute
    /// pipeline switch left recorded but undeclared can only ADD a resource to the written set or ANSWER yes to a
    /// dependency, so the cost is a global memory barrier that was not owed and never a missing one. Bounding it
    /// is a change to a BARRIER decision rather than to wasted work, so it wants its own argument against the
    /// storage-binding clause above, and it has not been made.</para>
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
        /// <param name="records">The COMPUTE bind records as they stand, whose recorded slots are what the
        /// dispatch will bind.</param>
        internal bool NeedsBarrier(VulkanBindRecords records)
        {
            if (_written.Count == 0) return false;

            for (uint slot = 0; slot < (uint)records.RecordedSlotCount; slot++)
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
        /// Record everything the dispatch about to run can write: every STORAGE image and every storage buffer its
        /// bound sets name. Called AFTER the dispatch is emitted, because a dispatch does not depend on its own
        /// writes.
        /// </summary>
        /// <param name="records">The compute bind records as they stand.</param>
        internal void NoteWrites(VulkanBindRecords records)
        {
            for (uint slot = 0; slot < (uint)records.RecordedSlotCount; slot++)
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
