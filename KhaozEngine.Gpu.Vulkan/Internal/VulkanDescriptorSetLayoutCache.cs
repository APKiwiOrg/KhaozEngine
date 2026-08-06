using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE CONTENT KEY OF DECISION V-D5: everything <c>vkCreateDescriptorSetLayout</c> reads, and nothing else.
    ///
    /// <para><b>WHAT IS IN IT.</b> The ordered binding table: for each binding, its NUMBER, its
    /// <c>VkDescriptorType</c>, its <c>descriptorCount</c> and its stage flags. Order is part of the key, because
    /// two layouts whose bindings are the same set in a different order are different objects to Vulkan.</para>
    ///
    /// <para><b>WHAT IS DELIBERATELY NOT IN IT, and each omission is load-bearing.</b> The element NAME is not:
    /// Vulkan binds by number, so two layouts differing only in names are one object to the driver, and giving
    /// them separate handles would make a genuinely compatible pipeline pair compare as incompatible.
    /// <see cref="GpuResourceLayoutElement.Dynamic"/> is not either, and that is the subtle one: the
    /// dynamic-ness the key carries is the DESCRIPTOR TYPE's
    /// (<see cref="VulkanDescriptorType.UniformBufferDynamic"/> against
    /// <see cref="VulkanDescriptorType.UniformBuffer"/>), which is the only dynamic-ness the create-info has.
    /// The engine's declared flag decides whether the CALLER's own per-draw offset is added on top at bind time
    /// (V-D4), which is a property of the bind rather than of the layout object, so including it would split one
    /// driver object into two for a distinction the driver cannot see.</para>
    /// </summary>
    internal readonly struct VulkanDescriptorLayoutKey : IEquatable<VulkanDescriptorLayoutKey>
    {
        readonly VulkanDescriptorBinding[] _bindings;
        readonly int _hash;

        /// <param name="bindings">The binding table, taken by reference and never mutated afterwards. The caller
        /// builds a fresh array per layout, so there is no aliasing with anything a consumer holds.</param>
        internal VulkanDescriptorLayoutKey(VulkanDescriptorBinding[] bindings)
        {
            ArgumentNullException.ThrowIfNull(bindings);

            _bindings = bindings;

            var hash = new HashCode();
            hash.Add(bindings.Length);
            for (int i = 0; i < bindings.Length; i++) hash.Add(bindings[i]);
            _hash = hash.ToHashCode();
        }

        /// <summary>The binding table this key is over.</summary>
        internal ReadOnlySpan<VulkanDescriptorBinding> Bindings => _bindings;

        /// <inheritdoc/>
        public bool Equals(VulkanDescriptorLayoutKey other)
        {
            if (ReferenceEquals(_bindings, other._bindings)) return true;
            if (_bindings is null || other._bindings is null) return false;
            if (_hash != other._hash || _bindings.Length != other._bindings.Length) return false;

            for (int i = 0; i < _bindings.Length; i++)
            {
                if (!_bindings[i].Equals(other._bindings[i])) return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is VulkanDescriptorLayoutKey other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _hash;
    }

    /// <summary>
    /// CONTENT-DEDUPLICATED <c>VkDescriptorSetLayout</c>s (V-D5), and the reason this is not a
    /// micro-optimisation.
    ///
    /// <para><b>IDENTITY-SHARED SET LAYOUTS ARE WHAT MAKE BOUND DESCRIPTORS SURVIVE A PIPELINE SWITCH.</b> Vulkan
    /// decides pipeline-layout compatibility by comparing the set layouts slot by slot, and two layouts with
    /// identical content compare equal only if the implementation says so. Handing out ONE handle per distinct
    /// CONTENT makes that comparison a pointer compare that always answers correctly, which is exactly what row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521) computes its compatible prefix with: a
    /// <c>VkDescriptorSetLayout</c> handle equality test, nothing deeper, no structural walk at bind time.</para>
    ///
    /// <para><b>THE INCUMBENT CREATES ONE PER <c>ResourceLayout</c> OBJECT WITH NO DEDUP AT ALL.</b> Two layouts
    /// built from identical descriptions are two handles there, so nothing is ever compatible with anything and
    /// every pipeline switch forces a full rebind of every set. That is the shape this type exists to not
    /// reproduce.</para>
    ///
    /// <para><b>CREATION IS FREE-THREADED, SO THIS TAKES ITS OWN SHORT LOCK.</b> The seam permits resource
    /// creation from any thread (V-W8) and two threads creating identically shaped layouts at once would
    /// otherwise both miss and both create, leaving one handle leaked and the pointer compare wrong for the rest
    /// of the run. The lock is held across the <c>vkCreateDescriptorSetLayout</c> itself rather than only around
    /// the dictionary, which is what makes "one handle per content" true rather than usually true, and a set
    /// layout creation is a rare load-time call rather than anything on a frame path.</para>
    ///
    /// <para><b>NOTHING BUT DEVICE TEARDOWN DESTROYS A HANDLE.</b> A handle is shared by every layout with the
    /// same content, so <c>IGpuResourceLayout.Dispose</c> cannot end one: the second layout would be left naming
    /// a destroyed object. <see cref="DestroyAll"/> runs once, in the device's teardown window, after the wait
    /// that made the GPU idle and before the liveness flip.</para>
    /// </summary>
    internal sealed class VulkanDescriptorSetLayoutCache
    {
        readonly IVulkanDescriptorApi _api;
        readonly object _gate = new();
        readonly Dictionary<VulkanDescriptorLayoutKey, ulong> _byContent = new();

        int _requests;

        /// <param name="api">The native descriptor seam. Held here and NOT handed to a layout object, so a
        /// <see cref="VulkanResourceLayout"/> carries a handle and no way to create another one.</param>
        internal VulkanDescriptorSetLayoutCache(IVulkanDescriptorApi api)
        {
            ArgumentNullException.ThrowIfNull(api);
            _api = api;
        }

        /// <summary>How many distinct CONTENTS have a handle. The observable half of the dedup claim: a run that
        /// creates ten identically shaped layouts leaves this at 1.</summary>
        internal int DistinctLayoutCount
        {
            get { lock (_gate) return _byContent.Count; }
        }

        /// <summary>How many layouts have asked, distinct or not. With <see cref="DistinctLayoutCount"/> this is
        /// the hit rate, which is what a diagnostic reports and what a test asserts the dedup on.</summary>
        internal int RequestCount
        {
            get { lock (_gate) return _requests; }
        }

        /// <summary>
        /// The shared <c>VkDescriptorSetLayout</c> for one binding table: the existing handle when this content
        /// has been seen, and a freshly created one otherwise.
        /// </summary>
        /// <param name="bindings">The binding table, which becomes the key and must not be mutated after this
        /// call.</param>
        internal ulong GetOrCreate(VulkanDescriptorBinding[] bindings)
        {
            ArgumentNullException.ThrowIfNull(bindings);

            var key = new VulkanDescriptorLayoutKey(bindings);

            lock (_gate)
            {
                _requests++;

                if (_byContent.TryGetValue(key, out ulong existing)) return existing;

                // INSIDE THE LOCK. Two threads that both missed would both create, and the loser's handle would
                // leak while every later compare against it answered "incompatible" for a layout that is not.
                ulong created = _api.CreateSetLayout(bindings);
                _byContent[key] = created;
                return created;
            }
        }

        /// <summary>
        /// Destroy every shared handle. Called ONCE, from the device's teardown window. Returns how many were
        /// destroyed, which is <see cref="DistinctLayoutCount"/> before the call and is reported so a teardown
        /// can say what it released.
        /// </summary>
        internal int DestroyAll()
        {
            lock (_gate)
            {
                int destroyed = _byContent.Count;
                foreach (ulong handle in _byContent.Values) _api.DestroySetLayout(handle);
                _byContent.Clear();
                return destroyed;
            }
        }
    }
}
