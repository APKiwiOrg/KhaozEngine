using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE TWO <c>SetPipeline</c> MEMBERS, WHICH RECORD AND EMIT NOTHING. Work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577), section 6.3 clause 5.
    ///
    /// <para><b>NOTHING IS EMITTED HERE, AND THAT IS THE DEFERRED BEGIN RATHER THAN LAZINESS.</b> A pipeline's
    /// state block goes into a RENDER ENCODER, and under M-A1 the encoder does not exist until the pass actually
    /// begins, which happens at the first draw. So binding a pipeline before a framebuffer, or between two
    /// passes, is legal and common, and there is nothing to write to at the moment the seam calls this. The
    /// emission is the pre-draw flush's, with row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580),
    /// which is also where the depth-target guard is applied because the BOUND FRAMEBUFFER is what it asks
    /// about.</para>
    ///
    /// <para><b>WHAT DOES HAPPEN IS THE IDENTITY GUARD (M-R8)</b>, which is the incumbent's missing comparison:
    /// its <c>SetPipelineCore</c> sets the changed flag and clears the whole active-set array on every call,
    /// including a redundant one. See <see cref="MetalPipelineBinding"/> for the record and the two invalidation
    /// rules it keeps apart.</para>
    /// </summary>
    internal sealed partial class MetalCommandList
    {
        /// <inheritdoc/>
        /// <remarks>
        /// Records the pipeline, and on a genuine CHANGE tells the pass schedule which scissor gate is now in
        /// force.
        /// <para>
        /// A REDUNDANT BIND DOES NOTHING AT ALL (M-R8): no state re-emission is scheduled, no recorded resource
        /// slot is invalidated, and the scissor gate is not touched, because none of the three has changed.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">No pipeline.</exception>
        /// <exception cref="ArgumentException">A pipeline from another backend or another device.</exception>
        /// <exception cref="InvalidOperationException">This list is not recording.</exception>
        public void SetPipeline(IGpuPipeline p)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(p);

            // ARGUMENT VALIDATION FIRST, in the shape UpdateBuffer settled on: a caller passing another device's
            // pipeline has made the same mistake whether or not this list is recording.
            MetalGraphicsPipeline pipeline = MetalResourceOwnership.Require<MetalGraphicsPipeline>(
                p, _liveness, nameof(p));

            if (!_recording) throw NotRecording("SetPipeline");

            if (!_pipelines.BindGraphics(pipeline)) return;

            // ---- ROW 12'S CALL GOES HERE AT THE MERGE, AND IT IS AN OBLIGATION RATHER THAN A NICETY ----
            //
            //     Passes.SetScissorTestEnabled(pipeline.ScissorTestEnabled);
            //
            // MetalRenderPassSchedule.SetScissorTestEnabled is row 12's
            // (https://github.com/APKiwiOrg/KhaozEngine/issues/578), which is not on this branch yet, so the call
            // is documented here rather than written against a member that does not exist. It is NOT optional:
            // M-A6 keeps the incumbent's gate, where PreDrawCommand flushes the scissor only when the bound
            // pipeline has the seam's ScissorTestEnabled set. Metal has no scissor-test enable of its own, so
            // without the call the gate reads false forever and no scissor rectangle is ever emitted, which is
            // not a crash and not a validation error: it is a draw that rasterises the whole attachment where the
            // caller asked for a rectangle.
            //
            // It belongs on the CHANGE path rather than on every call, which is this guard's other half: a
            // redundant bind cannot change the gate.

            // ---- AND ROW 13'S INVALIDATION GOES HERE TOO (M-R9) ----
            //
            // A switch invalidates a recorded slot only where the incoming program's index table maps that slot's
            // elements to different indices than the outgoing one did, which is a reference compare through
            // MetalShaderIndexTable.SameIndicesAs now that row 10 has content-deduplicated the tables. The
            // per-slot records it invalidates are row 13's
            // (https://github.com/APKiwiOrg/KhaozEngine/issues/579) and do not exist on this branch either.
        }

        /// <inheritdoc/>
        /// <remarks>The compute sibling. Same guard, and nothing to tell the pass schedule: a compute pipeline
        /// has no scissor and no framebuffer.</remarks>
        /// <exception cref="ArgumentNullException">No pipeline.</exception>
        /// <exception cref="ArgumentException">A pipeline from another backend or another device.</exception>
        /// <exception cref="InvalidOperationException">This list is not recording.</exception>
        public void SetComputePipeline(IGpuComputePipeline p)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(p);

            MetalComputePipeline pipeline = MetalResourceOwnership.Require<MetalComputePipeline>(
                p, _liveness, nameof(p));

            if (!_recording) throw NotRecording("SetComputePipeline");

            _pipelines.BindCompute(pipeline);
        }

        /// <summary>
        /// THE BOUND-PIPELINE RECORD, exposed because rows 13 and 14 read it: the bind flush asks which pipeline
        /// is bound to reach its layouts and its table, and the pre-draw flush asks whether the state block has
        /// reached the current encoder.
        /// </summary>
        internal MetalPipelineBinding Pipelines => _pipelines;

        static InvalidOperationException NotRecording(string member)
            => new(member + " was called on a native Metal command list that is not recording. Call Begin first. "
                + "A bound pipeline is state of the recording rather than of the list, and it is forgotten at "
                + "every Begin, so binding one outside a recording would be binding it into nothing.");
    }
}
