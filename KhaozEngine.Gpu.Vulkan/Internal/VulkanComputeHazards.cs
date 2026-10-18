using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHICH RESOURCES A COMMAND IN THIS RECORDING HAS WRITTEN OR READ, and therefore whether the NEXT dispatch
    /// or draw owes a barrier before it runs (V-C2). Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>A SET OF ACCESSED RESOURCES, NOT A BARRIER PER DISPATCH, and that is the whole decision.</b> A
    /// barrier before every dispatch would serialise a run of independent dispatches that share nothing, which is
    /// the common shape (a particle update and an unrelated reduction in one list). A barrier before a command
    /// that BINDS something an earlier command wrote is the dependency the classic ping-pong actually has, and it
    /// is the one the design asks for in as many words.</para>
    ///
    /// <para><b>THE SAME QUESTION SERVES BOTH BIND POINTS.</b> <see cref="NeedsBarrier"/> takes either the compute
    /// or the graphics records, so a draw whose vertex or fragment stage reads a storage buffer an earlier
    /// dispatch wrote is asked exactly what a dependent dispatch is asked. That is the seam's in-list buffer
    /// ordering on this backend: <see cref="VulkanDrawRecorder"/> asks it before a draw's transitions and begin,
    /// and ends an open rendering scope first when the answer is yes.</para>
    ///
    /// <para><b>A STORAGE BUFFER HAS A DIRECTION, A STORAGE IMAGE DOES NOT.</b>
    /// <see cref="GpuResourceKind"/> has a read-only and a read-write structured-buffer kind, so a set records
    /// which of its buffers a command can write (<see cref="VulkanBoundSet.BoundWritableStorageBuffers"/>) and
    /// only those are noted as written. A storage image has one kind, so every storage image binding is still
    /// treated as a write, and assuming a read would drop the barrier on exactly the chain it exists for.</para>
    ///
    /// <para><b>A READ-ONLY BINDING IS STILL REMEMBERED, AS A READ.</b> A later command that WRITES a
    /// buffer an earlier draw or dispatch read owes an execution dependency, or the write can land under the read.
    /// Before buffers had a direction that edge was covered by counting the read as a write. Now it is covered by
    /// remembering the read and asking whether a later binding can write it, so dropping the over-count drops no
    /// barrier a write-after-read needs.</para>
    ///
    /// <para><b>AND EVERY BINDING COUNTS AS A READ</b>, sampled and storage alike, which is why the compute check
    /// walks all of a set's images rather than its storage ones. A dispatch that SAMPLES what an earlier dispatch
    /// wrote as a storage image is the same hazard, and a second dispatch writing what the first wrote is a
    /// write-after-write with the same answer. A DRAW's sampled images are the one exception: rule 1's layout
    /// transition already orders them, so the graphics arm asks only about its storage images and buffers.</para>
    ///
    /// <para><b>THE SET IS CLEARED BY THE BARRIER RATHER THAN DECREMENTED.</b>
    /// <see cref="VulkanDispatchBarrier.ReadAfterWrite"/> is a GLOBAL memory barrier that names the compute stage
    /// and every graphics stage on BOTH sides, so it orders every earlier shader access against every later
    /// shader access at once: once one is emitted there is no resource left in this recording whose earlier
    /// access is still unordered, whichever bind point reads it next. Keeping the entries would emit a second
    /// barrier for a dependency the first one already covered. A compute-only destination could not be cleared
    /// like this, because a later vertex reader of a buffer published to compute would find its entry gone.</para>
    ///
    /// <para><b>ALL WALKS BELOW STOP AT THE BOUND PIPELINE LAYOUT'S DECLARED SET COUNT</b>
    /// (<see cref="VulkanBindRecords.BindableSlotLimit"/>), which is where the bind flush stops because a bind past
    /// it is an invalid call (https://github.com/APKiwiOrg/KhaozEngine/issues/625) and where
    /// <see cref="VulkanDrawRecorder"/>'s image walk stops because a transition past it is wasted work
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/626). This was the last walk over the records left
    /// unbounded (https://github.com/APKiwiOrg/KhaozEngine/issues/632), and it was left deliberately: it is the
    /// only one whose bound decides whether a BARRIER is EMITTED, so it owes an argument of its own rather than
    /// either of theirs.</para>
    ///
    /// <para><b>AND THE ARGUMENT IS THAT THE STORAGE-IMAGE CLAUSE ABOVE IS ABOUT DIRECTION, NOT REACH.</b> That
    /// clause assumes a WRITE because the seam cannot tell a read-only storage image from a read-write one, and
    /// guessing the other way would drop the barrier the whole chain exists for. It says nothing about a set the
    /// command cannot access AT ALL, which is what a slot past the limit is: it is not bound to the command, the
    /// bound layout has no entry at that index, and no shader on the pipeline can name it. The slots this bound
    /// drops are therefore not bindings of unknown direction, they are bindings of no access, and dropping them
    /// cannot lose a hazard. The one thing it must not become is a bound on DIRTY slots, for the reason
    /// <see cref="VulkanBindRecords.BoundAt"/> gives: a set bound before an earlier dispatch is still bound at
    /// this one and its writes are still this recording's. A graphics pipeline that drops a set and a later one
    /// that restores it is the same rule from the draw side: the dropped slot is asked nothing while it is out of
    /// reach and is asked again the moment a layout declares it.</para>
    ///
    /// <para><b>WHICH IS WHY THE OLD BOUND WAS CONSERVATIVE RATHER THAN WRONG, AND WHY IT STILL COST.</b> A compute
    /// pipeline switch to a layout declaring fewer sets leaves the dropped slots recording their sets on purpose,
    /// so the trip back rebinds them. Walking those could only ADD a resource to the accessed sets or ANSWER yes
    /// to a dependency, so the error was an extra global memory barrier and never a missing one. But V-C2 exists
    /// precisely so a run of independent dispatches is not serialised by a barrier each, and a stale slot put one
    /// back. <c>VulkanComputeHazardWalkTests</c> pins both halves.</para>
    ///
    /// <para><b>LIST-LOCAL AND UNSYNCHRONISED</b>, on the same grounds as everything else a recording holds: one
    /// list records on one thread at a time, and a set shared between two lists would be exactly the shared
    /// record-time state V-F7 eliminates on the layout side. That is also why ordering ACROSS lists is not this
    /// type's to give: a second list starts with nothing written.</para>
    /// </summary>
    internal sealed class VulkanComputeHazards
    {
        readonly HashSet<ulong> _written = new();
        readonly HashSet<ulong> _read = new();

        /// <summary>How many distinct resources an earlier command in this recording wrote and no barrier has
        /// ordered yet. For the tests, and the number that reading zero means no later reader owes
        /// anything.</summary>
        internal int WrittenCount => _written.Count;

        /// <summary>
        /// Whether a command binding <paramref name="records"/> reads or overwrites something an earlier command
        /// in this recording wrote, or writes a buffer an earlier command read, and therefore owes a barrier
        /// before it runs.
        /// </summary>
        /// <param name="records">The COMPUTE or GRAPHICS bind records as they stand, whose DECLARED slots are
        /// what the command will bind.</param>
        internal bool NeedsBarrier(VulkanBindRecords records)
        {
            if (_written.Count == 0 && _read.Count == 0) return false;

            // READ ONCE, like every other walk over the records: nothing this one does can move the limit, because
            // asking a question binds no pipeline.
            uint limit = (uint)records.BindableSlotLimit();

            // A DRAW'S SAMPLED IMAGES ARE RULE 1's, NOT THIS QUESTION'S. A storage write leaves the image in
            // GENERAL and the draw's sampled binding needs SHADER_READ_ONLY_OPTIMAL, so the layout tracker emits
            // a transition whose source side already names every command and every memory write. Asking here as
            // well would put a second barrier in front of every compute-to-sampled handoff for nothing.
            bool graphics = records.BindPoint == PipelineBindPoint.Graphics;

            for (uint slot = 0; slot < limit; slot++)
            {
                VulkanBoundSet bound = records.BoundAt(slot);
                if (!bound.IsBound) continue;

                foreach (VulkanBoundImage image in bound.BoundImages)
                {
                    if (graphics && !image.Storage) continue;
                    if (_written.Contains(image.Image.Image)) return true;
                }

                foreach (ulong buffer in bound.BoundStorageBuffers)
                {
                    if (_written.Contains(buffer)) return true;
                }

                if (_read.Count == 0) continue;

                foreach (ulong buffer in bound.BoundWritableStorageBuffers)
                {
                    if (_read.Contains(buffer)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Record everything the command can access: every STORAGE image and every WRITABLE storage buffer as
        /// written, and every read-only storage buffer as read. Called AFTER a draw or dispatch is emitted,
        /// because a command does not depend on its own accesses. Fragment-stage writable bindings are writes
        /// just like compute bindings.
        /// </summary>
        /// <param name="records">The compute or graphics bind records as they stand.</param>
        internal void NoteAccesses(VulkanBindRecords records)
        {
            // BOUNDED IDENTICALLY TO THE QUESTION ABOVE, and that is load-bearing rather than tidy: a walk that
            // reached further would put a resource back into the sets the barrier had just cleared, and the next
            // command to bind it would pay for an access no command in this recording made.
            uint limit = (uint)records.BindableSlotLimit();

            for (uint slot = 0; slot < limit; slot++)
            {
                VulkanBoundSet bound = records.BoundAt(slot);
                if (!bound.IsBound) continue;

                foreach (VulkanBoundImage image in bound.BoundImages)
                {
                    if (image.Storage) _written.Add(image.Image.Image);
                }

                ReadOnlySpan<ulong> writable = bound.BoundWritableStorageBuffers;
                foreach (ulong buffer in writable) _written.Add(buffer);

                foreach (ulong buffer in bound.BoundStorageBuffers)
                {
                    if (!writable.Contains(buffer)) _read.Add(buffer);
                }
            }
        }

        /// <summary>
        /// Forget every access, which is what a barrier has just made unnecessary and what a fresh
        /// <c>VkCommandBuffer</c> has none of. Called from the dispatch and draw paths immediately after a barrier
        /// is emitted, and from <c>VulkanCommandList.Begin</c> for the reason every other recorder state is reset
        /// there: the accesses belonged to a recording that was discarded.
        /// </summary>
        internal void Clear()
        {
            _written.Clear();
            _read.Clear();
        }
    }
}
