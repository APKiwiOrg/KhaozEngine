using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE DESCRIPTOR POOLS (V-D3, 8.2): a list of <c>VkDescriptorPool</c>s created with
    /// <c>FREE_DESCRIPTOR_SET</c>, sized from actual demand, walked by REMAINING PER-TYPE BUDGET on every
    /// allocation, and restored on EVERY counted type when a set is freed.
    ///
    /// <para><b>THE FREE PATH IS THE WHOLE POINT, AND THE INCUMBENT'S IS WRONG.</b>
    /// <c>VkDescriptorPoolManager.PoolInfo.Free</c> restores five of the seven types it spends
    /// (<c>v4.9.0</c>, lines 166 to 178: <c>RemainingSets</c>, <c>UniformBufferCount</c>,
    /// <c>SampledImageCount</c>, <c>SamplerCount</c>, <c>StorageBufferCount</c>, <c>StorageImageCount</c>) and
    /// silently forgets <c>UniformBufferDynamicCount</c> and <c>StorageBufferDynamicCount</c>, which its own
    /// <c>Allocate</c> does spend. An application that churns dynamic-offset resource sets therefore leaks pool
    /// budget until a fresh pool spawns, and every fresh pool leaks the same way, so the pool list grows without
    /// bound. This engine's resource sets are overwhelmingly dynamic-offset ones and the map editor churns them
    /// on every document load, so the leak is aimed squarely at this consumer. And it BINDS HARDER HERE THAN
    /// THERE, because decision V-D4 makes every uniform buffer a dynamic one rather than only the elements the
    /// engine declared dynamic.</para>
    ///
    /// <para><b>HERE IT CANNOT BE WRITTEN.</b> Take and restore are one pair of methods over one value
    /// (<see cref="VulkanDescriptorCounts.Take"/> and <see cref="VulkanDescriptorCounts.Restore"/>), so there is
    /// no second list of field names to keep in step with the first, and
    /// <c>VulkanDescriptorPoolTests</c> churns sets in a loop and asserts the pool count does not
    /// grow.</para>
    ///
    /// <para><b>THE ALLOCATION WALK ASKS ABOUT ALL SEVEN TYPES TOO.</b> The incumbent's walk compared
    /// <c>StorageBufferCount &gt;= counts.SamplerCount</c>, a transposed term that admits a set with more storage
    /// buffers than samplers into a pool that cannot hold it, and then fails inside
    /// <c>vkAllocateDescriptorSets</c> where the message says nothing about budgets.
    /// <see cref="VulkanDescriptorCounts.Fits"/> is one expression over seven fields, which is the same defence
    /// as above applied to the other direction. Fixed upstream on Veldrid's default branch (commit
    /// 35c6f23, 2023-09-16), but that commit is not an ancestor of the vendored v4.9.103 line, so a future fork
    /// rebase may pick it up on its own and the guard test above stays valid either way.</para>
    ///
    /// <para><b>ONE SHORT LOCK, because creation is free-threaded (V-W8).</b> It covers the pool walk, the
    /// budget arithmetic, the append and the native allocate, because a budget checked outside the lock and spent
    /// inside it is a budget two threads can both pass.</para>
    ///
    /// <para><b>AND IT IS UNREACHABLE FROM THE RECORDING TYPE (V-D2).</b> That is not a comment, it is the
    /// enforcement: <c>vkAllocateDescriptorSets</c> is not a bind, a draw or a barrier, so no counting seam can
    /// see it, and the guarantee is instead that a recorder's field graph cannot reach this type at all.
    /// <c>VulkanRecordingUnreachabilityTests</c> asserts it. See <see cref="VulkanDescriptorOwner"/> for why this
    /// does not hang off <see cref="VulkanResourceOwner"/>.</para>
    /// </summary>
    internal sealed class VulkanDescriptorPoolManager
    {
        readonly VulkanDescriptorOwner _owner;
        readonly object _gate = new();
        readonly List<PoolInfo> _pools = new();

        VulkanDescriptorCounts _largestSingleRequest;
        int _outstandingSets;
        int _peakOutstandingSets;
        long _allocations;
        long _frees;

        /// <param name="owner">The device's descriptor seam, timeline and retire list.</param>
        internal VulkanDescriptorPoolManager(VulkanDescriptorOwner owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            _owner = owner;
        }

        /// <summary>How many <c>VkDescriptorPool</c>s exist. THE number the churn test turns on: a free path that
        /// forgets a type makes this grow forever under a workload that allocates and frees the same sets.
        /// <para>
        /// It starts at ZERO rather than at one. The incumbent created a pool in its constructor, so a device that
        /// never builds a resource set still holds one, and here the first pool is created by the first
        /// allocation that needs it.
        /// </para></summary>
        internal int PoolCount
        {
            get { lock (_gate) return _pools.Count; }
        }

        /// <summary>How many sets are allocated and not yet freed.</summary>
        internal int OutstandingSets
        {
            get { lock (_gate) return _outstandingSets; }
        }

        /// <summary>The most that have ever been live at once, which is what the next pool is sized for.</summary>
        internal int PeakOutstandingSets
        {
            get { lock (_gate) return _peakOutstandingSets; }
        }

        /// <summary>How many sets have been allocated over this manager's life.</summary>
        internal long AllocationCount
        {
            get { lock (_gate) return _allocations; }
        }

        /// <summary>How many have been freed. With <see cref="AllocationCount"/> this is the leak check a
        /// diagnostic reports.</summary>
        internal long FreeCount
        {
            get { lock (_gate) return _frees; }
        }

        /// <summary>
        /// ALLOCATE ONE <c>VkDescriptorSet</c> (V-D1), out of the first pool whose remaining per-type budget can
        /// cover <paramref name="counts"/>, appending a pool sized for the request when none can.
        /// </summary>
        /// <param name="setLayout">The shared <c>VkDescriptorSetLayout</c> the set is allocated against.</param>
        /// <param name="counts">What the layout's bindings cost. The SAME value must be handed back to
        /// <see cref="Free"/>, which is why a set carries it for its life rather than recomputing it.</param>
        internal VulkanDescriptorSetToken Allocate(ulong setLayout, in VulkanDescriptorCounts counts)
        {
            lock (_gate)
            {
                _largestSingleRequest = _largestSingleRequest.Max(counts);

                PoolInfo pool = FindOrAppend(counts);
                ulong set = _owner.Api.AllocateSet(pool.Pool, setLayout);

                // AFTER the native call, so a driver refusal leaves the budget exactly where it was rather than
                // spending it on a set that does not exist.
                pool.Take(counts);

                _allocations++;
                _outstandingSets++;
                if (_outstandingSets > _peakOutstandingSets) _peakOutstandingSets = _outstandingSets;

                return new VulkanDescriptorSetToken(set, pool.Pool);
            }
        }

        /// <summary>
        /// FREE ONE SET, BEHIND THE TIMELINE. The budget is restored and <c>vkFreeDescriptorSets</c> is called at
        /// the deferred release rather than here, because a descriptor set freed while a submission that binds it
        /// is still executing is undefined behaviour, and because the budget is only genuinely free once the
        /// descriptors are.
        /// </summary>
        /// <param name="token">The set and the pool it came from.</param>
        /// <param name="counts">The same value the allocation spent, restored on ALL SEVEN types.</param>
        internal void Free(in VulkanDescriptorSetToken token, in VulkanDescriptorCounts counts)
        {
            VulkanDescriptorSetToken released = token;
            VulkanDescriptorCounts restored = counts;

            _owner.RetireTerminal(() => FreeNow(released, restored));
        }

        /// <summary>
        /// DESTROY EVERY POOL. Called ONCE, from the device's teardown window, after the wait that made the GPU
        /// idle and the retire drain that ran every deferred free. <c>vkDestroyDescriptorPool</c> implicitly frees
        /// every set still in it, so a set whose owner never disposed goes with its pool rather than leaking.
        /// </summary>
        /// <returns>How many pools were destroyed.</returns>
        internal int DestroyAll()
        {
            lock (_gate)
            {
                int destroyed = _pools.Count;
                for (int i = 0; i < _pools.Count; i++) _owner.Api.DestroyPool(_pools[i].Pool);

                _pools.Clear();
                _outstandingSets = 0;
                return destroyed;
            }
        }

        /// <summary>The line a teardown diagnostic quotes.</summary>
        internal string Describe()
        {
            lock (_gate)
            {
                return _pools.Count.ToString(CultureInfo.InvariantCulture)
                    + " native Vulkan descriptor pools, "
                    + _allocations.ToString(CultureInfo.InvariantCulture) + " sets allocated and "
                    + _frees.ToString(CultureInfo.InvariantCulture) + " freed, peak "
                    + _peakOutstandingSets.ToString(CultureInfo.InvariantCulture) + " live at once";
            }
        }

        // The pool walk of 8.2: the first pool with room on EVERY counted type, or a new one sized for the
        // request that could not be placed. Called under the lock.
        PoolInfo FindOrAppend(in VulkanDescriptorCounts counts)
        {
            for (int i = 0; i < _pools.Count; i++)
            {
                if (_pools[i].CanFit(counts)) return _pools[i];
            }

            VulkanDescriptorPoolSize size = VulkanDescriptorPoolBudget.Next(
                counts, _largestSingleRequest, _peakOutstandingSets);

            var appended = new PoolInfo(_owner.Api.CreatePool(size), size);
            _pools.Add(appended);

            if (appended.CanFit(counts)) return appended;

            throw new InvalidOperationException(
                "A native Vulkan descriptor pool sized for " + size.Counts.Describe() + " across "
                + size.MaxSets.ToString(CultureInfo.InvariantCulture)
                + " sets still cannot hold a set needing " + counts.Describe()
                + ". VulkanDescriptorPoolBudget.Next is meant to guarantee the failing request fits in the pool "
                + "appended for it, so this is a defect in that arithmetic rather than a workload the backend "
                + "cannot serve.");
        }

        // The deferred half of Free. Runs from the retire drain, once the timeline has passed the value the
        // disposal recorded.
        void FreeNow(VulkanDescriptorSetToken token, VulkanDescriptorCounts counts)
        {
            lock (_gate)
            {
                for (int i = 0; i < _pools.Count; i++)
                {
                    if (_pools[i].Pool != token.Pool) continue;

                    // EVERY COUNTED TYPE, plus the set slot. This one call is the whole of the divergence from
                    // the incumbent's five-of-seven restore.
                    _pools[i].Restore(counts);

                    _owner.Api.FreeSet(token.Pool, token.Set);

                    _frees++;
                    if (_outstandingSets > 0) _outstandingSets--;
                    return;
                }

                // The pool has already been destroyed, which is teardown ordering rather than an error: every set
                // in a destroyed pool was freed with it. Counted so the numbers still reconcile.
                _frees++;
                if (_outstandingSets > 0) _outstandingSets--;
            }
        }

        // ONE VkDescriptorPool and what is left in it. A class rather than a struct because the list holds it and
        // mutates it in place, and a struct in a List<T> would be mutated through a copy.
        sealed class PoolInfo
        {
            readonly uint _capacitySets;
            readonly VulkanDescriptorCounts _capacity;

            internal PoolInfo(ulong pool, in VulkanDescriptorPoolSize size)
            {
                Pool = pool;
                _capacitySets = size.MaxSets;
                _capacity = size.Counts;

                RemainingSets = size.MaxSets;
                Remaining = size.Counts;
            }

            internal ulong Pool { get; }

            internal uint RemainingSets { get; private set; }

            internal VulkanDescriptorCounts Remaining { get; private set; }

            internal bool CanFit(in VulkanDescriptorCounts counts)
                => RemainingSets > 0 && Remaining.Fits(counts);

            internal void Take(in VulkanDescriptorCounts counts)
            {
                RemainingSets -= 1;
                Remaining = Remaining.Take(counts);
            }

            // CLAMPED TO THE CAPACITY, so a double free cannot inflate a pool's budget past what the driver
            // really created. A restore that overshot would let the pool admit sets vkAllocateDescriptorSets then
            // refuses, which is the failure shape with the least useful message in the whole subsystem.
            internal void Restore(in VulkanDescriptorCounts counts)
            {
                RemainingSets = Math.Min(_capacitySets, RemainingSets + 1);

                VulkanDescriptorCounts restored = Remaining.Restore(counts);
                Remaining = new VulkanDescriptorCounts(
                    Math.Min(_capacity.UniformBuffer, restored.UniformBuffer),
                    Math.Min(_capacity.UniformBufferDynamic, restored.UniformBufferDynamic),
                    Math.Min(_capacity.StorageBuffer, restored.StorageBuffer),
                    Math.Min(_capacity.StorageBufferDynamic, restored.StorageBufferDynamic),
                    Math.Min(_capacity.SampledImage, restored.SampledImage),
                    Math.Min(_capacity.StorageImage, restored.StorageImage),
                    Math.Min(_capacity.Sampler, restored.Sampler));
            }
        }
    }
}
