using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DECISION V-R6's WHOLE COMPUTATION, in one pure function, and the thing decision V-R7 guards.
    ///
    /// <para><b>THE RULE, STATED SO IT CAN BE TESTED.</b> Two pipeline layouts are compatible for set N when they
    /// were created with identically defined set layouts for sets 0 through N and identical push-constant ranges.
    /// <see cref="VulkanDescriptorSetLayoutCache"/>'s content dedup (V-D5) turns "identically defined" into HANDLE
    /// IDENTITY, and V-D8 declines push constants outright, so what is left is the LONGEST COMMON PREFIX of the two
    /// layouts' set-layout handle sequences. Every set at or past that prefix is invalidated by the switch.</para>
    ///
    /// <para><b>THE DIRECTION IS THE OPPOSITE OF THE DIRECT3D 11 BACKEND'S AND THAT IS NOT AN ACCIDENT.</b> There a
    /// pipeline switch drains the pending sets under the OUTGOING layouts and then forgets the records, because the
    /// layout array decides register numbering. Here nothing is renumbered: the sets that survive a switch are the
    /// ones Vulkan itself says survive it, and the records for the rest are marked dirty so the next draw rebinds
    /// them. Same clause slot in the schedule, a different reason, a different answer.</para>
    ///
    /// <para><b>WITHOUT V-D5's DEDUP THIS ALWAYS ANSWERS ZERO, which is the incumbent's behaviour rather than a
    /// bug in this function.</b> One handle per <c>ResourceLayout</c> object means no two pipelines ever share one,
    /// so nothing is ever compatible and every switch forces a full rebind of every set. The blunt clear-everything
    /// version of this clause reproduces that by construction rather than by choice, which is the argument section
    /// 2.4 settles.</para>
    ///
    /// <para><b>CONSERVATIVE IS SAFE AND OPTIMISTIC IS SILENT, so every arm here rounds down.</b> A prefix shorter
    /// than the truth costs a redundant <c>vkCmdBindDescriptorSets</c>. A prefix LONGER than the truth leaves a set
    /// the driver has already invalidated marked clean, so the next draw reads whatever the descriptor slot now
    /// holds, which renders wrong and throws nothing. That asymmetry is why V-R7 exists at all: a device-free test
    /// walks every ordered pair of shipped pipelines and asserts the computed prefix never exceeds the true
    /// identical-handle prefix, and the validation build asserts at the draw that every bound set's layout really
    /// is the current pipeline layout's set layout at that index.</para>
    /// </summary>
    internal static class VulkanLayoutCompatibility
    {
        /// <summary>
        /// How many leading sets survive a switch from <paramref name="outgoing"/> to <paramref name="incoming"/>:
        /// the length of their common prefix of identical <c>VkDescriptorSetLayout</c> handles. Also the index of
        /// the FIRST set the switch invalidates, which is how <see cref="VulkanBindRecords"/> uses it.
        /// <para>
        /// AN EMPTY OUTGOING SEQUENCE ANSWERS ZERO WITH NO SPECIAL CASE, which is the right answer for a list that
        /// has bound no pipeline yet: nothing is bound, so nothing is compatible, and every recorded slot owes the
        /// first draw a bind. That falls out of the loop rather than being written as an arm, because an arm is
        /// something a later edit can get wrong.
        /// </para>
        /// <para>
        /// A NULL HANDLE NEVER ESTABLISHES COMPATIBILITY even against another null handle. Two zeroes are not two
        /// identically defined set layouts, they are two absences, and the whole failure mode this function has is
        /// claiming a compatibility that is not there. A shipped pipeline layout never carries one (row 10 refuses
        /// a null layout at creation), so this costs one comparison and can only ever shorten the answer.
        /// </para>
        /// </summary>
        /// <param name="outgoing">The set-layout handles of the pipeline layout currently bound, in slot order.
        /// Empty when none is.</param>
        /// <param name="incoming">The set-layout handles of the pipeline layout being bound, in slot order.</param>
        internal static int CompatiblePrefix(ReadOnlySpan<ulong> outgoing, ReadOnlySpan<ulong> incoming)
        {
            int shared = Math.Min(outgoing.Length, incoming.Length);

            int prefix = 0;
            while (prefix < shared && outgoing[prefix] != 0 && outgoing[prefix] == incoming[prefix]) prefix++;

            return prefix;
        }
    }
}
