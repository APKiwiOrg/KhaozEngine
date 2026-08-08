using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT A COMMAND LIST ASKS FOR WHEN A RECORD-TIME WRITE IS NOT A UNIFORM WRITE (9.3): stage these bytes and
    /// record the copy. The list-facing half of the staging arena.
    ///
    /// <para><b>WHY THE LIST HOLDS AN INTERFACE RATHER THAN THE ARENA.</b> Recording a copy needs the list's
    /// CURRENT <c>VkCommandBuffer</c>, which changes with every slot advance, and the barrier goes through
    /// <see cref="IVkCmdSink"/>, which is consumed through a generic constraint so the JIT can monomorphize it
    /// (V-T2). Neither of those is expressible as a field the list can hold, so the list holds this instead and the
    /// row that owns the sink writes the four-line implementation over
    /// <see cref="VulkanBufferUpload.Record"/>. That keeps this row's half (the arena, the size classes, the
    /// retention cap, the narrowed barrier and the ROUTING DECISION) complete and driven by tests, without
    /// pre-empting the shape of a recorder that does not exist yet.</para>
    ///
    /// <para><b>NULL ON A LIST TODAY, AND THAT IS NOT A PLACEHOLDER.</b> A non-uniform buffer cannot EXIST until
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/519">row 9</see> builds
    /// <c>IGpuResourceFactory</c>, so a list with no uploader can never be handed one, and it refuses by naming
    /// that row. What is live from this row is the decision itself: a ring-backed buffer goes to the ring and
    /// everything else comes here, made once in <see cref="VulkanCommandList.UpdateBuffer{T}(IGpuBuffer, uint,
    /// ReadOnlySpan{T})"/> rather than repeated as a convention at each write site.</para>
    ///
    /// <para><b><see cref="IDisposable"/> BECAUSE THE ARENA'S LIFETIME NEEDS AN OWNER.</b>
    /// <see cref="VulkanCommandList.Dispose"/> disposes this beside its own pool retirement, so row 9's
    /// implementation (which wraps <see cref="VulkanStagingArena"/>) has a place to hand its blocks to
    /// <see cref="IVulkanStagingSource.Destroy"/> when the list dies. The default body below is a no-op rather than
    /// abstract, because a caller with nothing to own (a test double that only counts calls, for instance) is not
    /// obliged to implement a destructor for memory it never allocated.</para>
    /// </summary>
    internal interface IVulkanRecordUploads : IDisposable
    {
        /// <summary>
        /// Stage <paramref name="data"/> and record its copy into <paramref name="destination"/> at
        /// <paramref name="destinationOffsetBytes"/>, with the barrier narrowed to that buffer's usage.
        /// </summary>
        void Upload(IVulkanUploadDestination destination, ulong destinationOffsetBytes, ReadOnlySpan<byte> data);

        /// <summary>
        /// Open <paramref name="slot"/>, giving back the staging blocks it filled last time round. Called by
        /// <c>Begin</c> immediately after the pool ring advanced onto that slot, which is AFTER it waited for that
        /// slot's last submission. Per SLOT rather than per list, because the blocks the previous record filled
        /// belong to a submission that may still be in flight.
        /// </summary>
        void BeginSlot(int slot);

        /// <summary>
        /// Take the rendering scope a staged upload ends before it records its copy. Called ONCE, by
        /// <see cref="VulkanCommandList"/>'s own constructor, with the list itself.
        ///
        /// <para><b>THE LIST WIRES THIS, NOT THE DEVICE.</b> The scope IS the list, and the list takes this
        /// uploader in its constructor, so one of the two edges has to be wired second. The list is the end that
        /// owns both (it disposes this uploader and it implements the scope), so closing the cycle inside its
        /// constructor is what keeps a null scope unreachable on any list that has a rendering seam at all. Wiring
        /// it from the far side instead leaves a construction path that can forget the call, and a forgotten call
        /// is silent: a null scope makes the pass-end a no-op rather than an error, and the copy lands inside an
        /// open render pass instance.</para>
        ///
        /// <para>A no-op by default, for the reason <see cref="IDisposable.Dispose"/> below is: an uploader that
        /// records no copy has no pass to end, so a test double that only counts calls is not obliged to hold a
        /// scope it would never use.</para>
        /// </summary>
        /// <param name="scope">The list this uploader records into.</param>
        void UseRenderingScope(IVulkanRenderingScope scope)
        {
        }

        /// <summary>
        /// Release whatever this uploader owns. A no-op by default: see the class remarks for why a caller with
        /// nothing to release is not required to override it.
        /// </summary>
        void IDisposable.Dispose()
        {
        }
    }

    /// <summary>
    /// THE ONE THING A STAGED UPLOAD NEEDS FROM THE RENDERING STATE: end the pass, because a copy cannot be
    /// recorded inside one.
    ///
    /// <para><b>THE PASS SPLIT IS UNAVOIDABLE HERE AND IS AVOIDED ENTIRELY BY THE RING</b>, which is the whole
    /// contrast section 9.2 and 9.3 draw between the two paths. <c>vkCmdCopyBuffer</c> is not permitted inside a
    /// <c>vkCmdBeginRendering</c> scope, so a bulk upload recorded mid-frame ends the pass, copies, barriers, and
    /// lets the next draw begin it again. That is the same cost the incumbent pays for EVERY uniform write, and the
    /// ring's entire value is that a uniform write no longer comes here at all.</para>
    ///
    /// <para><b>IMPLEMENTED BY <see cref="VulkanCommandList"/></b>, which owns the deferred begin
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/522">row 12</see>) and therefore knows whether
    /// a pass is open. Its body is the SAME <c>EndRenderingBeforeIllegalCommand</c> helper a dispatch, a resolve
    /// and a mip generation call, so the upload path takes the clear-only flush with it and cannot drift from the
    /// other commands that may not appear inside a render pass instance. The list hands itself over through
    /// <see cref="IVulkanRecordUploads.UseRenderingScope"/>. Still null on a list built with no rendering seam,
    /// which is only a list a test constructed, and null is correct rather than unbuilt there: with no rendering
    /// there is no pass to end.</para>
    /// </summary>
    internal interface IVulkanRenderingScope
    {
        /// <summary>End the active <c>vkCmdBeginRendering</c> scope if there is one, and do nothing if there is
        /// not. Idempotent, because a run of uploads asks once each.</summary>
        void EndActiveRendering();
    }
}
