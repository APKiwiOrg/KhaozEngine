using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE DESCRIPTOR SEAM WITH NO DRIVER BEHIND IT, recording every call as an event and keeping the
    /// create-info of every object, so a test can assert what a layout, a pool and a set actually asked the driver
    /// for, in order.
    /// <para>
    /// This is what makes the whole of row 10's POLICY device-free: which Vulkan type an element becomes, which
    /// layouts SHARE a handle, how a pool is sized, which per-type budget an allocation spends and which one a
    /// free restores, what range a descriptor is written with, and that a set is written exactly ONCE for its
    /// life.
    /// </para>
    /// <para>
    /// IT IS ALSO THE "FAKE POOL" OF DECISION V-D2's zero-count assertion. <see cref="AllocateCount"/> and
    /// <see cref="UpdateCount"/> are the two numbers that must not move while a command list is recording, and
    /// this type is where they are read from.
    /// </para>
    /// <para>
    /// WHAT NO FAKE HERE CAN PROVE is that a driver accepted the structures, that a descriptor set is compatible
    /// with the layout it was allocated against, or that a range satisfies
    /// <c>VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979</c> at run time. That belongs to the
    /// <c>vulkan-native</c> CI leg with its validation layers (row 19,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/529). The boundary is deliberate.
    /// </para>
    /// </summary>
    internal sealed class FakeVulkanDescriptorApi : IVulkanDescriptorApi
    {
        readonly List<string> _log = new();
        readonly HashSet<ulong> _live = new();
        readonly Dictionary<ulong, VulkanDescriptorBinding[]> _setLayouts = new();
        readonly Dictionary<ulong, ulong[]> _pipelineLayouts = new();
        readonly Dictionary<ulong, VulkanDescriptorPoolSize> _poolSizes = new();
        readonly Dictionary<ulong, ulong> _setPools = new();
        readonly List<(ulong Set, VulkanDescriptorWrite[] Writes)> _updates = new();

        ulong _nextSetLayout = 0x5_0000;
        ulong _nextPipelineLayout = 0x6_0000;
        ulong _nextPool = 0x7_0000;
        ulong _nextSet = 0x8_0000;

        /// <summary>Every call in order, as text.</summary>
        internal IReadOnlyList<string> Events => _log;

        /// <summary>Handles created and not yet destroyed.</summary>
        internal IReadOnlyCollection<ulong> Live => _live;

        /// <summary>The binding table each set layout was created with, by handle.</summary>
        internal IReadOnlyDictionary<ulong, VulkanDescriptorBinding[]> SetLayouts => _setLayouts;

        /// <summary>The set-layout array each pipeline layout was created with, in slot order.</summary>
        internal IReadOnlyDictionary<ulong, ulong[]> PipelineLayouts => _pipelineLayouts;

        /// <summary>The size each pool was created with, which is decision V-D3's whole observable surface.</summary>
        internal IReadOnlyDictionary<ulong, VulkanDescriptorPoolSize> PoolSizes => _poolSizes;

        /// <summary>Every <c>vkUpdateDescriptorSets</c> in order, with the writes it carried.</summary>
        internal IReadOnlyList<(ulong Set, VulkanDescriptorWrite[] Writes)> Updates => _updates;

        /// <summary>How many <c>vkCreateDescriptorSetLayout</c> calls were made. With the number of
        /// <c>IGpuResourceLayout</c>s created, this is decision V-D5's dedup, observable.</summary>
        internal int SetLayoutCreateCount { get; private set; }

        /// <summary>How many <c>vkCreatePipelineLayout</c> calls were made.</summary>
        internal int PipelineLayoutCreateCount { get; private set; }

        /// <summary>How many <c>vkCreateDescriptorPool</c> calls were made. The churn test turns on this not
        /// growing.</summary>
        internal int PoolCreateCount { get; private set; }

        /// <summary>How many <c>vkAllocateDescriptorSets</c> calls were made. One half of decision V-D2's
        /// zero-during-recording assertion.</summary>
        internal int AllocateCount { get; private set; }

        /// <summary>How many <c>vkFreeDescriptorSets</c> calls were made.</summary>
        internal int FreeCount { get; private set; }

        /// <summary>How many <c>vkUpdateDescriptorSets</c> calls were made. The other half of V-D2's
        /// assertion, and the number that proves a set is written exactly once.</summary>
        internal int UpdateCount { get; private set; }

        /// <summary>The call to fail on, once, so a test can drive the half-built failure paths.</summary>
        internal string? FailOn { get; set; }

        /// <summary>The pool a set was allocated from, so a test can prove a free went back to the right one.
        /// </summary>
        internal ulong PoolOf(ulong set) => _setPools[set];

        /// <inheritdoc/>
        public ulong CreateSetLayout(ReadOnlySpan<VulkanDescriptorBinding> bindings)
        {
            FailIfAsked("vkCreateDescriptorSetLayout");

            ulong handle = _nextSetLayout;
            _nextSetLayout += 0x10;

            _live.Add(handle);
            _setLayouts[handle] = bindings.ToArray();
            SetLayoutCreateCount++;
            _log.Add($"vkCreateDescriptorSetLayout {Hex(handle)} bindings={bindings.Length}");
            return handle;
        }

        /// <inheritdoc/>
        public void DestroySetLayout(ulong setLayout)
        {
            RequireLive(setLayout, "vkDestroyDescriptorSetLayout");
            _live.Remove(setLayout);
            _log.Add($"vkDestroyDescriptorSetLayout {Hex(setLayout)}");
        }

        /// <inheritdoc/>
        public ulong CreatePipelineLayout(ReadOnlySpan<ulong> setLayouts)
        {
            FailIfAsked("vkCreatePipelineLayout");

            ulong handle = _nextPipelineLayout;
            _nextPipelineLayout += 0x10;

            _live.Add(handle);
            _pipelineLayouts[handle] = setLayouts.ToArray();
            PipelineLayoutCreateCount++;
            _log.Add($"vkCreatePipelineLayout {Hex(handle)} sets={setLayouts.Length}");
            return handle;
        }

        /// <inheritdoc/>
        public void DestroyPipelineLayout(ulong pipelineLayout)
        {
            RequireLive(pipelineLayout, "vkDestroyPipelineLayout");
            _live.Remove(pipelineLayout);
            _log.Add($"vkDestroyPipelineLayout {Hex(pipelineLayout)}");
        }

        /// <inheritdoc/>
        public ulong CreatePool(in VulkanDescriptorPoolSize size)
        {
            FailIfAsked("vkCreateDescriptorPool");

            ulong handle = _nextPool;
            _nextPool += 0x10;

            _live.Add(handle);
            _poolSizes[handle] = size;
            PoolCreateCount++;
            _log.Add($"vkCreateDescriptorPool {Hex(handle)} maxSets={size.MaxSets} [{size.Counts.Describe()}]");
            return handle;
        }

        /// <inheritdoc/>
        public void DestroyPool(ulong pool)
        {
            RequireLive(pool, "vkDestroyDescriptorPool");
            _live.Remove(pool);

            // A destroyed pool takes every set still in it, which is what makes a consumer that never disposed a
            // resource set a leak report rather than a leak.
            foreach (ulong set in _setPools.Where(p => p.Value == pool).Select(p => p.Key).ToArray())
            {
                _live.Remove(set);
                _setPools.Remove(set);
            }

            _log.Add($"vkDestroyDescriptorPool {Hex(pool)}");
        }

        /// <inheritdoc/>
        public ulong AllocateSet(ulong pool, ulong setLayout)
        {
            FailIfAsked("vkAllocateDescriptorSets");
            RequireLive(pool, "vkAllocateDescriptorSets");
            RequireLive(setLayout, "vkAllocateDescriptorSets");

            ulong handle = _nextSet;
            _nextSet += 0x10;

            _live.Add(handle);
            _setPools[handle] = pool;
            AllocateCount++;
            _log.Add($"vkAllocateDescriptorSets {Hex(handle)} pool={Hex(pool)} layout={Hex(setLayout)}");
            return handle;
        }

        /// <inheritdoc/>
        public void FreeSet(ulong pool, ulong set)
        {
            RequireLive(set, "vkFreeDescriptorSets");

            if (_setPools.TryGetValue(set, out ulong owner) && owner != pool)
            {
                throw new InvalidOperationException(
                    $"vkFreeDescriptorSets returned {Hex(set)} to {Hex(pool)}, which is not the pool it was "
                    + $"allocated from ({Hex(owner)}). A set freed into the wrong pool corrupts both budgets.");
            }

            _live.Remove(set);
            _setPools.Remove(set);
            FreeCount++;
            _log.Add($"vkFreeDescriptorSets {Hex(set)} pool={Hex(pool)}");
        }

        /// <inheritdoc/>
        public void UpdateSet(ulong set, ReadOnlySpan<VulkanDescriptorWrite> writes)
        {
            FailIfAsked("vkUpdateDescriptorSets");
            RequireLive(set, "vkUpdateDescriptorSets");

            _updates.Add((set, writes.ToArray()));
            UpdateCount++;
            _log.Add($"vkUpdateDescriptorSets {Hex(set)} writes={writes.Length}");
        }

        /// <summary>The writes one set was given, which must have happened exactly once.</summary>
        internal VulkanDescriptorWrite[] WritesFor(ulong set)
        {
            var found = _updates.Where(u => u.Set == set).ToArray();
            if (found.Length == 1) return found[0].Writes;

            throw new InvalidOperationException(
                $"{Hex(set)} was updated {found.Length.ToString(CultureInfo.InvariantCulture)} times. Decision "
                + "V-D1 is that a descriptor set is written ONCE at creation and never again.");
        }

        void FailIfAsked(string call)
        {
            if (FailOn != call) return;

            FailOn = null;
            throw new InvalidOperationException($"The fake native Vulkan descriptor seam was told to fail {call}.");
        }

        void RequireLive(ulong handle, string call)
        {
            if (_live.Contains(handle)) return;

            throw new InvalidOperationException(
                $"{call} was called on {Hex(handle)}, which is not live. Either it was never created or it has "
                + "already been destroyed, and a double free through the retire list is exactly the defect a "
                + "deferred disposal produces without saying anything.");
        }

        static string Hex(ulong handle) => "0x" + handle.ToString("x", CultureInfo.InvariantCulture);
    }
}
