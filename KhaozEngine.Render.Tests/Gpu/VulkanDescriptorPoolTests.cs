using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION V-D3, device-free: pools sized from ACTUAL DEMAND, walked by remaining per-type budget, and
    /// restored on EVERY counted type when a set is freed. Work-breakdown row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/520).
    ///
    /// <para><b>THE FREE PATH IS THE REGRESSION THIS FILE EXISTS FOR.</b> The incumbent's
    /// <c>VkDescriptorPoolManager.PoolInfo.Free</c> restores five of the seven types it spends (<c>v4.9.0</c>,
    /// lines 166 to 178) and silently forgets <c>UniformBufferDynamicCount</c> and
    /// <c>StorageBufferDynamicCount</c>. An application that churns dynamic-offset resource sets therefore leaks
    /// pool budget until a fresh pool spawns, and every fresh pool leaks the same way. This engine's resource sets
    /// are overwhelmingly dynamic-offset ones, the map editor churns them on every document load, and decision
    /// V-D4 makes far MORE descriptors dynamic here than there, so the leak is aimed squarely at this
    /// consumer.</para>
    /// </summary>
    public sealed class VulkanDescriptorPoolTests
    {
        // A shape that uses all seven counted types at once, including the two the incumbent's free forgets and
        // the two this backend's own layout policy can never produce. The pool's accounting is asserted complete
        // rather than complete-for-what-we-happen-to-emit, which is the whole of V-D3.
        static readonly VulkanDescriptorCounts AllSeven = new(
            UniformBuffer: 2, UniformBufferDynamic: 3, StorageBuffer: 5, StorageBufferDynamic: 7,
            SampledImage: 11, StorageImage: 13, Sampler: 17);

        /// <summary>
        /// TAKE THEN RESTORE IS THE IDENTITY, ON ALL SEVEN TYPES. The value has structural equality, so this is
        /// one assertion rather than seven, and it is exactly the assertion the incumbent's five-of-seven restore
        /// fails.
        /// </summary>
        [Fact]
        public void TakingAndRestoring_IsTheIdentityOnEveryCountedType()
        {
            VulkanDescriptorCounts budget = AllSeven.Scaled(4);

            VulkanDescriptorCounts spent = budget.Take(AllSeven);
            Assert.NotEqual(budget, spent);

            Assert.Equal(budget, spent.Restore(AllSeven));

            // Named individually as well, because the two that matter are the two a five-of-seven restore drops
            // and an equality failure would not say which.
            Assert.Equal(budget.UniformBufferDynamic, spent.Restore(AllSeven).UniformBufferDynamic);
            Assert.Equal(budget.StorageBufferDynamic, spent.Restore(AllSeven).StorageBufferDynamic);
        }

        /// <summary>
        /// THE ALLOCATION WALK ASKS ABOUT ALL SEVEN TYPES TOO, which is the other half of the same discipline.
        /// The incumbent's <c>Allocate</c> compares <c>StorageBufferCount &gt;= counts.SamplerCount</c>, a
        /// transposed term that admits a set with more storage buffers than samplers into a pool that cannot hold
        /// it, and then fails inside <c>vkAllocateDescriptorSets</c> where the message says nothing about
        /// budgets.
        /// </summary>
        [Fact]
        public void TheFitCheck_AsksAboutStorageBuffersRatherThanSamplers()
        {
            var budget = new VulkanDescriptorCounts(0, 0, StorageBuffer: 1, 0, 0, 0, Sampler: 8);
            var request = new VulkanDescriptorCounts(0, 0, StorageBuffer: 4, 0, 0, 0, Sampler: 1);

            // The incumbent's transposed comparison would answer true here, because it measures the request's
            // SAMPLER count against the pool's storage buffer budget.
            Assert.False(budget.Fits(request));
            Assert.True(budget.Fits(request with { StorageBuffer = 1 }));
        }

        /// <summary>
        /// A POOL IS SIZED FROM DEMAND rather than being the incumbent's fixed <c>maxSets = 1000</c> with 100
        /// descriptors of each of seven types, whose per-type ceiling is reached long before its set ceiling. And
        /// the budget always covers the request the pool was appended FOR, which is what makes appending a pool a
        /// guaranteed fix rather than a retry.
        /// </summary>
        [Fact]
        public void APoolIsSizedFromDemand_AndAlwaysCoversTheRequestItWasAppendedFor()
        {
            VulkanDescriptorPoolSize first = VulkanDescriptorPoolBudget.Next(
                AllSeven, default, peakOutstandingSets: 0);

            Assert.Equal((uint)VulkanDescriptorPoolBudget.MinimumSetsPerPool, first.MaxSets);
            Assert.True(first.Counts.Fits(AllSeven));
            Assert.NotEqual(1000u, first.MaxSets);

            // A pool for a heavier workload is bigger, and one for a workload that once spiked is capped.
            VulkanDescriptorPoolSize later = VulkanDescriptorPoolBudget.Next(AllSeven, AllSeven, 64);
            Assert.Equal(64u, later.MaxSets);
            Assert.True(later.Counts.Fits(AllSeven.Scaled(64)));

            VulkanDescriptorPoolSize capped = VulkanDescriptorPoolBudget.Next(AllSeven, AllSeven, 1_000_000);
            Assert.Equal((uint)VulkanDescriptorPoolBudget.MaximumSetsPerPool, capped.MaxSets);
        }

        /// <summary>
        /// THE POOL LIST STARTS EMPTY, unlike the incumbent's, which creates one in its constructor: a device
        /// that never builds a resource set holds a 1000-set pool there and nothing here.
        /// </summary>
        [Fact]
        public void ThePoolList_StartsEmpty()
        {
            var fixture = new VulkanResourceFixture();

            Assert.Equal(0, fixture.Descriptors.Pools.PoolCount);
            Assert.Empty(fixture.DescriptorApi.Events);
        }

        /// <summary>
        /// THE CHURN TEST OF DECISION V-D3: allocate and free in a loop and the pool count does not grow. With
        /// the incumbent's free path this fails, because each round spends dynamic uniform budget that never comes
        /// back and a fresh pool spawns once the first is exhausted.
        /// <para>
        /// The shape churned is the one the engine really builds: a resource set over a ring-backed uniform
        /// buffer, which under V-D4 is a <c>UNIFORM_BUFFER_DYNAMIC</c> descriptor and therefore spends exactly the
        /// counter the incumbent forgets.
        /// </para>
        /// </summary>
        [Fact]
        public void ChurningDynamicOffsetResourceSets_DoesNotGrowThePoolList()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                VulkanResourceFixture.UniformLayout(dynamic: true));
            using IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(256, GpuBufferUsage.UniformBuffer));

            const int SetsPerRound = 24;
            const int Rounds = 12;

            int poolsAfterFirstRound = 0;

            for (int round = 0; round < Rounds; round++)
            {
                var sets = new List<IGpuResourceSet>(SetsPerRound);
                for (int i = 0; i < SetsPerRound; i++)
                {
                    sets.Add(fixture.Factory.CreateResourceSet(
                        new GpuResourceSetDescription(layout, new GpuBufferRange(uniform, 0, 64))));
                }

                foreach (IGpuResourceSet set in sets) set.Dispose();

                // The free is DEFERRED behind the timeline, exactly as a buffer's destroy is, so the budget comes
                // back at the drain a frame boundary would run rather than at Dispose.
                fixture.Drain();

                if (round == 0) poolsAfterFirstRound = fixture.Descriptors.Pools.PoolCount;
            }

            Assert.Equal(poolsAfterFirstRound, fixture.Descriptors.Pools.PoolCount);
            Assert.Equal(poolsAfterFirstRound, fixture.DescriptorApi.PoolCreateCount);
            Assert.Equal(0, fixture.Descriptors.Pools.OutstandingSets);
            Assert.Equal(SetsPerRound * Rounds, fixture.DescriptorApi.AllocateCount);
            Assert.Equal(SetsPerRound * Rounds, fixture.DescriptorApi.FreeCount);
        }

        /// <summary>
        /// AND THE SAME CHURN AT THE POOL LEVEL, over a shape that uses ALL SEVEN counted types including the two
        /// this backend's own layout policy can never produce. A free path that restored only the types the
        /// engine emits would pass the test above and fail this one, which is why both exist.
        /// </summary>
        [Fact]
        public void ChurningEverySevenTypes_DoesNotGrowThePoolList()
        {
            var fixture = new VulkanResourceFixture();
            VulkanDescriptorPoolManager pools = fixture.Descriptors.Pools;

            ulong setLayout = fixture.DescriptorApi.CreateSetLayout([]);

            int poolsAfterFirstRound = 0;

            for (int round = 0; round < 16; round++)
            {
                var tokens = new List<VulkanDescriptorSetToken>(8);
                for (int i = 0; i < 8; i++) tokens.Add(pools.Allocate(setLayout, AllSeven));

                foreach (VulkanDescriptorSetToken token in tokens) pools.Free(token, AllSeven);
                fixture.Drain();

                if (round == 0) poolsAfterFirstRound = pools.PoolCount;
            }

            Assert.Equal(poolsAfterFirstRound, pools.PoolCount);
            Assert.Equal(0, pools.OutstandingSets);
            Assert.Equal(128, pools.AllocationCount);
            Assert.Equal(128, pools.FreeCount);
        }

        /// <summary>
        /// AN ALLOCATION THAT FITS NO POOL APPENDS ONE SIZED FOR IT (8.2), rather than failing or spawning a pool
        /// shaped like something else. The second shape here needs a type the first pool holds none of.
        /// </summary>
        [Fact]
        public void AnAllocationThatFitsNoPool_AppendsOneSizedForIt()
        {
            var fixture = new VulkanResourceFixture();
            VulkanDescriptorPoolManager pools = fixture.Descriptors.Pools;

            ulong setLayout = fixture.DescriptorApi.CreateSetLayout([]);

            var uniforms = new VulkanDescriptorCounts(0, UniformBufferDynamic: 1, 0, 0, 0, 0, 0);
            var images = new VulkanDescriptorCounts(0, 0, 0, 0, SampledImage: 4, StorageImage: 0, Sampler: 4);

            pools.Allocate(setLayout, uniforms);
            Assert.Equal(1, pools.PoolCount);

            pools.Allocate(setLayout, images);
            Assert.Equal(2, pools.PoolCount);

            // And the appended pool really is sized for what could not be placed.
            VulkanDescriptorPoolSize appended =
                fixture.DescriptorApi.PoolSizes[fixture.DescriptorApi.PoolSizes.Keys.Max()];
            Assert.True(appended.Counts.Fits(images));
        }

        /// <summary>
        /// A FREED SET GOES BACK TO ITS OWN POOL, which the fake asserts from the other side. A set freed into a
        /// pool it did not come from corrupts both budgets and is undefined behaviour in the driver.
        /// </summary>
        [Fact]
        public void AFreedSet_GoesBackToItsOwnPool()
        {
            var fixture = new VulkanResourceFixture();
            VulkanDescriptorPoolManager pools = fixture.Descriptors.Pools;

            ulong setLayout = fixture.DescriptorApi.CreateSetLayout([]);
            var heavy = new VulkanDescriptorCounts(0, 0, 0, 0, SampledImage: 6, 0, Sampler: 6);

            VulkanDescriptorSetToken first = pools.Allocate(setLayout, heavy);
            VulkanDescriptorSetToken second = pools.Allocate(
                setLayout, new VulkanDescriptorCounts(0, 0, StorageBuffer: 9, 0, 0, 0, 0));

            Assert.NotEqual(first.Pool, second.Pool);
            Assert.Equal(first.Pool, fixture.DescriptorApi.PoolOf(first.Set));

            pools.Free(first, heavy);
            fixture.Drain();

            Assert.Contains(fixture.DescriptorApi.Events,
                e => e.StartsWith("vkFreeDescriptorSets", StringComparison.Ordinal));
        }

        /// <summary>
        /// THE FREE IS DEFERRED BEHIND THE TIMELINE (V-F9), because a descriptor set freed while a submission
        /// that binds it is still executing is undefined behaviour of the quiet kind: the driver reads a recycled
        /// slot and draws something. Nothing native happens at <c>Dispose</c> and the budget is still spent until
        /// the drain.
        /// </summary>
        [Fact]
        public void TheFreeIsDeferred_AndSoIsTheBudgetRestore()
        {
            var fixture = new VulkanResourceFixture();
            VulkanDescriptorPoolManager pools = fixture.Descriptors.Pools;

            ulong setLayout = fixture.DescriptorApi.CreateSetLayout([]);
            VulkanDescriptorSetToken token = pools.Allocate(setLayout, AllSeven);

            Assert.Equal(1, pools.OutstandingSets);

            pools.Free(token, AllSeven);

            Assert.Equal(0, fixture.DescriptorApi.FreeCount);
            Assert.Equal(1, pools.OutstandingSets);

            Assert.Equal(1, fixture.Drain());
            Assert.Equal(1, fixture.DescriptorApi.FreeCount);
            Assert.Equal(0, pools.OutstandingSets);
        }

        /// <summary>
        /// TEARDOWN DESTROYS EVERY POOL, and a pool destroyed with sets still in it takes them, which is what
        /// makes a consumer that never disposed a resource set a report rather than a leak.
        /// </summary>
        [Fact]
        public void TeardownDestroysEveryPool_AndTakesTheSetsStillInThem()
        {
            var fixture = new VulkanResourceFixture();
            VulkanDescriptorPoolManager pools = fixture.Descriptors.Pools;

            ulong setLayout = fixture.DescriptorApi.CreateSetLayout([]);
            pools.Allocate(setLayout, AllSeven);
            pools.Allocate(setLayout, new VulkanDescriptorCounts(0, 0, StorageBuffer: 999, 0, 0, 0, 0));

            Assert.Equal(2, pools.PoolCount);

            (int pipelineLayouts, int setLayouts, int destroyedPools) = fixture.Descriptors.DestroyAll();

            Assert.Equal(0, pipelineLayouts);
            Assert.Equal(0, setLayouts);
            Assert.Equal(2, destroyedPools);
            Assert.Equal(0, pools.PoolCount);

            // The set layout above was created straight off the fake rather than through the cache, so it is what
            // is left live. Every pool and every set in one has gone.
            Assert.Equal(new ulong[] { setLayout }, fixture.DescriptorApi.Live);
        }

        /// <summary>
        /// A DOUBLE RESTORE CANNOT INFLATE A POOL PAST WHAT THE DRIVER REALLY CREATED. A budget that overshot
        /// would let the pool admit sets <c>vkAllocateDescriptorSets</c> then refuses, which is the failure shape
        /// with the least useful message in the whole subsystem.
        /// </summary>
        [Fact]
        public void ARestoreBeyondTheCapacity_IsClamped()
        {
            var fixture = new VulkanResourceFixture();
            VulkanDescriptorPoolManager pools = fixture.Descriptors.Pools;

            ulong setLayout = fixture.DescriptorApi.CreateSetLayout([]);
            VulkanDescriptorSetToken token = pools.Allocate(setLayout, AllSeven);

            pools.Free(token, AllSeven);
            fixture.Drain();

            // Fill the pool to its REAL capacity, which the restore above must not have inflated. An unclamped
            // restore would leave the tracker one set and one set's descriptors ahead of what the driver created,
            // so the pool would swallow the extra allocation below instead of appending a second pool, and the
            // driver would refuse an allocation the budget said was fine.
            VulkanDescriptorPoolSize size = fixture.DescriptorApi.PoolSizes[token.Pool];
            for (uint i = 0; i < size.MaxSets; i++) pools.Allocate(setLayout, AllSeven);

            Assert.Equal(1, pools.PoolCount);

            pools.Allocate(setLayout, AllSeven);

            Assert.Equal(2, pools.PoolCount);
        }

        /// <summary>The counts value describes itself with only the types it carries, which is what keeps a
        /// message about one uniform buffer from being six zeroes long.</summary>
        [Fact]
        public void TheCountsDescribeThemselves_WithoutTheZeroes()
        {
            Assert.Equal("no descriptors", default(VulkanDescriptorCounts).Describe());
            Assert.Equal("1 UniformBufferDynamic",
                new VulkanDescriptorCounts(0, 1, 0, 0, 0, 0, 0).Describe());
            Assert.Equal("2 SampledImage, 2 Sampler",
                new VulkanDescriptorCounts(0, 0, 0, 0, 2, 0, 2).Describe());
        }
    }
}
