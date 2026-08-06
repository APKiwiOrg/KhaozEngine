using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE DYNAMIC UNIFORM DESCRIPTOR OF A SET, as row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/521)
    /// needs it at bind time. The set resolves all of this at CREATION, which is V-M11's no-work-at-draw-time rule
    /// applied to <see cref="GpuBufferRange"/>.
    /// <para>
    /// THE OFFSET ROW 11 COMPOSES IS <c>frameBase + RangeOffset + callerDynamicOffset</c>, where
    /// <c>frameBase</c> comes from <see cref="Ring"/>, <see cref="RangeOffset"/> is fixed for the set's life, and
    /// the caller's own per-draw offset is added only when <see cref="AppliesCallerOffset"/> is set. That last
    /// flag is the ONE thing <see cref="GpuResourceLayoutElement.Dynamic"/> decides under decision V-D4.
    /// </para>
    /// <para>
    /// THE DESCRIPTOR'S OWN <c>offset</c> IS ZERO FOR EVERY ONE OF THESE, which is what makes that composition
    /// correct rather than double-counted: the whole offset travels in <c>pDynamicOffsets</c>.
    /// </para>
    /// </summary>
    /// <param name="Binding">The binding number, which is also the element index.</param>
    /// <param name="Ring">The bound buffer's uniform ring, whose current segment base is the first term.</param>
    /// <param name="RangeOffset">The set's own <see cref="GpuBufferRange.Offset"/>, 0 at every shipped site.</param>
    /// <param name="Range">The descriptor's range, which is the bind window and never the stride (V-M6).</param>
    /// <param name="AppliesCallerOffset">Whether the element was declared dynamic, so the caller's per-draw
    /// offset is added.</param>
    internal readonly record struct VulkanDynamicUniform(
        uint Binding, VulkanUniformRing Ring, ulong RangeOffset, ulong Range, bool AppliesCallerOffset);

    /// <summary>
    /// <see cref="IGpuResourceSet"/> on the native Vulkan backend: ONE <c>VkDescriptorSet</c>, allocated and
    /// written ONCE at creation with a single <c>vkUpdateDescriptorSets</c> covering every binding, and immutable
    /// for the rest of its life (V-D1).
    ///
    /// <para><b>THE WRITE-ONCE IMMUTABLE SET IS A PORT, NOT AN INVENTION, and saying so matters.</b> The
    /// incumbent already does exactly this. What is new is that it now holds BY CONSTRUCTION rather than by the
    /// incumbent happening to be written that way: neither the descriptor pool nor the descriptor seam is
    /// reachable from the recording type (V-D2), so a draw-time allocation or a draw-time write is something the
    /// type graph will not express. The naive Vulkan renderer does the opposite, which is why this is stated as a
    /// decision at all.</para>
    ///
    /// <para><b>THE RANGE IS THE BIND WINDOW, AND THIS IS WHERE IT IS WRITTEN (V-M6).</b> Never
    /// <c>VK_WHOLE_SIZE</c>, because a whole-size range combined with a dynamic offset addresses past the end of
    /// the buffer. And never the STRIDE, which is the shape that looks safe and is not: at the last frame slot a
    /// range of <c>stride</c> overruns the buffer by exactly the caller's own offset, and five shipped renderers
    /// pass a non-zero one. It is <see cref="GpuBufferRange.Size"/> where the set was created from a range, and
    /// the buffer's own LOGICAL size where it was created from a bare buffer. Row 8
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/518) owns the invariant and its helper asserts here at
    /// creation, row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/521) composes the offset the VUID
    /// measures, and all three have to agree or
    /// <c>VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979</c> fails on the last frame slot only.</para>
    ///
    /// <para><b>WHICH OF <c>offset</c> AND <c>pDynamicOffsets</c> CARRIES A RANGE OFFSET DEPENDS ON THE
    /// DESCRIPTOR TYPE, and getting it wrong doubles the offset.</b> A dynamic uniform buffer is written with
    /// <c>offset = 0</c> and its whole offset travels at bind time, because Vulkan ADDS the dynamic offset to the
    /// descriptor's own. A non-dynamic buffer (every storage buffer here) has no bind-time term at all, so its
    /// <see cref="GpuBufferRange.Offset"/> goes into the descriptor's <c>offset</c> field, which is the only
    /// place it can go.</para>
    ///
    /// <para><b>EVERY <see cref="GpuBufferRange"/> IS RESOLVED HERE AND NEVER AT DRAW TIME (V-M11's rule applied
    /// to buffers).</b> A set is created once at load time across 68 shipped call sites and bound thousands of
    /// times, so anything resolved at a bind is resolved for nothing.</para>
    ///
    /// <para><b>DISPOSAL IS ONE TERMINAL RETIRE (V-F9)</b>, through the pool, because a descriptor set freed
    /// while a submission that binds it is still executing is undefined behaviour of the quiet kind. The pool's
    /// per-type budget is restored at that deferred free, on ALL SEVEN counted types.</para>
    /// </summary>
    internal sealed class VulkanResourceSet : IGpuResourceSet
    {
        readonly VulkanDescriptorPoolManager _pools;
        readonly VulkanDescriptorCounts _counts;
        readonly VulkanDescriptorSetToken _token;
        readonly VulkanDynamicUniform[] _dynamicUniforms;

        bool _disposed;

        /// <param name="api">The native descriptor seam, used for the ONE update and NOT held. A set that kept it
        /// would carry <c>vkUpdateDescriptorSets</c> into the field graph of anything that holds a set.</param>
        /// <param name="pools">The device's descriptor pools, held because the set frees itself back into
        /// them.</param>
        /// <param name="description">The seam's description: a layout plus one resource per element, in
        /// order.</param>
        internal VulkanResourceSet(IVulkanDescriptorApi api, VulkanDescriptorPoolManager pools,
            in GpuResourceSetDescription description)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(pools);

            VulkanResourceLayout layout = VulkanResourceLayout.Require(description.Layout, "a resource set");
            IGpuBindableResource[] resources = description.Resources ?? [];

            if (resources.Length != layout.ElementCount)
            {
                throw new ArgumentException(
                    "A native Vulkan resource set was built with "
                    + resources.Length.ToString(CultureInfo.InvariantCulture)
                    + " resources against a layout declaring "
                    + layout.ElementCount.ToString(CultureInfo.InvariantCulture)
                    + " elements. Resources are matched to elements POSITIONALLY, so a count mismatch is not a "
                    + "shortfall the backend can work around.",
                    nameof(description));
            }

            _pools = pools;
            _counts = layout.Counts;
            Layout = layout;

            // EVERYTHING IS VALIDATED AND RESOLVED BEFORE ANYTHING NATIVE HAPPENS. A caller error therefore
            // leaves no half-built descriptor set behind, which is the same discipline row 9's resource
            // constructors take from the other direction.
            var writes = new VulkanDescriptorWrite[layout.ElementCount];
            var dynamics = new List<VulkanDynamicUniform>(layout.DynamicUniformCount);
            for (int i = 0; i < layout.ElementCount; i++)
            {
                writes[i] = Resolve(layout, resources[i], i, dynamics);
            }

            _dynamicUniforms = dynamics.ToArray();

            _token = pools.Allocate(layout.SetLayout, _counts);

            try
            {
                // ONE CALL COVERING EVERY BINDING (V-D1), and none at all for a layout with no elements, where
                // there is nothing to write and a zero-length update would be a driver call that says nothing.
                if (writes.Length != 0) api.UpdateSet(_token.Set, writes);
            }
            catch
            {
                // The set exists and nothing has been submitted against it, so it goes back through the normal
                // deferred free rather than being leaked for the device's life.
                pools.Free(_token, _counts);
                throw;
            }
        }

        /// <summary>The layout this set satisfies, whose SHARED <c>VkDescriptorSetLayout</c> is what row 11
        /// compares for pipeline compatibility.</summary>
        internal VulkanResourceLayout Layout { get; }

        /// <summary>The <c>VkDescriptorSet</c> a bind names.</summary>
        internal ulong DescriptorSet => _token.Set;

        /// <summary>
        /// The dynamic uniform descriptors, in BINDING ORDER, which is the order <c>pDynamicOffsets</c> is
        /// positional in. Row 11 reads this at bind time and composes one offset per entry.
        /// <para>
        /// ROW 11 MUST READ THESE INTO ITS OWN RECORDS RATHER THAN HOLDING THE SET, and that is a decision V-D2
        /// obligation rather than a style note: this object holds the descriptor POOL, so a recorder with a field
        /// of this type would reach <c>vkAllocateDescriptorSets</c> and
        /// <c>VulkanRecordingUnreachabilityTests</c> would fail. The values here are all plain data plus a ring,
        /// none of which reaches the pool.
        /// </para>
        /// </summary>
        internal ReadOnlySpan<VulkanDynamicUniform> DynamicUniforms => _dynamicUniforms;

        /// <summary>
        /// THE SAME THREE THINGS AS ONE VALUE, which is what row 11's per-slot record actually holds: the
        /// descriptor set handle, the shared set-layout handle, and the dynamic uniform array by reference. The
        /// obligation above stated as a type, so a bind record cannot accidentally hold the set by holding
        /// "everything a bind needs".
        /// <para>
        /// THE ARRAY IS HANDED OVER BY REFERENCE AND NEVER COPIED, so recording a bind allocates nothing on a path
        /// that runs thousands of times a frame. Neither side mutates it: this set writes it once at creation and a
        /// bind reads it.
        /// </para>
        /// </summary>
        internal VulkanBoundSet AsBound => new(_token.Set, Layout.SetLayout, _dynamicUniforms);

        /// <summary>What this set spends in its pool, restored in full when it is freed.</summary>
        internal VulkanDescriptorCounts Counts => _counts;

        /// <summary>True once disposed, whether or not the deferred free has run yet.</summary>
        internal bool IsDisposed => _disposed;

        /// <inheritdoc/>
        /// <remarks>Idempotent, because a consumer disposing a set twice is a teardown-order accident rather than
        /// a defect, and freeing the same descriptor set twice is undefined behaviour.</remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pools.Free(_token, _counts);
        }

        /// <summary>A set this backend created, refused by name for anything else. Row 11 binds through
        /// this.</summary>
        internal static VulkanResourceSet Require(IGpuResourceSet? set, string what)
            => set as VulkanResourceSet
                ?? throw new ArgumentException(
                    $"The resource set handed to {what} was not created by the native Vulkan backend, so it "
                    + "carries no VkDescriptorSet. Create it through the same IGpuDevice.Factory.",
                    nameof(set));

        // ONE BINDING'S WRITE, fully resolved. Every refusal here names the element by its declared name, because
        // a message about "element 4" is unactionable in a seven-element material layout.
        VulkanDescriptorWrite Resolve(VulkanResourceLayout layout, IGpuBindableResource? resource, int index,
            List<VulkanDynamicUniform> dynamics)
        {
            VulkanDescriptorBinding binding = layout.Bindings[index];
            GpuResourceLayoutElement element = layout.Elements[index];

            if (resource is null)
            {
                throw new ArgumentException(
                    $"'{element.Name}' at binding {index.ToString(CultureInfo.InvariantCulture)} of a native "
                    + "Vulkan resource set is null. A descriptor set is written once at creation and never again, "
                    + "so there is no later point at which a missing resource could arrive.",
                    nameof(resource));
            }

            if (VulkanDescriptorPolicy.IsBuffer(binding.Type))
                return ResolveBuffer(binding, element, resource, index, dynamics);

            if (binding.Type == VulkanDescriptorType.Sampler)
            {
                VulkanSampler sampler = Require<VulkanSampler>(resource, element, index, "a sampler");
                return new VulkanDescriptorWrite(binding.Binding, binding.Type, Buffer: 0, BufferOffset: 0,
                    BufferRange: 0, ImageView: 0, VulkanDescriptorImageLayout.None, sampler.Handle);
            }

            return ResolveImage(binding, element, resource, index);
        }

        VulkanDescriptorWrite ResolveBuffer(in VulkanDescriptorBinding binding,
            in GpuResourceLayoutElement element, IGpuBindableResource resource, int index,
            List<VulkanDynamicUniform> dynamics)
        {
            ulong rangeOffset;
            ulong range;
            VulkanBuffer buffer;

            if (resource is GpuBufferRange window)
            {
                buffer = Require<VulkanBuffer>(window.Buffer, element, index, "a buffer");
                rangeOffset = window.Offset;
                range = window.Size;
            }
            else
            {
                buffer = Require<VulkanBuffer>(resource, element, index, "a buffer");
                rangeOffset = 0;

                // THE BUFFER'S OWN LOGICAL SIZE, which on a ring-backed uniform buffer is emphatically not its
                // native allocation: that is FramesInFlight segments and binding it whole would address every
                // frame's copy at once.
                range = buffer.SizeInBytes;
            }

            RequireWindow(element, index, buffer, rangeOffset, range);

            bool dynamic = VulkanDescriptorPolicy.IsDynamic(binding.Type);

            if (dynamic)
            {
                VulkanUniformRing ring = buffer.Ring
                    ?? throw new ArgumentException(
                        $"'{element.Name}' at binding {index.ToString(CultureInfo.InvariantCulture)} of a native "
                        + "Vulkan resource set is a uniform buffer element bound to a "
                        + buffer.Describe()
                        + ", which is not ring-backed. Every uniform element becomes a dynamic uniform descriptor "
                        + "on this backend (decision V-D4) and its bind-time offset is the ring's per-frame base, "
                        + "so a buffer with no ring would be bound with an offset nothing supplies. Create it "
                        + "with GpuBufferUsage.UniformBuffer.",
                        nameof(resource));

                // V-M6, at CREATION, through row 8's own helper, with the caller's per-draw offset taken as 0
                // because it is not knowable here. With a zero caller offset this is IMPLIED by the logical-size
                // check above, and saying so is more useful than implying it is load-bearing: what it buys is
                // that the invariant is STATED by one shared method at the place the range is written and again
                // at the place the offset is composed, so the two cannot drift into disagreeing. Row 11 is where
                // it can really fail, on the last frame slot only, for a caller offset five shipped renderers
                // pass.
                VulkanRingStride.RequireBindWindowFits(
                    rangeOffset, callerDynamicOffset: 0, range, ring.SegmentStrideBytes);

                dynamics.Add(new VulkanDynamicUniform(
                    binding.Binding, ring, rangeOffset, range, element.Dynamic));

                // OFFSET ZERO. The whole offset travels in pDynamicOffsets, and putting it in both would double
                // it. See the class note.
                return new VulkanDescriptorWrite(binding.Binding, binding.Type, buffer.Handle, BufferOffset: 0,
                    range, ImageView: 0, VulkanDescriptorImageLayout.None, Sampler: 0);
            }

            if (buffer.Ring is not null)
            {
                throw new ArgumentException(
                    $"'{element.Name}' at binding {index.ToString(CultureInfo.InvariantCulture)} of a native "
                    + "Vulkan resource set is a " + element.Kind
                    + " element bound to a ring-backed uniform buffer. A ring-backed buffer is "
                    + "FramesInFlight segments wide and its bind base is applied through the dynamic offset a "
                    + "storage descriptor does not have, so it would address segment zero on every frame while "
                    + "the writes went to the current one. Bind a buffer created with the structured usage.",
                    nameof(resource));
            }

            // NON-DYNAMIC, so the range offset has nowhere else to go and goes into the descriptor's own offset.
            return new VulkanDescriptorWrite(binding.Binding, binding.Type, buffer.Handle, rangeOffset, range,
                ImageView: 0, VulkanDescriptorImageLayout.None, Sampler: 0);
        }

        VulkanDescriptorWrite ResolveImage(in VulkanDescriptorBinding binding,
            in GpuResourceLayoutElement element, IGpuBindableResource resource, int index)
        {
            VulkanTexture texture = Require<VulkanTexture>(resource, element, index, "a texture");

            bool sampled = binding.Type == VulkanDescriptorType.SampledImage;
            ulong view = sampled ? texture.SampledView : texture.StorageView;

            if (view == 0)
            {
                throw new ArgumentException(
                    $"'{element.Name}' at binding {index.ToString(CultureInfo.InvariantCulture)} of a native "
                    + "Vulkan resource set is a " + element.Kind + " element bound to a " + texture.Describe()
                    + ", which has no " + (sampled ? "sampled" : "storage") + " image view. Every view is created "
                    + "at RESOURCE creation from the declared usage bits (decision V-M11) and none at a bind, so "
                    + "a missing view is a usage the texture was not created with rather than something this call "
                    + "could make. Add GpuTextureUsage."
                    + (sampled ? "Sampled" : "Storage") + " to its description.",
                    nameof(resource));
            }

            return new VulkanDescriptorWrite(binding.Binding, binding.Type, Buffer: 0, BufferOffset: 0,
                BufferRange: 0, view, VulkanDescriptorPolicy.ImageLayoutFor(binding.Type), Sampler: 0);
        }

        // The bind window has to be a real window inside the buffer the caller named, whatever the descriptor
        // type. Checked before the ring-specific V-M6 assertion, because a range that already leaves the LOGICAL
        // buffer is a caller error with a better message than "leaves its segment".
        static void RequireWindow(in GpuResourceLayoutElement element, int index, VulkanBuffer buffer,
            ulong rangeOffset, ulong range)
        {
            if (range != 0 && rangeOffset <= buffer.SizeInBytes && range <= buffer.SizeInBytes - rangeOffset)
                return;

            throw new ArgumentException(
                $"'{element.Name}' at binding {index.ToString(CultureInfo.InvariantCulture)} of a native Vulkan "
                + "resource set binds " + range.ToString(CultureInfo.InvariantCulture) + " bytes at offset "
                + rangeOffset.ToString(CultureInfo.InvariantCulture) + " of a " + buffer.Describe()
                + ". The descriptor's range is the BIND WINDOW and is written once at creation: it is never "
                + "VK_WHOLE_SIZE and never zero, and it has to be a window that exists inside the buffer's "
                + "logical size.",
                nameof(buffer));
        }

        static T Require<T>(object? resource, in GpuResourceLayoutElement element, int index, string what)
            where T : class
            => resource as T
                ?? throw new ArgumentException(
                    $"'{element.Name}' at binding {index.ToString(CultureInfo.InvariantCulture)} of a native "
                    + "Vulkan resource set declares " + element.Kind + ", which needs " + what
                    + " created by this backend. It was given a "
                    + (resource?.GetType().Name ?? "null") + ".",
                    nameof(resource));
    }
}
