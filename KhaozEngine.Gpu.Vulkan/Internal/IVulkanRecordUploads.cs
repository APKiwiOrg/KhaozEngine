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
    /// </summary>
    internal interface IVulkanRecordUploads
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
    /// <para>Implemented by <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/522">row 12</see>, which
    /// owns the deferred begin and therefore knows whether a pass is open. Null until then, and null is correct
    /// rather than unbuilt: with no rendering there is no pass to end.</para>
    /// </summary>
    internal interface IVulkanRenderingScope
    {
        /// <summary>End the active <c>vkCmdBeginRendering</c> scope if there is one, and do nothing if there is
        /// not. Idempotent, because a run of uploads asks once each.</summary>
        void EndActiveRendering();
    }
}
