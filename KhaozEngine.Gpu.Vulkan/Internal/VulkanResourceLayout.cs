using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuResourceLayout"/> on the native Vulkan backend: a SHARED <c>VkDescriptorSetLayout</c> plus
    /// the binding table and the per-type cost that every set built on it spends and restores.
    ///
    /// <para><b>THE HANDLE IS SHARED AND THIS OBJECT DOES NOT OWN IT (V-D5).</b> Two layouts created from
    /// identical descriptions get the same <c>VkDescriptorSetLayout</c> from
    /// <see cref="VulkanDescriptorSetLayoutCache"/>, which is what makes row 11's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521) pipeline-layout compatibility test a POINTER COMPARE
    /// rather than a structural walk, and what makes bound descriptors survive a pipeline switch at all. So
    /// <see cref="Dispose"/> destroys nothing: ending the handle here would leave every other layout with the same
    /// content naming a destroyed object. The cache retires them all at device teardown.</para>
    ///
    /// <para><b>THE INCUMBENT CREATES ONE HANDLE PER OBJECT.</b> Nothing there is ever compatible with anything,
    /// so every pipeline switch forces a full rebind of every set. That is the cost this type exists to not
    /// pay.</para>
    ///
    /// <para><b>BINDING INDEX EQUALS ELEMENT INDEX AND <c>descriptorCount</c> IS ALWAYS 1 (8.1)</b>, which is what
    /// lets <see cref="VulkanResourceSet"/> match its resources to this layout's elements positionally with no
    /// lookup at all.</para>
    ///
    /// <para><b>IT HOLDS NO SEAM AND NO CACHE.</b> The cache is a constructor parameter and not a field, so a
    /// layout carries a handle and no way to make another one. That is not tidiness: row 11 holds layouts on
    /// the recording path, and a layout that could reach the descriptor seam would carry
    /// <c>vkAllocateDescriptorSets</c> into the recorder's field graph and break decision V-D2's unreachability
    /// walk.</para>
    /// </summary>
    internal sealed class VulkanResourceLayout : IGpuResourceLayout
    {
        readonly GpuResourceLayoutElement[] _elements;
        readonly VulkanDescriptorBinding[] _bindings;

        /// <param name="setLayouts">The device's ONE set-layout cache, asked here and never held.</param>
        /// <param name="description">The seam's description. Its element array is COPIED, because it is a public
        /// struct holding a reference and a caller that reused or mutated it would otherwise re-shape a layout
        /// whose native handle has already been created and shared.</param>
        internal VulkanResourceLayout(VulkanDescriptorSetLayoutCache setLayouts,
            in GpuResourceLayoutDescription description)
        {
            ArgumentNullException.ThrowIfNull(setLayouts);

            GpuResourceLayoutElement[] source = description.Elements ?? [];
            _elements = new GpuResourceLayoutElement[source.Length];
            Array.Copy(source, _elements, source.Length);

            // THROWS on a declared-dynamic element that is not a uniform buffer, before anything native happens.
            // See VulkanDescriptorPolicy.TypeFor for why that refusal is wider than the Direct3D 11 backend's.
            _bindings = VulkanDescriptorPolicy.BindingsFor(new GpuResourceLayoutDescription(_elements));

            Counts = VulkanDescriptorCounts.ForBindings(_bindings);
            DynamicUniformCount = VulkanDescriptorPolicy.DynamicUniformCount(_bindings);

            int declared = 0;
            for (int i = 0; i < _elements.Length; i++)
            {
                if (_elements[i].Dynamic) declared++;
            }

            DeclaredDynamicCount = declared;

            SetLayout = setLayouts.GetOrCreate(_bindings);
        }

        /// <summary>The SHARED <c>VkDescriptorSetLayout</c>. Identity-equal to every other layout with the same
        /// content, and destroyed by nobody but the cache.</summary>
        internal ulong SetLayout { get; }

        /// <summary>The declared elements, in declaration order. Same order as a resource set's resources.</summary>
        internal ReadOnlySpan<GpuResourceLayoutElement> Elements => _elements;

        /// <summary>The binding table, in the same order. Binding index equals element index.</summary>
        internal ReadOnlySpan<VulkanDescriptorBinding> Bindings => _bindings;

        /// <summary>What a set built on this layout costs the pool, on all seven counted types. Carried by every
        /// set for its life so its free restores exactly what its allocation spent.</summary>
        internal VulkanDescriptorCounts Counts { get; }

        /// <summary>How many <c>UNIFORM_BUFFER_DYNAMIC</c> descriptors this layout spends. Under V-D4 that is
        /// simply its uniform buffer element count, whether or not any of them is declared dynamic, and it is the
        /// number <c>maxDescriptorSetUniformBuffersDynamic</c> is measured against once summed across a
        /// pipeline.</summary>
        internal int DynamicUniformCount { get; }

        /// <summary>How many elements carry the ENGINE's own <see cref="GpuResourceLayoutElement.Dynamic"/> flag,
        /// which decides only whether the caller's per-draw offset is added on top of the ring base for that
        /// element (V-D4). Deliberately a different number from <see cref="DynamicUniformCount"/>, and the
        /// distinction is the one most easily lost.</summary>
        internal int DeclaredDynamicCount { get; }

        /// <summary>Element count, which is also the required resource count of any set built on this
        /// layout.</summary>
        internal int ElementCount => _elements.Length;

        /// <summary>True once disposed. Nothing native is released: the handle is shared and outlives this
        /// object. The flag exists so a use-after-dispose is a stated error rather than a silently working
        /// call.</summary>
        internal bool IsDisposed { get; private set; }

        /// <inheritdoc/>
        /// <remarks>Destroys nothing, deliberately. See the class note.</remarks>
        public void Dispose() => IsDisposed = true;

        /// <summary>A layout this backend created, refused by name for anything else. Shared by the resource set
        /// and by row 13's pipelines, because both would otherwise carry the same message.</summary>
        internal static VulkanResourceLayout Require(IGpuResourceLayout? layout, string what)
            => layout as VulkanResourceLayout
                ?? throw new ArgumentException(
                    $"The resource layout handed to {what} was not created by the native Vulkan backend, so it "
                    + "carries no VkDescriptorSetLayout and no per-type descriptor cost. Create it through the "
                    + "same IGpuDevice.Factory the set or the pipeline is being created from.",
                    nameof(layout));
    }
}
