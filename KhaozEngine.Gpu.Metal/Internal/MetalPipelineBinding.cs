namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-R8: THE BOUND-PIPELINE RECORD, WITH THE IDENTITY GUARD THE INCUMBENT LACKS. Section 6.3 clause
    /// 5 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577).
    ///
    /// <para><b>WHAT THE INCUMBENT DOES, VERIFIED RATHER THAN ASSUMED.</b>
    /// <c>MTLCommandList.SetPipelineCore</c> stores the pipeline, clears the whole active-set array and sets
    /// <c>_graphicsPipelineChanged = true</c>, unconditionally, with no comparison against what is already
    /// bound. So a redundant bind of the pipeline already in place costs a five-call state re-emit at the next
    /// draw PLUS a full re-activation of every resource set. Section 2.1 records that one draft of this design
    /// asserted the guard already existed, and it does not.</para>
    ///
    /// <para><b>TWO THINGS ARE TRACKED HERE AND THEY ARE INVALIDATED BY DIFFERENT EVENTS, which is the whole
    /// shape.</b> WHICH pipeline is bound is RECORDER state: it survives an encoder boundary, because the
    /// recorder still intends the same pipeline for the next draw. WHETHER its state block has reached the
    /// current encoder is ENCODER state, and dies at every boundary, because Metal's bound pipeline state, cull
    /// mode, winding, fill mode, blend colour and depth-stencil state are all properties of the encoder (M-R4).
    /// Collapsing the two into one flag is exactly the incumbent's shape, and it is why its
    /// <c>EndCurrentRenderPass</c> has to remember to re-set the changed flag by hand.</para>
    ///
    /// <para><b>SO THE STAMP IS A <see cref="MetalEncoderMark"/> RATHER THAN A BOOL</b>, and the invalidation
    /// comes for free from the epoch the scope already counts. A record stamped in one encoder reads as invalid
    /// in the next, so nothing has to be reset at a boundary and nothing can be forgotten at one.</para>
    ///
    /// <para><b>WHAT A REDUNDANT BIND COSTS HERE IS ONE REFERENCE COMPARISON</b>, and it leaves both the stamp
    /// and every bind record alone, which is the half a test can see: the state block is not re-emitted and row
    /// 13's per-slot records are never invalidated, because <see cref="BindGraphics"/> answers false and the
    /// caller does nothing at all.</para>
    ///
    /// <para><b>THE EMISSION IS ROW 14's (https://github.com/APKiwiOrg/KhaozEngine/issues/580).</b> The
    /// pipeline-state block is emitted from the pre-draw flush, where the render encoder and the bound
    /// framebuffer both exist, and it asks <see cref="NeedsGraphicsStateBlock"/> and then
    /// <see cref="MarkGraphicsStateBlockEmitted"/>. This type is the decision and never the call.</para>
    /// </summary>
    internal sealed class MetalPipelineBinding
    {
        MetalGraphicsPipeline? _graphics;
        MetalComputePipeline? _compute;
        MetalEncoderMark _graphicsMark;
        MetalEncoderMark _computeMark;

        /// <summary>The bound graphics pipeline, or null before the first <see cref="BindGraphics"/> of this
        /// recording. Row 13 reads its table and its layout count, and row 14 its state block and its
        /// topology.</summary>
        internal MetalGraphicsPipeline? Graphics => _graphics;

        /// <summary>The bound compute pipeline, or null.</summary>
        internal MetalComputePipeline? Compute => _compute;

        /// <summary>
        /// Record <paramref name="pipeline"/> as the bound graphics pipeline.
        /// </summary>
        /// <returns>
        /// True when this CHANGED the binding, which is the caller's signal to do the work a switch owes: honour
        /// the seam's scissor gate for the incoming pipeline, and invalidate the recorded slots whose indices
        /// moved (M-R9). False for a redundant bind, which is M-R8 and where the caller does nothing.
        /// </returns>
        internal bool BindGraphics(MetalGraphicsPipeline pipeline)
        {
            if (ReferenceEquals(_graphics, pipeline)) return false;

            _graphics = pipeline;

            // The state block has NOT reached any encoder for this pipeline, so the stamp goes rather than being
            // re-marked. Clearing is what makes the next draw emit, and it is a different act from an encoder
            // boundary invalidating it, which happens on its own.
            _graphicsMark.Clear();
            return true;
        }

        /// <summary>The compute sibling of <see cref="BindGraphics"/>, with the same guard for the same
        /// reason.</summary>
        internal bool BindCompute(MetalComputePipeline pipeline)
        {
            if (ReferenceEquals(_compute, pipeline)) return false;

            _compute = pipeline;
            _computeMark.Clear();
            return true;
        }

        /// <summary>
        /// Whether the pre-draw flush has to emit the graphics pipeline-state block into the encoder that is open
        /// at <paramref name="epoch"/>. True when the pipeline changed since the last emission, and true again
        /// after any encoder boundary, because the block is encoder state.
        /// </summary>
        internal bool NeedsGraphicsStateBlock(ulong epoch) => !_graphicsMark.IsValidIn(epoch);

        /// <summary>Record that the block has been emitted into the encoder open at
        /// <paramref name="epoch"/>.</summary>
        internal void MarkGraphicsStateBlockEmitted(ulong epoch) => _graphicsMark.Mark(epoch);

        /// <summary>Whether the pre-dispatch flush has to emit <c>-setComputePipelineState:</c> into the encoder
        /// open at <paramref name="epoch"/>.</summary>
        internal bool NeedsComputeStateBlock(ulong epoch) => !_computeMark.IsValidIn(epoch);

        /// <summary>Record that the compute pipeline state has been set on the encoder open at
        /// <paramref name="epoch"/>.</summary>
        internal void MarkComputeStateBlockEmitted(ulong epoch) => _computeMark.Mark(epoch);

        /// <summary>
        /// Forget both bindings. Called from <c>MetalCommandList.Begin</c>, which is the one place a recording's
        /// state is reset, and by nothing else: a fresh command buffer has no encoder and therefore no pipeline,
        /// and a record carried over from a discarded recording would let the first draw of the next one skip an
        /// emission that never happened.
        /// </summary>
        internal void Reset()
        {
            _graphics = null;
            _compute = null;
            _graphicsMark.Clear();
            _computeMark.Clear();
        }
    }
}
