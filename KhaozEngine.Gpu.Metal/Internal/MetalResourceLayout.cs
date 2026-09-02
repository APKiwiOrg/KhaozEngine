using System;
using System.Globalization;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// <see cref="IGpuResourceLayout"/> ON THE NATIVE METAL BACKEND: THE DECLARATION ORDER, AND NOT ONE INDEX.
    /// Work-breakdown row 10 (https://github.com/APKiwiOrg/KhaozEngine/issues/576), section 8.1 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>THERE IS NO NATIVE OBJECT HERE AT ALL, and that is Metal rather than a choice.</b> Metal has no
    /// <c>MTLResourceLayout</c>, no descriptor-set layout and nothing to create on the device: an argument table
    /// is addressed by integer per stage and a layout is purely the engine's own bookkeeping. So this holds a
    /// copied element array, and creation makes no native call, takes no lock and cannot fail on the
    /// device.</para>
    ///
    /// <para><b>THE PER-KIND DECLARATION-ORDER ARITHMETIC IS DELIBERATELY ABSENT (2.2b).</b> The incumbent's
    /// <c>MTLResourceLayout</c> is exactly this class plus per-kind counters, and <c>GetBufferBase</c> and its
    /// siblings re-walk the layout array on every single bind to sum them. That arithmetic is right only where
    /// the emission's first-reference order happens to equal declaration order, which is the mechanism behind
    /// three recorded production incidents. Section 2.2b rules that it is written ONCE, as the comparison inside
    /// <c>MetalShaderIndexTableTests</c>, and never on a shipped path. The authority for where a resource landed
    /// is <see cref="MetalShaderIndexTable"/>, read off the emitted MSL. Nothing here counts anything, and adding
    /// a counter to this type is the specific regression the ruling forbids.</para>
    ///
    /// <para><b>WHAT DECLARATION ORDER IS STILL FOR: ELEMENT INDEX IS THE BINDING NUMBER.</b> The table is keyed
    /// on <c>(set, binding, stage)</c> where <c>binding</c> is the element's POSITION in this array and
    /// <c>set</c> is this layout's position in the pipeline's array. That positional assumption is the same one
    /// <c>VulkanDescriptorPolicy.BindingsFor</c> ships on, it is checked from both ends
    /// (<see cref="MetalShaderIndexTable.Build"/> refuses a decorated pair outside the declared arrays, and
    /// <see cref="MetalShaderIndexTable.RequireLayoutShape"/> refuses a pipeline whose declared array is a
    /// different shape), and holding the elements in order is the whole of what this type owes it.</para>
    ///
    /// <para><b>THE ELEMENT NAME IS A LABEL AND NOTHING JOINS THROUGH IT.</b> Under 2.2b the join is keyed on the
    /// SPIR-V id, so a blank or duplicated name is not refused here: it is not wrong, it is unread. The name is
    /// still quoted in every refusal below, because "element 4" is unactionable in a seven-element material
    /// layout.</para>
    ///
    /// <para><b>IT CARRIES THE LIVENESS TOKEN LIKE EVERY OTHER RESOURCE</b>, so a set or a pipeline built from a
    /// layout of a DIFFERENT device is refused by name through <see cref="MetalResourceOwnership"/> rather than
    /// resolving positionally against an array that means something else. A layout is plain managed data, so the
    /// mistake would otherwise be invisible.</para>
    /// </summary>
    internal sealed class MetalResourceLayout : IGpuResourceLayout, IMetalOwnedResource
    {
        readonly IDeviceLiveness _liveness;
        readonly GpuResourceLayoutElement[] _elements;

        /// <param name="liveness">The creating device's token, which is its identity.</param>
        /// <param name="description">The seam's description. Its element array is COPIED, because it is a public
        /// struct holding a reference: a caller that reused or mutated the array would otherwise re-shape a
        /// layout that sets and pipelines have already been built against.</param>
        /// <exception cref="ArgumentException">An element declares a per-draw dynamic offset on a kind that has
        /// nowhere to put one.</exception>
        /// <exception cref="ArgumentOutOfRangeException">An element declares a kind with no Metal index space,
        /// which is a new <see cref="GpuResourceKind"/> member rather than a caller error.</exception>
        internal MetalResourceLayout(IDeviceLiveness liveness, in GpuResourceLayoutDescription description)
        {
            ArgumentNullException.ThrowIfNull(liveness);

            _liveness = liveness;

            GpuResourceLayoutElement[] source = description.Elements ?? [];
            _elements = new GpuResourceLayoutElement[source.Length];
            Array.Copy(source, _elements, source.Length);

            for (int i = 0; i < _elements.Length; i++) Validate(_elements[i], i);

            // The copy wrapped back up, so a pipeline can hand its declared array to
            // MetalShaderIndexTable.RequireLayoutShape without rebuilding one and without handing out the
            // mutable array this object's own answers depend on.
            Description = new GpuResourceLayoutDescription(_elements);
        }

        /// <inheritdoc/>
        public IDeviceLiveness Owner => _liveness;

        /// <summary>The declared elements, in declaration order, which is binding order. Same order as a resource
        /// set's resources and the same order the index table's <c>binding</c> key counts in.</summary>
        internal ReadOnlySpan<GpuResourceLayoutElement> Elements => _elements;

        /// <summary>Element count, which is also the required resource count of any set built on this
        /// layout.</summary>
        internal int ElementCount => _elements.Length;

        /// <summary>
        /// This layout as the seam struct again, over the COPIED array. Row 11
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577) collects one of these per declared slot and
        /// hands the array to <see cref="MetalShaderIndexTable.RequireLayoutShape"/> at pipeline creation, which
        /// is the first moment the declared shape and the reflected one exist together.
        /// </summary>
        internal GpuResourceLayoutDescription Description { get; }

        /// <summary>True once disposed. Nothing native is released because nothing native was created: the flag
        /// is what <see cref="Require"/> refuses on, which is what makes a use-after-dispose a named error rather
        /// than a silently working call.</summary>
        internal bool IsDisposed { get; private set; }

        /// <inheritdoc/>
        /// <remarks>Releases nothing, because a Metal resource layout IS its element array. See the class
        /// note.</remarks>
        public void Dispose() => IsDisposed = true;

        /// <summary>The declared element at one binding, for a set resolving its resources positionally.</summary>
        internal GpuResourceLayoutElement ElementAt(int binding) => _elements[binding];

        /// <summary>
        /// A layout this backend created, on THIS device, NOT DISPOSED, refused by name for anything else. Shared
        /// by the resource set and by row 11's pipelines, because both would otherwise carry the same message,
        /// and it is the single place the disposed check lives: a set or a pipeline built from a disposed layout
        /// would otherwise resolve positionally against an array its owner has already let go of, and work.
        /// </summary>
        /// <exception cref="ArgumentException">No layout, another backend's, or another device's.</exception>
        /// <exception cref="ObjectDisposedException">This backend's layout, on this device, already
        /// disposed.</exception>
        internal static MetalResourceLayout Require(IGpuResourceLayout? layout, IDeviceLiveness owner,
            string what)
        {
            if (layout is null)
            {
                throw new ArgumentException(
                    $"{what} was given no resource layout. Every element of a native Metal resource set is "
                    + "matched to a declared element positionally, so there is nothing to match against.",
                    nameof(layout));
            }

            MetalResourceLayout typed = MetalResourceOwnership.Require<MetalResourceLayout>(
                layout, owner, nameof(layout));

            if (typed.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(MetalResourceLayout),
                    $"{what} was given a native Metal resource layout that is already disposed. A layout releases "
                    + "nothing on this backend, so the call would work, which is exactly why the flag is checked "
                    + "here: the caller believes the declaration is still theirs to build against.");
            }

            return typed;
        }

        // ONE ELEMENT'S DECLARATION, CHECKED AT CREATION rather than at the bind that would suffer for it. Both
        // refusals here are about a declaration that cannot be honoured at all, which is the only class a layout
        // can see: it has no resources yet, so everything about what is BOUND is the resource set's.
        static void Validate(in GpuResourceLayoutElement element, int binding)
        {
            string where = $"'{Describe(element)}' at binding {binding.ToString(CultureInfo.InvariantCulture)} "
                + "of a native Metal resource layout";

            // CALLED FOR ITS REFUSAL. The answer is not stored: a resource set resolves the space from the same
            // helper when it resolves the resource, and a second copy here is a second thing to keep in step.
            // What this buys is that a GpuResourceKind with no Metal index space is refused at LAYOUT creation,
            // naming the element, rather than at the first bind of a set built on it.
            MetalIndexSpace space = MetalIndexSpaces.For(element.Kind);

            if (!element.Dynamic || space == MetalIndexSpace.Buffer) return;

            throw new ArgumentException(
                $"{where} declares a {element.Kind} with a per-draw dynamic offset. On Metal the per-draw offset "
                + "is applied with -setVertexBufferOffset:atIndex: or its stage sibling, which exists only in the "
                + "[[buffer(n)]] space, so a dynamic offset on a texture or a sampler element has nowhere to go "
                + "and would be silently dropped at every bind. Declare it on the buffer element the offset is "
                + "meant for.",
                nameof(element));
        }

        // THIS BACKEND IS WIDER THAN THE CONTRACT, AND THAT IS A DOCUMENTED SUPERSET RATHER THAN THE SEAM.
        // GpuResourceLayoutElement.Dynamic is a dynamic-offset UNIFORM buffer and only that, since #597 narrowed
        // it. Metal's setBufferOffset: works at any buffer index whatever the kind, so a dynamic structured
        // element is honoured here and is refused on both siblings: VulkanDescriptorPolicy because a storage
        // descriptor has no dynamic offset at all, and D3D11ResourceLayout because a structured buffer binds
        // through a view created once over the whole buffer and neither *SetShaderResources nor
        // *SetUnorderedAccessViews has a per-bind window. The second of those is not a gap anyone can close: the
        // API has nowhere to put the number, which is why the seam narrowed rather than the siblings widening.
        // Accepting it here is kept because refusing a declaration this backend can honour would buy nothing, and
        // it is NOT portable: a consumer who writes one is macOS-only, which is what the seam doc now says.
        // https://github.com/APKiwiOrg/KhaozEngine/issues/597.

        static string Describe(in GpuResourceLayoutElement element)
            => string.IsNullOrEmpty(element.Name) ? "<unnamed>" : element.Name;
    }
}
