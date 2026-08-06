using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// HOW BIG THE NEXT <c>VkDescriptorPool</c> IS, computed from ACTUAL DEMAND (V-D3, 8.2). Pure arithmetic over
    /// plain integers, so every sizing decision runs under a <c>[Fact]</c> with no loader.
    ///
    /// <para><b>THE INCUMBENT'S FIXED POOL IS THE SHAPE THIS REPLACES.</b> It creates every pool with
    /// <c>maxSets = 1000</c> and 100 descriptors of each of seven types, so its PER-TYPE ceiling is reached long
    /// before its SET ceiling: 101 sets each holding one sampled image exhaust the sampled images while 899 set
    /// slots sit unused, and a new 1000-set pool spawns behind them. A pool shaped like nothing in particular is
    /// wrong in both directions at once, wasteful for a small program and prematurely exhausted for a real
    /// one.</para>
    ///
    /// <para><b>THE RULE HERE, IN ONE SENTENCE.</b> A new pool holds as many SETS as the most that have ever been
    /// live at once (floored and capped), and for each type as many DESCRIPTORS as that many sets of the
    /// heaviest single shape seen so far, and never fewer than the request that just failed. So a pool is shaped
    /// like the workload that produced it, and the guarantee "the failing request fits in the pool appended for
    /// it" holds by construction rather than by the size happening to be generous.</para>
    ///
    /// <para><b>THE FLOOR IS WHAT KEEPS THE POOL COUNT FLAT.</b> Sizing the first pool at exactly one set's worth
    /// would make the second allocation miss, the third miss again, and the pool list grow one entry per set,
    /// which is worse than the fixed pool it replaces. <see cref="MinimumSetsPerPool"/> is what makes the common
    /// case one pool, and the cap is what stops a program that once had a spike from allocating a pool sized for
    /// that spike forever.</para>
    /// </summary>
    internal static class VulkanDescriptorPoolBudget
    {
        /// <summary>The fewest sets any pool is created for. Small enough that a program creating three resource
        /// sets does not allocate for a thousand, large enough that the pool list does not grow per set.</summary>
        internal const int MinimumSetsPerPool = 8;

        /// <summary>The most sets any single pool is created for. A program whose live set count spikes once
        /// should not allocate a pool sized for the spike on every later miss.</summary>
        internal const int MaximumSetsPerPool = 1024;

        /// <summary>
        /// The size of the pool to append when no existing pool can satisfy <paramref name="request"/>.
        /// </summary>
        /// <param name="request">The allocation that just failed to fit anywhere. The returned budget is never
        /// below it on any type, which is what makes appending a pool a guaranteed fix rather than a retry.</param>
        /// <param name="largestSingleRequest">The heaviest single-set demand seen so far, per type, INCLUDING
        /// <paramref name="request"/> if the caller has already folded it in. Folding it in here as well is
        /// harmless and is done so the guarantee does not depend on the caller's order.</param>
        /// <param name="peakOutstandingSets">The most sets that have ever been live at once.</param>
        internal static VulkanDescriptorPoolSize Next(in VulkanDescriptorCounts request,
            in VulkanDescriptorCounts largestSingleRequest, int peakOutstandingSets)
        {
            if (peakOutstandingSets < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(peakOutstandingSets), peakOutstandingSets,
                    "A native Vulkan descriptor pool cannot have had a negative number of sets live at once.");
            }

            uint sets = (uint)Math.Clamp(peakOutstandingSets, MinimumSetsPerPool, MaximumSetsPerPool);

            VulkanDescriptorCounts perSet = largestSingleRequest.Max(request);

            // The Max at the end is not redundant even though sets is at least 1: Scaled saturates, and a
            // saturated product must still not land below the request the pool is being appended FOR.
            VulkanDescriptorCounts counts = perSet.Scaled(sets).Max(request);

            return new VulkanDescriptorPoolSize(sets, counts);
        }
    }
}
