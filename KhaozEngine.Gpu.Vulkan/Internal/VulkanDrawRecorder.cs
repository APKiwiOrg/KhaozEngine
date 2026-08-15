using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE PRE-COMMAND ORDERING, IN ONE PLACE: everything that has to happen before a <c>vkCmdDraw</c>,
    /// a <c>vkCmdDrawIndexed</c> or a <c>vkCmdDispatch</c>, in the one order that is correct, plus the vertex and
    /// index bind state and the dependent-dispatch hazard set that feed it. Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>IT IS ITS OWN TYPE BECAUSE THE ORDER IS THE THING THAT CAN BE WRONG</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/556). Five draw members would otherwise repeat the same
    /// four-step sequence five times, and a step dropped from one of them renders plausibly wrong rather than
    /// throwing. Written once here, it is one device-free test per step rather than five.</para>
    ///
    /// <para><b>THE DRAW ORDER, AND WHY EACH STEP IS WHERE IT IS.</b>
    /// <list type="number">
    /// <item><description><b>The bound sets' images are transitioned FIRST, OUTSIDE any render pass instance.</b>
    /// This is the compute rule 1 barrier (V-C1): a storage texture a dispatch left in <c>GENERAL</c> goes to
    /// <c>SHADER_READ_ONLY_OPTIMAL</c> where the sampled bind is assembled, and so does a render target the
    /// previous pass wrote and this one samples. It has to be outside, because a barrier inside an open render
    /// pass instance is a different and much narrower call than the one section 10.3's table describes, and the
    /// incumbent drains its own queued restores before <c>EnsureRenderPassActive</c> for the same reason. So a
    /// pass that is ALREADY OPEN is ended here, and only when a transition is really owed: the FIRST draw of a
    /// pass finds nothing open and the invariant costs it nothing, and a LATER draw whose newly bound set needs a
    /// layout change would otherwise emit its barrier inside the instance, where <c>oldLayout</c> must equal
    /// <c>newLayout</c> and this one does not. That is not a hypothetical shape: the ocean chain reaches it, where
    /// a mip generation leaves levels in the transfer layouts, a sky pass opens the instance on the shared colour
    /// and depth framebuffer, and the water pass that follows binds the SAME framebuffer (so no framebuffer change
    /// ends anything) and then binds the ocean map. The pass is ended through
    /// <see cref="VulkanRenderingSchedule.EndRendering"/> rather than by hand, so the clear-only flush travels with
    /// it, and the begin that step 2 then makes carries <c>loadOp = LOAD</c> because the clears were consumed by
    /// the begin this ended. Reopening costs nothing beyond the pair: descriptor binds, geometry binds and dynamic
    /// state are COMMAND BUFFER state rather than render pass state, so they survive the boundary and the reopened
    /// pass re-emits none of them.</description></item>
    /// <item><description><b>Then <c>PrepareDraw</c></b>, which opens the instance if it is not open, folding
    /// every pending clear into a <c>loadOp</c>, transitions the ATTACHMENTS inside that begin, and emits the
    /// viewport and the scissor if a framebuffer change marked them (V-A2, V-A5).</description></item>
    /// <item><description><b>Then the vertex and index binds</b>, one <c>vkCmdBindVertexBuffers</c> per contiguous
    /// run of dirty slots.</description></item>
    /// <item><description><b>Then the descriptor flush and the command</b>, as one monomorphized pair inside
    /// <see cref="VulkanDrawBatch"/>, so nothing can be recorded between a bind and the draw that reads
    /// it.</description></item>
    /// </list></para>
    ///
    /// <para><b>THE DISPATCH ORDER IS THE SAME SHAPE WITH TWO DIFFERENCES.</b> It ENDS any pending rendering
    /// first (V-A4), because a dispatch is illegal inside a render pass instance, and between the transitions and
    /// the command it emits the read-after-write barrier when
    /// <see cref="VulkanComputeHazards"/> says an earlier dispatch wrote something this one binds (V-C2). A
    /// compute set's STORAGE images go to <c>GENERAL</c> and its sampled ones to
    /// <c>SHADER_READ_ONLY_OPTIMAL</c>, which is the same walk with the layout taken off the binding rather than
    /// off the texture.</para>
    ///
    /// <para><b>THE TRANSITION WALK COVERS EVERY SLOT THE BOUND LAYOUT DECLARES AND NOT ONLY THE DIRTY ONES.</b> A
    /// set bound before a dispatch is still bound at the draw after it, and a dirty-only walk would skip the rule 1
    /// transition on exactly the sequence rule 1 names. It costs a scan of a handful of images per command with NO
    /// native call in the common case, because <see cref="VulkanLayoutTracker"/> emits nothing for an image already
    /// in the layout it is being asked for, which every plain sampled texture is. The graphics arm scans TWICE,
    /// once to ask whether the pass has to close and once to transition, and that is the cheaper half of the trade:
    /// a second scan makes no call at all, where closing every pass unconditionally would cost a
    /// <c>vkCmdEndRendering</c> and a <c>vkCmdBeginRendering</c> per draw. That is what keeps V-T2's gated
    /// invariant true: no pipeline barrier, and no pass boundary either, between two draws that touch no new
    /// texture.</para>
    ///
    /// <para><b>AND DECLARED IS WHERE IT STOPS, WHICH IS A DIFFERENT BOUND FROM DIRTY AND NOT A RETREAT TOWARDS IT</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/626). A switch to a pipeline declaring FEWER sets leaves the
    /// dropped slots recording their sets deliberately, so the trip back rebinds them
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/625), and those slots were walked here as well: their images
    /// were asked for a layout no shader on the bound pipeline could read them in. Where the image was already
    /// resting there the tracker emitted nothing, which is why the shipped post chain showed no extra barrier and
    /// why this was never a wrong picture. Where it was NOT, the draw paid a barrier to move an image out of the
    /// layout its real consumer wants, and the consumer paid a second one to move it back. The sharp shape is a
    /// dropped set naming an image the pass BEGIN itself moves, a <c>RenderTarget | Sampled</c> target: the walk is
    /// owed a transition the instant the pass reopens, so the draw ends the pass, transitions, reopens, and the
    /// begin puts the attachment straight back, at EVERY draw of that pass rather than once. Both walks therefore
    /// stop at <see cref="VulkanBindRecords.BindableSlotLimit"/>, the same limit the flush stops at, and a slot past
    /// it is walked again the moment a layout declares it.</para>
    ///
    /// <para><b>NOTHING HERE IS SYNCHRONISED</b>, on the same grounds as the list that owns it.</para>
    /// </summary>
    internal sealed class VulkanDrawRecorder
    {
        readonly IVulkanDrawEmitter _emitter;
        readonly VulkanLayoutTracker? _layouts;
        readonly VulkanGeometryBinds _geometry = new();
        readonly VulkanComputeHazards _hazards = new();

        /// <param name="emitter">The record-time calls a draw and a dispatch are. Real on a device, a recording
        /// fake or the counting emitter in the device-free tests.</param>
        /// <param name="layouts">The owning list's layout tracker (V-F7), which the bound-image transitions go
        /// through. Null only on a recorder with no barrier seam, which is only one a test constructed, and then
        /// there is nothing to transition.</param>
        internal VulkanDrawRecorder(IVulkanDrawEmitter emitter, VulkanLayoutTracker? layouts = null)
        {
            ArgumentNullException.ThrowIfNull(emitter);

            _emitter = emitter;
            _layouts = layouts;
        }

        /// <summary>The vertex and index bind schedule, exposed because the list's two seam members record into it
        /// and because the device-free tests drive the run cutting through it.</summary>
        internal VulkanGeometryBinds Geometry => _geometry;

        /// <summary>The dependent-dispatch hazard set (V-C2), exposed for the tests that assert a barrier is
        /// emitted for a chain and not for two independent dispatches.</summary>
        internal VulkanComputeHazards Hazards => _hazards;

        /// <summary>
        /// FORGET EVERY BIND AND EVERY WRITE, which is what a fresh <c>VkCommandBuffer</c> holds. Called from
        /// <c>VulkanCommandList.Begin</c> alongside the other recorder resets, for the reason they are all called
        /// there: the state belonged to a recording that was discarded, and keeping it would let the next one skip
        /// a bind as redundant against a command buffer nobody submitted.
        /// </summary>
        internal void Reset()
        {
            _geometry.Reset();
            _hazards.Clear();
        }

        /// <summary><c>vkCmdDraw</c>, with the four steps above in front of it.</summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="rendering">The list's rendering schedule, whose deferred begin this opens.</param>
        /// <param name="binds">The GRAPHICS bind records.</param>
        /// <param name="call">The draw's four counts.</param>
        internal void Draw(ulong commandBuffer, VulkanRenderingSchedule rendering, VulkanBindRecords binds,
            in VulkanDrawCall call)
        {
            PrepareGraphics(commandBuffer, rendering, binds);
            _emitter.Draw(commandBuffer, binds, in call);
        }

        /// <summary><c>vkCmdDrawIndexed</c>, with the identical four steps.</summary>
        internal void DrawIndexed(ulong commandBuffer, VulkanRenderingSchedule rendering, VulkanBindRecords binds,
            in VulkanIndexedDrawCall call)
        {
            PrepareGraphics(commandBuffer, rendering, binds);
            _emitter.DrawIndexed(commandBuffer, binds, in call);
        }

        /// <summary>
        /// <c>vkCmdDispatch</c>, with the pass ended, the compute set's images in the layouts their bindings need,
        /// and the read-after-write barrier when this dispatch depends on an earlier one.
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="rendering">The list's rendering schedule, whose pending instance this ends (V-A4).</param>
        /// <param name="binds">The COMPUTE bind records.</param>
        /// <param name="groupCountX">Workgroups in X.</param>
        /// <param name="groupCountY">Workgroups in Y.</param>
        /// <param name="groupCountZ">Workgroups in Z.</param>
        internal void Dispatch(ulong commandBuffer, VulkanRenderingSchedule rendering, VulkanBindRecords binds,
            uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            ArgumentNullException.ThrowIfNull(rendering);
            ArgumentNullException.ThrowIfNull(binds);

            // ILLEGAL INSIDE A RENDER PASS INSTANCE (V-A4), through the ONE helper every such command calls, and
            // before the transitions for the reason the draw path puts them before the begin.
            rendering.EndRendering(commandBuffer);

            TransitionBoundImages(commandBuffer, binds);

            // THE DEPENDENCY, IF THERE IS ONE. Asked before the writes of THIS dispatch are recorded, because a
            // dispatch does not depend on itself, and cleared by the barrier because a global memory barrier
            // orders every earlier write at once.
            if (_hazards.NeedsBarrier(binds))
            {
                _emitter.DependencyBarrier(commandBuffer);
                _hazards.Clear();
            }

            _emitter.Dispatch(commandBuffer, binds, groupCountX, groupCountY, groupCountZ);
            _hazards.NoteWrites(binds);
        }

        // STEPS 1 TO 3 OF THE DRAW ORDER. Step 4 is the emitter's, because the descriptor flush and the command
        // are one monomorphized pair.
        void PrepareGraphics(ulong commandBuffer, VulkanRenderingSchedule rendering, VulkanBindRecords binds)
        {
            ArgumentNullException.ThrowIfNull(rendering);
            ArgumentNullException.ThrowIfNull(binds);

            // THE PASS IS CLOSED FIRST, AND ONLY WHEN A TRANSITION IS REALLY OWED. See the class note's step 1:
            // an open instance has to be ended before the walk emits, and asking first is what keeps the common
            // draw free of an end and a begin it does not need.
            if (rendering.IsRendering && NeedsTransition(binds)) rendering.EndRendering(commandBuffer);

            TransitionBoundImages(commandBuffer, binds);
            rendering.PrepareDraw(commandBuffer);
            _geometry.Flush(_emitter, commandBuffer);
        }

        // EVERY IMAGE EVERY SLOT THE BOUND LAYOUT DECLARES BINDS, INTO THE LAYOUT ITS BINDING NEEDS. One tracker
        // call per image, which emits a barrier only when the image is not already there, so the common frame pays
        // a scan and no native call at all. See the class note for why the walk is over declared slots rather than
        // dirty ones, and for what stopping at the declared count is really about.
        void TransitionBoundImages(ulong commandBuffer, VulkanBindRecords binds)
        {
            if (_layouts is null) return;

            for (uint slot = 0; slot < (uint)binds.BindableSlotLimit(); slot++)
            {
                VulkanBoundSet bound = binds.BoundAt(slot);
                if (!bound.IsBound) continue;

                foreach (VulkanBoundImage image in bound.BoundImages)
                {
                    _layouts.TransitionTo(commandBuffer, image.Image, image.Layout);
                }
            }
        }

        // THE SAME WALK, ASKED RATHER THAN ACTED ON: would the walk above emit anything at all? It is a second
        // pass over the same handful of images and it makes no call, which is the trade this shape takes: a scan
        // the common draw pays twice, against an end and a begin the common draw would pay once. The scan costs
        // nothing native and the pair costs two commands and a loadOp reset.
        //
        // IT IS BOUNDED IDENTICALLY, AND THAT IS LOAD-BEARING RATHER THAN TIDY. This answer is what decides whether
        // the pass is ended, so a walk that reached further than the one that emits would end passes for nothing,
        // and one that reached less far would leave a barrier to be recorded INSIDE an open render pass instance,
        // which is the invalid call step 1 exists to prevent. The two bounds are the same expression on purpose.
        bool NeedsTransition(VulkanBindRecords binds)
        {
            if (_layouts is null) return false;

            for (uint slot = 0; slot < (uint)binds.BindableSlotLimit(); slot++)
            {
                VulkanBoundSet bound = binds.BoundAt(slot);
                if (!bound.IsBound) continue;

                foreach (VulkanBoundImage image in bound.BoundImages)
                {
                    if (_layouts.WouldTransition(image.Image, image.Layout)) return true;
                }
            }

            return false;
        }
    }
}
