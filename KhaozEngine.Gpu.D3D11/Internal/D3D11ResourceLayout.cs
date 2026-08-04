using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuResourceLayout"/> for the native Direct3D 11 backend: the declared elements plus the
    /// WITHIN-LAYOUT register assignment of decision S2, computed ONCE here at creation.
    /// <para>
    /// THERE IS NO NATIVE OBJECT BEHIND THIS. Direct3D 11 has no resource-layout primitive, so a layout is purely
    /// the CPU-side numbering agreement between the emitted HLSL and the bind calls. That is why this type takes
    /// no device, holds no COM pointer, needs no liveness gate, and is testable device-free on any operating
    /// system. It is also why it is created eagerly and never cached: there is nothing to cache.
    /// </para>
    /// <para>
    /// The assignment stored here is RELATIVE to this layout. The pipeline-dependent half (the per-file base, from
    /// the sum over the layouts before this one in the pipeline's array) is applied when the set is bound, because
    /// one layout is legitimately used at different slots under different pipelines and the relative numbering is
    /// the part that does not vary. <see cref="D3D11RegisterScheme"/> holds both halves of the rule.
    /// </para>
    /// </summary>
    internal sealed class D3D11ResourceLayout : IGpuResourceLayout
    {
        readonly GpuResourceLayoutElement[] _elements;
        readonly D3D11RegisterSlot[] _slots;

        internal D3D11ResourceLayout(in GpuResourceLayoutDescription description)
        {
            // A copy, not the caller's array. The description is a public struct holding a reference, so a caller
            // that reuses or mutates its element array after creation would otherwise renumber a live layout.
            GpuResourceLayoutElement[] source = description.Elements ?? Array.Empty<GpuResourceLayoutElement>();
            _elements = new GpuResourceLayoutElement[source.Length];
            Array.Copy(source, _elements, source.Length);

            for (int i = 0; i < _elements.Length; i++)
            {
                if (!IsDynamicStructured(_elements[i])) continue;

                throw new ArgumentException(
                    $"'{_elements[i].Name}' is declared as {_elements[i].Kind} AND dynamic, which the native "
                    + "Direct3D 11 backend cannot honour. A dynamic offset is a per-draw byte rebase and the only "
                    + "bind that carries one is the constant-buffer bind, which takes a first constant and a "
                    + "count. A structured buffer binds through a view created ONCE over the whole buffer "
                    + "(decision C2's full-range RAW view), and neither *SetShaderResources nor "
                    + "*SetUnorderedAccessViews has a per-bind window to put the offset in, so the offset would "
                    + "be dropped in both directions: a full activation would write the pre-resolved view with "
                    + "nothing added, and the offsets-only path would skip the element entirely for not being a "
                    + "constant buffer. Every draw would read the window the view was created with while the "
                    + "caller believed it had moved. Declare the element as a uniform buffer, or build one "
                    + "resource set per window.",
                    nameof(description));
            }

            _slots = new D3D11RegisterSlot[_elements.Length];
            Counts = D3D11RegisterScheme.AssignWithinLayout(_elements, _slots);
        }

        /// <summary>How many registers of each file this layout consumes. Summed across the layouts before a set,
        /// in pipeline-array order, this is that set's base.</summary>
        internal D3D11RegisterCounts Counts { get; }

        /// <summary>The declared elements, in declaration order. Same order as a resource set's resources.</summary>
        internal ReadOnlySpan<GpuResourceLayoutElement> Elements => _elements;

        /// <summary>The layout-relative register for element <paramref name="index"/>.</summary>
        internal D3D11RegisterSlot SlotAt(int index) => _slots[index];

        /// <summary>Whether element <paramref name="index"/> is rebased per draw by a dynamic offset. Read when a
        /// set is bound with an offset, never at creation: the offset is a per-draw value and a set is not.</summary>
        internal bool IsDynamic(int index) => _elements[index].Dynamic;

        /// <summary>Element count, which is also the required resource count of any set built on this layout.</summary>
        internal int ElementCount => _elements.Length;

        /// <summary>True once disposed. Nothing native is released, because nothing native was created. The flag
        /// exists so a use-after-dispose is a stated error rather than a silently working call.</summary>
        internal bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;

        /// <summary>
        /// A PIPELINE'S LAYOUT ARRAY, in pipeline-array order, refused by name for anything this backend did not
        /// create. Shared by both pipeline types, because both flatten the same numbering across the same array
        /// and a second copy of this check would be a second message to keep true.
        /// <para>
        /// It lives HERE rather than on either pipeline for the reason every shared refusal in this package does:
        /// both pipeline types are Windows-only, so a guard inside one of their constructors is verified by the
        /// WARP leg and by nothing else, while this one is a plain <c>[Fact]</c> on any machine.
        /// </para>
        /// <para>
        /// A null or empty array answers <see cref="Array.Empty{T}"/> rather than throwing. A pipeline that
        /// declares no layouts binds no sets, and the mismatch a caller actually made in that case is "a set at
        /// slot k under a pipeline with fewer layouts than that", which the register scheme says in those terms at
        /// the flush.
        /// </para>
        /// </summary>
        internal static D3D11ResourceLayout[] RequireAll(IGpuResourceLayout[]? layouts, string pipelineKind)
        {
            if (layouts is null || layouts.Length == 0) return Array.Empty<D3D11ResourceLayout>();

            var result = new D3D11ResourceLayout[layouts.Length];
            for (int i = 0; i < layouts.Length; i++)
            {
                result[i] = layouts[i] as D3D11ResourceLayout
                    ?? throw new ArgumentException(
                        $"Resource layout {i} was not created by the native Direct3D 11 backend, so it carries no "
                        + $"register numbering this {pipelineKind} pipeline can flatten.", nameof(layouts));
            }

            return result;
        }

        /// <summary>
        /// THE SECOND BACKEND-DIVERGENT CREATION FAILURE, refused here for the reason decision U3's ring
        /// combination is refused at buffer creation: the backend cannot honour the combination, and every way of
        /// discovering that at run time is a wrong frame rather than an error.
        /// <para>
        /// Vacuous in the engine today (all six dynamic elements shipped are uniform buffers) and refused anyway,
        /// because nothing further down the path would ever say so: the full activation writes the pre-resolved
        /// view and the offsets-only path does not consider the element at all, so both halves of the flush
        /// silently agree to ignore the offset.
        /// </para>
        /// </summary>
        static bool IsDynamicStructured(in GpuResourceLayoutElement element)
            => element.Dynamic
                && element.Kind is GpuResourceKind.StructuredBufferReadOnly
                    or GpuResourceKind.StructuredBufferReadWrite;
    }
}
