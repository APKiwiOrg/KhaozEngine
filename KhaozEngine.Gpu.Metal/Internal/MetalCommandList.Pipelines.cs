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
        /// force and tells the graphics bind records which index table the next flush binds through.
        /// <para>
        /// A REDUNDANT BIND DOES NOTHING AT ALL (M-R8): no state re-emission is scheduled, no recorded resource
        /// slot is invalidated, the scissor gate is not touched and the index table is not re-adopted, because
        /// none of the four has changed.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">No pipeline.</exception>
        /// <exception cref="ArgumentException">A pipeline from another backend or another device.</exception>
        /// <exception cref="ObjectDisposedException">A disposed pipeline, or a disposed list.</exception>
        /// <exception cref="InvalidOperationException">This list is not recording.</exception>
        public void SetPipeline(IGpuPipeline p)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // ARGUMENT VALIDATION FIRST, in the shape UpdateBuffer settled on: a caller passing another device's
            // pipeline, or one they have already disposed, has made the same mistake whether or not this list is
            // recording. Require asks all three questions (backend, device, disposal) in one place.
            MetalGraphicsPipeline pipeline = MetalGraphicsPipeline.Require(p, _liveness, nameof(p));

            if (!_recording) throw NotRecording("SetPipeline");

            if (!_pipelines.BindGraphics(pipeline)) return;

            // ---- ROW 12'S OBLIGATION, ON THE CHANGE PATH ----
            //
            // M-A6 keeps the incumbent's gate, where the pre-draw flush emits the scissor only when the bound
            // pipeline has the seam's ScissorTestEnabled set. Metal has no scissor-test enable of its own, so
            // without this call the gate reads false forever and no scissor rectangle is ever emitted, which is
            // not a crash and not a validation error: it is a draw that rasterises the whole attachment where the
            // caller asked for a rectangle.
            _passes.SetScissorTestEnabled(pipeline.ScissorTestEnabled);

            // ---- AND ROW 13'S INVALIDATION (M-R9) ----
            //
            // A switch invalidates a recorded slot only where the incoming program's index table maps that slot's
            // elements to different indices than the outgoing one did. Row 10 content-deduplicated the tables, so
            // two programs that map every element identically SHARE one instance and this is a reference compare
            // that answers "nothing to invalidate" for the common case.
            _graphicsBinds.SetIndexTable(pipeline.Table);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The compute sibling. Same three-question guard, and nothing to tell the pass schedule, because a
        /// compute pipeline has no scissor and no framebuffer.
        /// <para>
        /// IT STILL ADOPTS THE INDEX TABLE, and that is the half a compute path loses most easily: the graphics
        /// site carries a loud comment about M-R9 and this one carries none, and <c>BindCompute</c>'s bool is
        /// discarded where the graphics arm's early-returns on it. So the identity guard is read EXPLICITLY here
        /// rather than relied on as a side effect, and the table is adopted only on a genuine change.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">No pipeline.</exception>
        /// <exception cref="ArgumentException">A pipeline from another backend or another device.</exception>
        /// <exception cref="ObjectDisposedException">A disposed pipeline, or a disposed list.</exception>
        /// <exception cref="InvalidOperationException">This list is not recording.</exception>
        public void SetComputePipeline(IGpuComputePipeline p)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            MetalComputePipeline pipeline = MetalComputePipeline.Require(p, _liveness, nameof(p));

            if (!_recording) throw NotRecording("SetComputePipeline");

            if (!_pipelines.BindCompute(pipeline)) return;

            _computeBinds.SetIndexTable(pipeline.Table);
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
