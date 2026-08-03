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
    }
}
