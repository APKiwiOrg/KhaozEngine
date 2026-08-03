namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// HOW MUCH OF A RESOURCE-SET SLOT THE NEXT DRAW OWES, and the whole of decision R5's three-state tracking.
    /// A seam-level <c>SetGraphicsResourceSet</c> or <c>SetComputeResourceSet</c> issues no native call at all: it
    /// compares what it was handed against what the slot already holds and leaves one of these behind, and the
    /// next draw or dispatch pays exactly that much.
    /// <para>
    /// THE MIDDLE STATE IS THE ENTIRE POINT. The shadow pass rebinds ONE set thousands of times a frame and
    /// changes nothing but the dynamic offset, so re-activating it fully would re-push its textures and samplers
    /// every time, which is the 40x encode cost the 4.9.101 fix collapsed. A slot that differs only in its offset
    /// is worth exactly one <c>*SetConstantBuffers1</c> per visible stage, and the difference between "worth one
    /// call" and "worth six" cannot be expressed by a bool.
    /// </para>
    /// <para>
    /// THE ORDINALS ARE ORDERED SO A REPEATED MARK ESCALATES. Several binds may land on one slot between two
    /// draws, and the slot owes the MOST either of them asked for: an offsets-only rebind after a full one is
    /// still a full activation, because the full one has not happened yet. Taking the higher value is what makes
    /// rule 7 ("repeated dirty marks collapse to one flush") true without a separate rule for every ordering.
    /// </para>
    /// </summary>
    internal enum D3D11SlotDirty
    {
        /// <summary>Nothing changed since the slot was last activated, so a draw issues nothing for it. Also the
        /// state every slot returns to the moment it is flushed.</summary>
        Clean = 0,

        /// <summary>The same set, bound the same way, with a different dynamic offset. The flush pushes ONLY the
        /// dynamic constant buffers and skips the textures and the samplers entirely.</summary>
        DynamicOffsetsOnly = 1,

        /// <summary>A different set, or a change between the offset and no-offset forms of the bind. The flush
        /// activates every binding of the set, one array call per register file per visible stage.</summary>
        Full = 2,
    }
}
