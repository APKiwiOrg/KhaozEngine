using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE CONTENT KEY FOR A <c>VkPipelineLayout</c>: the ordered <c>VkDescriptorSetLayout</c> handles it is
    /// built from, and nothing else, because there is nothing else in it. Push constant ranges are declined
    /// outright (V-D8), so a pipeline layout on this backend is EXACTLY its set layout array.
    /// <para>
    /// KEYING ON HANDLES IS KEYING ON CONTENT, because <see cref="VulkanDescriptorSetLayoutCache"/> already hands
    /// out one handle per distinct set-layout content. Two pipelines built from separately created but
    /// identically shaped layouts therefore arrive here with the same handles and share one pipeline layout,
    /// which is the whole of V-D5's second half.
    /// </para>
    /// </summary>
    internal readonly struct VulkanPipelineLayoutKey : IEquatable<VulkanPipelineLayoutKey>
    {
        readonly ulong[] _setLayouts;
        readonly int _hash;

        /// <param name="setLayouts">The set-layout handles in SLOT order, taken by reference and never
        /// mutated.</param>
        internal VulkanPipelineLayoutKey(ulong[] setLayouts)
        {
            ArgumentNullException.ThrowIfNull(setLayouts);

            _setLayouts = setLayouts;

            var hash = new HashCode();
            hash.Add(setLayouts.Length);
            for (int i = 0; i < setLayouts.Length; i++) hash.Add(setLayouts[i]);
            _hash = hash.ToHashCode();
        }

        /// <summary>The handles this key is over, in slot order.</summary>
        internal ReadOnlySpan<ulong> SetLayouts => _setLayouts;

        /// <inheritdoc/>
        public bool Equals(VulkanPipelineLayoutKey other)
        {
            if (ReferenceEquals(_setLayouts, other._setLayouts)) return true;
            if (_setLayouts is null || other._setLayouts is null) return false;
            if (_hash != other._hash || _setLayouts.Length != other._setLayouts.Length) return false;

            for (int i = 0; i < _setLayouts.Length; i++)
            {
                if (_setLayouts[i] != other._setLayouts[i]) return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is VulkanPipelineLayoutKey other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _hash;
    }

    /// <summary>
    /// CONTENT-DEDUPLICATED <c>VkPipelineLayout</c>s (V-D5), and the home of 8.3's THIRD defence.
    ///
    /// <para><b>WHY DEDUP MATTERS HERE AND NOT ONLY ON THE SET LAYOUTS.</b> Vulkan invalidates bound descriptor
    /// sets on a pipeline switch from the first INCOMPATIBLE set index onward, and compatibility is decided over
    /// the set layouts. Sharing the pipeline layout object as well means two pipelines built from the same
    /// layouts are compatible for their whole array by identity, which is the cheapest possible answer to the
    /// question row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/521) asks at every pipeline switch.</para>
    ///
    /// <para><b>NO PUSH CONSTANT RANGES, EVER (V-D8).</b> The seam has no push-constant concept, so using them
    /// would mean either inventing seam API with one backend behind it or having this backend silently promote
    /// some uniform buffer and diverge from what the other two do. Their absence is also what makes row 11's
    /// compatibility computation a pure set-layout prefix compare with no second term.</para>
    ///
    /// <para><b>AND THIS IS WHERE THE DYNAMIC UNIFORM LIMIT IS COUNTED (8.3's third defence).</b> The limit is a
    /// PIPELINE LAYOUT limit rather than a set limit, so this is the only place in the backend that can ask the
    /// question at all. The check runs BEFORE the dedup lookup, so a pipeline layout that is over the limit is
    /// refused every time it is asked for rather than only the first time. Row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523) is what calls this: it lands with the pipeline, and
    /// the counting and the refusal land here, one row early, because this row owns the limit.</para>
    ///
    /// <para><b>Teardown, threading and ownership are <see cref="VulkanDescriptorSetLayoutCache"/>'s, for the
    /// same reasons.</b> One short lock held across the create, and nothing but the device's teardown destroys a
    /// handle.</para>
    /// </summary>
    internal sealed class VulkanPipelineLayoutCache
    {
        readonly IVulkanDescriptorApi _api;
        readonly uint _maxDynamicUniformBuffers;
        readonly object _gate = new();
        readonly Dictionary<VulkanPipelineLayoutKey, ulong> _byContent = new();

        int _requests;

        /// <param name="api">The native descriptor seam.</param>
        /// <param name="maxDynamicUniformBuffers">The device's <c>maxDescriptorSetUniformBuffersDynamic</c>, or 0
        /// when it was never read, which degrades to Vulkan's required minimum
        /// (<see cref="VulkanDescriptorLimits.EffectiveLimit"/>).</param>
        internal VulkanPipelineLayoutCache(IVulkanDescriptorApi api, uint maxDynamicUniformBuffers)
        {
            ArgumentNullException.ThrowIfNull(api);

            _api = api;
            _maxDynamicUniformBuffers = maxDynamicUniformBuffers;
        }

        /// <summary>How many distinct set-layout ARRAYS have a pipeline layout.</summary>
        internal int DistinctPipelineLayoutCount
        {
            get { lock (_gate) return _byContent.Count; }
        }

        /// <summary>How many pipelines have asked, distinct or not.</summary>
        internal int RequestCount
        {
            get { lock (_gate) return _requests; }
        }

        /// <summary>The device limit this cache measures against, after the unread-degrades-to-the-floor
        /// rule.</summary>
        internal uint MaxDynamicUniformBuffers => VulkanDescriptorLimits.EffectiveLimit(_maxDynamicUniformBuffers);

        /// <summary>
        /// The shared <c>VkPipelineLayout</c> for one pipeline's layout array, in SLOT order. The entry point row
        /// 13 calls: it takes the engine's own layout objects, so the dynamic uniform count and the set-layout
        /// handles come from one place and cannot disagree.
        /// </summary>
        /// <exception cref="NotSupportedException">The layouts spend more dynamic uniform descriptors between
        /// them than the device allows.</exception>
        internal ulong GetOrCreate(IReadOnlyList<VulkanResourceLayout> layouts)
        {
            ArgumentNullException.ThrowIfNull(layouts);

            var handles = new ulong[layouts.Count];
            int dynamicUniforms = 0;
            for (int i = 0; i < layouts.Count; i++)
            {
                VulkanResourceLayout layout = layouts[i]
                    ?? throw new ArgumentException(
                        "A native Vulkan pipeline layout was built over a null resource layout at slot "
                        + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".", nameof(layouts));

                handles[i] = layout.SetLayout;
                dynamicUniforms += layout.DynamicUniformCount;
            }

            return GetOrCreate(handles, dynamicUniforms);
        }

        /// <summary>
        /// The same, over already-resolved handles and an already-summed count. Separate so the counting rule and
        /// the caching rule can each be driven on their own device-free.
        /// </summary>
        /// <param name="setLayouts">The handles in slot order. Taken over rather than copied: the caller builds a
        /// fresh array and must not mutate it afterwards.</param>
        /// <param name="dynamicUniformCount">The sum of the layouts' dynamic uniform descriptors.</param>
        internal ulong GetOrCreate(ulong[] setLayouts, int dynamicUniformCount)
        {
            ArgumentNullException.ThrowIfNull(setLayouts);

            // BEFORE the lookup, so a layout over the limit is refused on every request rather than on the first.
            VulkanDescriptorLimits.RequirePipelineWithinLimit(
                dynamicUniformCount, _maxDynamicUniformBuffers, setLayouts.Length);

            var key = new VulkanPipelineLayoutKey(setLayouts);

            lock (_gate)
            {
                _requests++;

                if (_byContent.TryGetValue(key, out ulong existing)) return existing;

                ulong created = _api.CreatePipelineLayout(setLayouts);
                _byContent[key] = created;
                return created;
            }
        }

        /// <summary>Destroy every shared handle. Called ONCE, from the device's teardown window, and returns how
        /// many were destroyed.</summary>
        internal int DestroyAll()
        {
            lock (_gate)
            {
                int destroyed = _byContent.Count;
                foreach (ulong handle in _byContent.Values) _api.DestroyPipelineLayout(handle);
                _byContent.Clear();
                return destroyed;
            }
        }
    }
}
