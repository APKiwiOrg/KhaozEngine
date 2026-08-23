using System;
using System.Collections.Generic;
using System.Globalization;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE LIST-LOCAL IMAGE LAYOUT TRACKER, DECISIONS V-F6 TO V-F8 (section 10.3), and the ruling that decides
    /// this backend's concurrency model. One of these belongs to ONE <see cref="VulkanCommandList"/> and holds
    /// nothing shared with any other.
    ///
    /// <list type="number">
    /// <item><description><b>Every texture is at REST when a list starts.</b> The resting layout is assigned at
    /// texture CREATION from the usage bits (<see cref="VulkanViewPolicy"/>, row 9) and the device's setup command
    /// buffer puts the image there before anything can record against it, so a list that has transitioned nothing
    /// knows every image's layout without asking anybody.</description></item>
    /// <item><description><b>A transition is tracked here and nowhere else.</b> The entry carries the range, the
    /// layout the list moved it to, and the resting layout to put it back to.</description></item>
    /// <item><description><b><c>End</c> restores every touched entry</b> through <see cref="RestoreResting"/>,
    /// which is what makes two lists composable in any submit order.</description></item>
    /// </list>
    ///
    /// <para><b>WHY LIST-LOCAL RATHER THAN ON THE TEXTURE (V-F7, section 2.5).</b> The incumbent tracked Vulkan
    /// image layouts as recording-time mutable state ON the texture: <c>VkTexture</c>'s layout array is read to
    /// decide whether a barrier is needed and written to record what the barrier did. Two recordings touching the
    /// same texture read and write the same array, and the loser records either a redundant barrier, which is
    /// harmless, or NO BARRIER FOR A TRANSITION IT NEEDED, which is a corruption no golden on a software
    /// rasterizer will show. One draft answered that by making one open recording per device this backend's own
    /// contract. This design ELIMINATES the hazard instead: nothing shared is read or written during recording, so
    /// two lists cannot disagree, and lists compose in any submit order, which is what the seam already
    /// promises.</para>
    ///
    /// <para><b>AND THE THING THAT IS LOST UNDER THAT RULING, RECORDED SO IT IS NOT MISREAD.</b>
    /// <c>OpenListTrackingGpuDevice</c> passes TRIVIALLY on the native Vulkan leg, exactly as it does on the
    /// native Direct3D 11 leg. The rejected model would have made that test real evidence here and this one does
    /// not. A future reader seeing it green on this leg must not read it as evidence about this backend: it stays
    /// the PORTABLE guard, and naming that here is the whole defence against V-R4's decay mode.</para>
    ///
    /// <para><b>THE COST IS BOUNDED BY TOUCHED TEXTURES AND INDEPENDENT OF DRAW COUNT (MV5).</b> Restoring to rest
    /// costs a handful of extra barriers per list at pass boundaries. A texture written in list 1 and sampled in
    /// list 2 pays a restore in list 1 and a re-transition in list 2, which is redundant and harmless. A
    /// <c>Storage | Sampled</c> texture rests at <c>SHADER_READ_ONLY_OPTIMAL</c>, so a dispatch writing it
    /// transitions to <c>GENERAL</c> and restores, which is the compute rule 1 case falling out correctly.
    /// A REPEATED TRANSITION TO THE LAYOUT AN IMAGE IS ALREADY IN EMITS NOTHING, which is what keeps the count per
    /// PASS rather than per draw.</para>
    ///
    /// <para><b>BARRIERS ARE BATCHED INTO ONE CALL PER BOUNDARY.</b> A begin transitioning four attachments is one
    /// <c>vkCmdPipelineBarrier2</c> carrying four barriers, not four calls, and so is the restore at <c>End</c>.
    /// Both numbers are countable off <see cref="VulkanCountingCmdSink"/>, reached by handing this tracker a
    /// <see cref="VulkanCountingBarrierRecorder"/> in place of the real one, because a budget that froze only the
    /// call count would pass a recorder that put a barrier per draw into one batch.</para>
    ///
    /// <para><b>THE RANGE RULE, IN FULL, BECAUSE PER-SUBRESOURCE TRACKING HAS TO MEET A WHOLE-CHAIN BIND.</b>
    /// Tracking is per subresource RANGE (V-F6), and the standard streaming path produces both shapes in one
    /// recording: a copy seeds mip 0, mip generation walks the chain a level at a time, and then a sampled bind
    /// names the WHOLE chain, because the seam has no texture-view type and the sampled view is full-chain by
    /// construction (V-M11). Four cases, and only one of them is refused.
    /// <list type="bullet">
    /// <item><description><b>The same range.</b> The entry is updated in place.</description></item>
    /// <item><description><b>A range that CONTAINS tracked narrower ones.</b> Answered: one barrier per piece,
    /// each FROM ITS OWN layout, so levels that disagree are not an ambiguity. The pieces then collapse into ONE
    /// entry for the wider range, which is what keeps the restore at <c>End</c> one barrier instead of one per
    /// level and makes the second whole-chain bind free. Subresources of the request that no piece covers are
    /// still at rest, and naming that leftover as a range would mean subtracting rectangles, so the case is
    /// answered when the leftover needs no barrier (the target IS the resting layout, which every whole-chain
    /// sampled bind satisfies because Sampled wins the resting ladder outright) and refused
    /// otherwise.</description></item>
    /// <item><description><b>A range CONTAINED IN one tracked wider entry.</b> Answered over the ENTRY's range
    /// rather than the request's: one barrier from the entry's own layout, covering every subresource the entry
    /// holds, and the entry stays one entry at the new layout. It is the previous case arriving a second time,
    /// because the collapse that case performs is what makes the entry wider than the next request. Nothing
    /// outside the entry is touched, so the list transitions only subresources it already owns, and no leftover
    /// has to be named as a range.</description></item>
    /// <item><description><b>A range that PARTIALLY overlaps a tracked one.</b> REFUSED, and this is the case
    /// worth naming: transitioning it would move part of the tracked range, so that entry would claim a layout
    /// the rest of it no longer has, and the restore at <c>End</c> would emit a barrier whose old layout is a
    /// lie.</description></item>
    /// </list>
    /// <see cref="LayoutOf"/> is the one place the CONTAINING shape is refused too, and for a different reason: a
    /// transition of that range is well defined because it MAKES the range uniform, and a query of it is not,
    /// because the pieces may disagree and no single layout is the answer.</para>
    ///
    /// <para><b>NOTHING HERE IS SYNCHRONISED</b>, on the same grounds as the list that owns it: one list records on
    /// one thread at a time and this tracker is that list's alone. That is the property this whole design exists
    /// to make true.</para>
    /// </summary>
    internal sealed class VulkanLayoutTracker
    {
        readonly IVulkanBarrierRecorder _recorder;
        readonly List<Entry> _touched = new();

        // THE MAP A BATCH IS BUILDING, which becomes _touched only once the call it describes has been made.
        // Reused rather than allocated per batch, like _batch below, and live only between the first staged
        // barrier of a batch and its emit.
        readonly List<Entry> _staged = new();
        bool _staging;

        // GROWN TO THE WIDEST BOUNDARY EVER EMITTED rather than allocated per batch, so a frame that transitions
        // the same four attachments every pass allocates nothing after the first one.
        ImageMemoryBarrier2[] _batch = new ImageMemoryBarrier2[4];

        /// <param name="recorder">Where a batch of barriers goes. Real on a device, a recording fake in the
        /// device-free tests.</param>
        internal VulkanLayoutTracker(IVulkanBarrierRecorder recorder)
        {
            ArgumentNullException.ThrowIfNull(recorder);

            _recorder = recorder;
        }

        /// <summary>How many subresource ranges this recording has TOUCHED. Not how many are away from rest: a
        /// range the recording moved and then moved back keeps its entry, because the entry is what says the range
        /// was touched at all, and only <see cref="RestoreResting"/> and <see cref="Reset"/> empty the map. The
        /// number MV5's bound is stated in: barriers per frame proportional to passes times touched textures.
        /// </summary>
        internal int TouchedCount => _touched.Count;

        /// <summary>
        /// The layout <paramref name="image"/> is in as far as this recording is concerned, which is its RESTING
        /// layout until this list transitions it. Never <c>UNDEFINED</c>: a list assumes every texture is at rest
        /// when it starts, so there is no untracked state for it to be in.
        /// <para>
        /// A RANGE THAT CONTAINS NARROWER TRACKED ONES HAS NO SINGLE LAYOUT TO REPORT, and this is the one place
        /// the wider shape is refused rather than answered. Transitioning such a range is well defined because it
        /// MAKES the range uniform, one barrier per piece. Reporting one layout for it would have to pick a level
        /// and call it the answer, and a caller that skipped a barrier on that answer is the corruption this whole
        /// model exists to make impossible.
        /// </para>
        /// <para>
        /// A RANGE CONTAINED IN A WIDER TRACKED ONE IS THE OPPOSITE CASE AND IS ANSWERED, with that entry's
        /// layout. An entry is uniform, so its layout is true of every subresource in it, and therefore of any
        /// part of it somebody asks about.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The range contains tracked narrower ranges, or partially
        /// overlaps one.</exception>
        internal ImageLayout LayoutOf(in VulkanTrackedImage image)
        {
            RequireImage(image);

            int index = Classify(image, out int covered);
            if (covered > 0) throw NoSingleLayout(image, covered);

            return index < 0 ? image.RestingLayout : Map[index].Current;
        }

        /// <summary>
        /// WHETHER <see cref="TransitionTo"/> WOULD EMIT ANYTHING, answered without emitting or recording
        /// anything at all. The same three cases <see cref="Stage"/> decides, read rather than acted on.
        /// <para>
        /// IT EXISTS FOR ONE CALLER AND ONE INVARIANT. A barrier may not be recorded inside an open render pass
        /// instance, and the draw path's bound-image walk runs while one may already be open, so
        /// <see cref="VulkanDrawRecorder"/> has to END the pass before the walk emits. Ending it unconditionally
        /// would cost an end and a begin on every draw of every pass, which is exactly the per-draw cost V-T2's
        /// gated invariant is about, so the pass is ended only when a transition is really owed and this is what
        /// answers that.
        /// </para>
        /// </summary>
        /// <param name="image">The image and the range, exactly as the transition would name them.</param>
        /// <param name="target">The layout the next command needs it in.</param>
        /// <exception cref="InvalidOperationException">The range partially overlaps a tracked one, which is the
        /// refusal <see cref="Classify"/> makes for a transition of the same shape.</exception>
        internal bool WouldTransition(in VulkanTrackedImage image, ImageLayout target)
        {
            RequireImage(image);

            int index = Classify(image, out int covered);

            // A WIDER RANGE MOVES WHATEVER PIECES ARE NOT ALREADY THERE. The untracked-remainder refusal is
            // deliberately NOT reproduced here: it is raised by the transition itself, and raising it from a
            // question would refuse a draw before the call that owns the rule had been reached.
            if (covered > 0)
            {
                List<Entry> map = Map;
                for (int i = 0; i < map.Count; i++)
                {
                    Entry entry = map[i];
                    if (entry.Image != image.Image || !image.Range.Contains(entry.Subrange)) continue;
                    if (entry.Current != target) return true;
                }

                return false;
            }

            return (index < 0 ? image.RestingLayout : Map[index].Current) != target;
        }

        /// <summary>
        /// Move <paramref name="image"/> into <paramref name="target"/>, emitting ONE barrier, or emitting nothing
        /// at all when it is already there.
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="image">The image and the range, with the resting layout it will be put back to.</param>
        /// <param name="target">The layout the next command needs it in.</param>
        internal void TransitionTo(ulong commandBuffer, in VulkanTrackedImage image, ImageLayout target)
        {
            RequireImage(image);

            try
            {
                int count = Stage(image, target, 0);
                if (count == 0) return;

                _recorder.Emit(commandBuffer, _batch.AsSpan(0, count));
                Commit();
            }
            finally
            {
                // COMMITTED OR ABANDONED, THE SHADOW IS DEAD EITHER WAY, and dropping it on the throwing path is
                // the whole point: a batch that did not reach the driver changed nothing.
                _staging = false;
            }
        }

        /// <summary>
        /// THE BEGIN-RENDERING TRANSITION (section 10.3's table, first row), as ONE batched barrier: every colour
        /// attachment into <c>COLOR_ATTACHMENT_OPTIMAL</c> and the depth attachment into
        /// <c>DEPTH_STENCIL_ATTACHMENT_OPTIMAL</c>.
        /// <para>
        /// CALLED IMMEDIATELY BEFORE <c>vkCmdBeginRendering</c> AND NEVER INSIDE THE INSTANCE, which is
        /// <see cref="VulkanRenderingSchedule"/>'s obligation rather than this type's: a barrier recorded inside an
        /// open render pass instance is a different and much narrower call than the one that table describes.
        /// </para>
        /// <para>
        /// NOTHING IN THIS TYPE ENFORCES THAT, AND SAYING SO IS THE POINT. Every caller is obliged to have closed
        /// any open instance before it asks for a barrier, and there are two of them: this one, reached from the
        /// begin itself where no instance can be open yet, and <see cref="VulkanDrawRecorder"/>'s bound-image walk,
        /// which runs where one may well be. That second caller ends the pass first, and only when
        /// <see cref="WouldTransition"/> says a barrier is really owed, so the common draw pays neither the end nor
        /// the begin. The obligation used to be stated here as though the schedule discharged it for everybody, and
        /// it did not: the first draw of a pass opened the instance and every later draw walked its bound sets with
        /// that instance still open.
        /// </para>
        /// <para>
        /// AN ATTACHMENT ALREADY IN ITS ATTACHMENT LAYOUT COSTS NOTHING, which is the common case for a plain
        /// render target: it rests in <c>COLOR_ATTACHMENT_OPTIMAL</c>, so a pass that renders into it and nothing
        /// else emits no barrier at either end. What does pay is the post-chain shape, a target that is also
        /// <c>Sampled</c> and therefore rests in <c>SHADER_READ_ONLY_OPTIMAL</c>, and paying there is the point.
        /// </para>
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="framebuffer">The bound framebuffer, whose attachments carry their image and their resting
        /// layout as plain data.</param>
        internal void TransitionAttachments(ulong commandBuffer, in VulkanBoundFramebuffer framebuffer)
        {
            ReadOnlySpan<VulkanAttachment> colour = framebuffer.ColourAttachments;
            EnsureCapacity(colour.Length + 1);

            try
            {
                int count = 0;
                for (int i = 0; i < colour.Length; i++)
                {
                    var attachment = VulkanTrackedImage.ForAttachment(in colour[i]);
                    count += Stage(attachment, attachment.AttachmentLayout, count);
                }

                if (framebuffer.HasDepth)
                {
                    VulkanAttachment depth = framebuffer.Depth;
                    var attachment = VulkanTrackedImage.ForAttachment(in depth);
                    count += Stage(attachment, attachment.AttachmentLayout, count);
                }

                if (count == 0) return;

                _recorder.Emit(commandBuffer, _batch.AsSpan(0, count));
                Commit();
            }
            finally
            {
                _staging = false;
            }
        }

        /// <summary>
        /// EVERY TOUCHED RANGE BACK TO ITS RESTING LAYOUT (V-F7), as ONE batched barrier, and then the map is
        /// empty. Called from <c>VulkanCommandList.End</c> after the render pass instance has closed and before
        /// <c>vkEndCommandBuffer</c>: a barrier recorded after the native end is a call against a sealed buffer.
        /// <para>
        /// A TOUCHED RANGE THAT IS ALREADY BACK AT REST COSTS NOTHING. A list that transitioned a texture away and
        /// back again inside the recording owes no restore, and emitting one would be a barrier bought for a
        /// transition that is not happening.
        /// </para>
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        internal void RestoreResting(ulong commandBuffer)
        {
            if (_touched.Count == 0) return;

            EnsureCapacity(_touched.Count);

            int count = 0;
            for (int i = 0; i < _touched.Count; i++)
            {
                Entry entry = _touched[i];
                if (entry.Current == entry.Resting) continue;

                _batch[count++] = Transition(entry.Image, entry.Range, entry.Current, entry.Resting);
            }

            if (count > 0) _recorder.Emit(commandBuffer, _batch.AsSpan(0, count));

            // AFTER THE CALL, for the reason Stage defers its own commit: a recorder that threw restored nothing,
            // and a map emptied anyway would leave the next reader believing every image is back at rest.
            _touched.Clear();
        }

        /// <summary>
        /// FORGET EVERY TRANSITION, which is what a fresh <c>VkCommandBuffer</c> has recorded. Called from
        /// <c>VulkanCommandList.Begin</c>, between the native begin and the recording flag, for the reason
        /// <see cref="VulkanBindRecords.Reset"/> and <see cref="VulkanRenderingSchedule.Reset"/> are called there.
        /// <para>
        /// DROPPING THE MAP IS CORRECT RATHER THAN LOSSY, and the argument is V-F7's whole point: the transitions
        /// belonged to a recording that was discarded, so the images are still at rest, which is what the next
        /// recording will assume. A retained map would let the next recording skip a barrier as redundant against
        /// a transition that lives on a command buffer nobody submitted.
        /// </para>
        /// </summary>
        internal void Reset()
        {
            _touched.Clear();
            _staged.Clear();
            _staging = false;
        }

        // THE ONE BARRIER, WITH THE ONE EXCEPTION THIS TRACKER MAKES (V-F8's second permitted UNDEFINED site).
        //
        // PRESENT_SRC_KHR IS A DESTINATION AND NEVER A SOURCE, which VulkanImageTransition says in as many words:
        // an image handed to vkQueuePresentKHR is next seen through an ACQUIRE, and the specification does not
        // preserve its contents across one. So a transition out of it discards rather than reading back pixels
        // the presentation engine already owns, and that discard is also what covers a FRESHLY CREATED
        // generation, whose images really are in UNDEFINED and which no first-use transition has been recorded
        // for: naming UNDEFINED as the old layout is valid whatever the image is really in.
        //
        // AND IT IS WHY PRESENT_SRC IS A RESTING LAYOUT AT ALL
        // (https://github.com/APKiwiOrg/KhaozEngine/issues/557). The alternative was for the present boundary to
        // record and submit the transition itself, which needs a command pool on the boundary, a second
        // vkQueueSubmit per frame, and a rearrangement of which submit signals the render-finished semaphore -
        // all on the one path with zero CI coverage (MV9). Resting there instead puts the present transition
        // inside the submit that signals that semaphore, which is a fact about ROUTING rather than a coincidence
        // of arrival order: VulkanPresentBoundary.TakeFrameSemaphores hands the pair only to a submit whose
        // recording bound the swapchain framebuffer, which is the same submit whose End records this restore.
        //
        // THE LIMITATION, NAMED: a SECOND list in one frame that binds the swapchain framebuffer after another
        // one already ended discards what the first drew, because it finds the image at rest in PRESENT_SRC and
        // a transition out of that discards. Every shipped renderer draws the backbuffer from one list, and the
        // seam's portable contract is one open recording per device anyway, so no shipped shape reaches it. Filed
        // rather than hidden: https://github.com/APKiwiOrg/KhaozEngine/issues/562.
        static ImageMemoryBarrier2 Transition(ulong image, in ImageSubresourceRange range, ImageLayout current,
            ImageLayout target)
            => current == ImageLayout.PresentSrcKhr
                ? VulkanImageTransition.Reacquired(image, range, target)
                : VulkanImageTransition.For(image, range, current, target);

        // THE MAP THIS BATCH IS READING AND WRITING: the committed one until the batch stages its first barrier,
        // and the shadow after that. Indexes are stable across the switch, because the shadow starts as a copy.
        List<Entry> Map => _staging ? _staged : _touched;

        // ONE STAGED BARRIER INTO THE BATCH AT slot, plus the entry update, returning whether anything was staged.
        //
        // THE ENTRY UPDATE LANDS IN THE SHADOW AND NOT IN _touched, which is the order that survives a throw. A
        // batch is ONE vkCmdPipelineBarrier2, so either every transition in it happened or none did, and a map
        // updated before the call would claim the whole batch after a recorder that threw. End would then restore
        // from a layout the image is not in, which is a barrier whose OLD layout is a lie: the validation layer
        // reports it and the driver may honour it by discarding. Building the barrier can throw too (an UNDEFINED
        // on either side, a layout outside the eight), so the staging of state happens after the barrier is built
        // rather than beside it.
        int Stage(in VulkanTrackedImage image, ImageLayout target, int slot)
        {
            int index = Classify(image, out int covered);

            // A WIDER RANGE OVER NARROWER TRACKED ONES. Its own arithmetic, below.
            if (covered > 0) return Widen(image, target, slot, covered);

            ImageLayout current = index < 0 ? image.RestingLayout : Map[index].Current;

            // ALREADY THERE. This is the clause that keeps the barrier count per PASS rather than per draw: a
            // second sampled bind of the same texture in one pass finds it in SHADER_READ_ONLY_OPTIMAL already.
            if (current == target) return 0;

            EnsureCapacity(slot + 1);

            // THE ENTRY'S RANGE WINS OVER THE REQUEST'S, which is what answers a request NARROWER than the entry
            // that holds it: the barrier covers everything the entry claims, so the entry stays uniform and stays
            // one entry. For an exact match the two ranges are the same range, so this is the same line twice.
            ImageSubresourceRange range = index < 0 ? image.SubresourceRange : Map[index].Range;
            _batch[slot] = Transition(image.Image, range, current, target);

            BeginStaging();

            if (index < 0)
            {
                _staged.Add(new Entry(image.Image, image.Range, range, target, image.RestingLayout));
                return 1;
            }

            _staged[index] = _staged[index] with { Current = target };
            return 1;
        }

        // A TRANSITION OF A RANGE THAT CONTAINS TRACKED NARROWER ONES: ONE BARRIER PER PIECE, THEN ONE ENTRY.
        //
        // This is the standard streaming path arriving at a draw. A copy seeds mip 0, mip generation walks the
        // chain a level at a time leaving level N-1 in TRANSFER_SRC_OPTIMAL and level N in TRANSFER_DST_OPTIMAL,
        // and then a sampled bind names the WHOLE chain, because the seam has no texture-view type and the sampled
        // view is full-chain by construction (V-M11). Every piece is transitioned FROM ITS OWN layout, so levels
        // that disagree are not an ambiguity here: each barrier names a layout that is true of the subresources it
        // covers, which is the thing a single whole-range barrier could not do.
        //
        // THE PIECES THEN COLLAPSE INTO ONE ENTRY, which is what keeps the restore at End one barrier instead of
        // one per level, and what makes the SECOND whole-chain bind free rather than another N. So MV5's bound
        // tightens here rather than loosening: the widening is paid once per texture per recording and the count
        // stays independent of draw count.
        int Widen(in VulkanTrackedImage image, ImageLayout target, int slot, int covered)
        {
            List<Entry> map = Map;
            ImageLayout resting = image.RestingLayout;

            // A SUBRESOURCE OF THE REQUEST THAT NO PIECE COVERS IS STILL AT REST (V-F7), and this tracker cannot
            // name that leftover as a range without subtracting rectangles. So the case is answered when the
            // leftover needs no barrier, which is every time the target IS the resting layout, and refused
            // otherwise. That refusal is unreachable on the shipped paths: a whole-chain range exists only for a
            // texture with a full-chain sampled view, and Sampled wins the resting ladder outright, so the target
            // of a whole-chain sampled bind is the resting layout by construction.
            if (target != resting && Covered(map, image) != image.Range.SubresourceCount)
                throw UntrackedRemainder(image, target);

            EnsureCapacity(slot + covered);

            int count = 0;
            for (int i = 0; i < map.Count; i++)
            {
                Entry entry = map[i];
                if (entry.Image != image.Image || !image.Range.Contains(entry.Subrange)) continue;
                if (entry.Current == target) continue;

                _batch[slot + count++] = Transition(entry.Image, entry.Range, entry.Current, target);
            }

            // NOTHING MOVED, so nothing is staged and the pieces stay as they are. Collapsing without a call would
            // be a state change this recording did not make, which is the order the whole staging model exists to
            // keep straight.
            if (count == 0) return 0;

            BeginStaging();

            for (int i = _staged.Count - 1; i >= 0; i--)
            {
                Entry entry = _staged[i];
                if (entry.Image == image.Image && image.Range.Contains(entry.Subrange)) _staged.RemoveAt(i);
            }

            _staged.Add(new Entry(image.Image, image.Range, image.SubresourceRange, target, resting));
            return count;
        }

        // The entry a transition of this range acts on, or -1, plus how many tracked entries the requested range
        // CONTAINS. The entry is the one that EQUALS the request, or the one wider entry that contains it, and both
        // answers mean the same thing to every caller: this is the entry whose layout the range is in and whose
        // range the barrier names. It keeps scanning past a hit deliberately, so a range recorded under a shape
        // this one neither equals, contains, nor sits inside is caught rather than silently mistracked.
        int Classify(in VulkanTrackedImage image, out int covered)
        {
            int found = -1;
            covered = 0;
            List<Entry> map = Map;

            for (int i = 0; i < map.Count; i++)
            {
                Entry entry = map[i];
                if (entry.Image != image.Image) continue;

                if (entry.Subrange == image.Range)
                {
                    found = i;
                    continue;
                }

                if (!entry.Subrange.Overlaps(image.Range)) continue;

                // WIDER IS ANSWERABLE, PARTIAL IS NOT. A contained piece can be transitioned whole from its own
                // layout. A piece that sticks OUT of the request would have to be split in two, and the tracker
                // would then hold two entries for one range that disagree about it the moment either moves.
                if (image.Range.Contains(entry.Subrange))
                {
                    covered++;
                    continue;
                }

                // AND NARROWER THAN ONE TRACKED ENTRY IS ANSWERABLE OVER THAT ENTRY, which is the SECOND arrival
                // of the widening case above and the shape the ocean's mip chain produces every frame after the
                // first. GenerateMipmaps names mip 0 over every layer, which collapses the per-layer entries the
                // seeding copies left into one, and the next frame's copies then ask for one layer of a mip 0 the
                // tracker now holds whole.
                //
                // THE BARRIER WIDENS TO THE ENTRY RATHER THAN THE ENTRY SPLITTING TO THE REQUEST. An entry is
                // UNIFORM by construction, so its layout is true of every subresource in it and one barrier over
                // the whole entry is valid. Splitting instead would mean naming the entry MINUS the request, which
                // is the rectangle subtraction this tracker refuses to do, and it would trade one entry for up to
                // four that all have to be restored separately at End. Widening moves subresources the caller did
                // not name, and that is sound precisely because they are already this list's: every one of them is
                // inside an entry this recording put there, nothing at rest is touched, and End still restores the
                // entry in one barrier.
                if (entry.Subrange.Contains(image.Range))
                {
                    found = i;
                    continue;
                }

                throw Overlapping(image, entry);
            }

            return found;
        }

        // How many subresources of the request are already covered by tracked pieces. The pieces a tracker holds
        // are pairwise non-overlapping, so this equalling the request's own count means they tile it exactly.
        static ulong Covered(List<Entry> map, in VulkanTrackedImage image)
        {
            ulong total = 0;

            for (int i = 0; i < map.Count; i++)
            {
                Entry entry = map[i];
                if (entry.Image == image.Image && image.Range.Contains(entry.Subrange))
                {
                    total += entry.Subrange.SubresourceCount;
                }
            }

            return total;
        }

        // The shadow goes live at the first barrier a batch actually stages, so a boundary that emits nothing
        // (the common case for a plain render target) copies nothing either.
        void BeginStaging()
        {
            if (_staging) return;

            _staged.Clear();
            _staged.AddRange(_touched);
            _staging = true;
        }

        // The call has been made, so what the batch described is now what the recording did.
        void Commit()
        {
            if (!_staging) return;

            _touched.Clear();
            _touched.AddRange(_staged);
        }

        void EnsureCapacity(int required)
        {
            if (required <= _batch.Length) return;

            int capacity = _batch.Length;
            while (capacity < required) capacity <<= 1;

            Array.Resize(ref _batch, capacity);
        }

        static void RequireImage(in VulkanTrackedImage image)
        {
            if (image.Image != 0) return;

            throw new ArgumentException(
                "A native Vulkan layout transition was asked for on an image handle of 0, which is what a STAGING "
                + "texture carries: a staging texture is a VkBuffer with a software subresource layout and has no "
                + "image, no view and no layout at all (V-C7). Nothing can transition one, and a caller that "
                + "tried lost track of which kind of resource it is holding.",
                nameof(image));
        }

        // TWO RANGES OF ONE IMAGE THAT PARTIALLY OVERLAP CANNOT BOTH BE TRACKED. Transitioning the request would
        // move PART of the tracked entry, so that entry would claim a layout half its subresources no longer have,
        // and the restore at End would then emit a barrier whose OLD layout is a lie, which the validation layer
        // reports and which corrupts contents without it. Disjoint ranges are fine and are how a mip chain is
        // generated a level at a time, and a range that CONTAINS the tracked ones is fine too and is how the chain
        // is then sampled whole.
        static InvalidOperationException Overlapping(in VulkanTrackedImage image, in Entry entry)
            => new(
                "A native Vulkan command list transitioned one image through two PARTIALLY OVERLAPPING subresource "
                + "ranges: they share a subresource, and neither contains the other. Tracking is per subresource "
                + "range (V-F6), so transitioning this one would move part of the tracked range and leave its "
                + "entry claiming a layout the rest of it no longer has, and the resting-layout restore at End "
                + "would then name an old layout the image is not in. A range that CONTAINS the tracked ones is "
                + "answered (one barrier per piece, from each piece's own layout, collapsing to one entry) and so "
                + "are disjoint ranges. Ask for one of those shapes. The image is 0x"
                + entry.Image.ToString("X", CultureInfo.InvariantCulture) + ", tracked over mips "
                + Describe(entry.Subrange) + " and asked for over mips " + Describe(image.Range) + ".");

        // A WIDER RANGE WHOSE UNTRACKED LEFTOVER WOULD NEED A BARRIER OF ITS OWN. Everything the tracker has not
        // touched is at REST, so the leftover needs nothing whenever the target IS the resting layout, which is
        // every whole-chain sampled bind. Naming the leftover as a range means subtracting rectangles and then
        // tracking the pieces of the result, which buys a shape nothing in this backend asks for.
        static InvalidOperationException UntrackedRemainder(in VulkanTrackedImage image, ImageLayout target)
            => new(
                "A native Vulkan command list transitioned a subresource range WIDER than the ranges it has "
                + "tracked, to a layout that is not the image's resting one, and the parts of that range it never "
                + "touched are still at rest. Answering it would mean emitting a barrier over the request MINUS "
                + "the tracked pieces, which is a range this tracker cannot name without subtracting rectangles. "
                + "Transition the untracked levels explicitly first, or target the resting layout, which is what "
                + "a whole-chain sampled bind does: Sampled wins the resting ladder, so such a texture rests in "
                + "SHADER_READ_ONLY_OPTIMAL. The image is 0x"
                + image.Image.ToString("X", CultureInfo.InvariantCulture) + ", asked for over mips "
                + Describe(image.Range) + " to " + target + ".");

        // A QUERY WITH NO ANSWER, unlike the transition of the same range. See LayoutOf's remarks: a wider range
        // over pieces that may disagree has no single layout, and inventing one is how a caller skips a barrier.
        static InvalidOperationException NoSingleLayout(in VulkanTrackedImage image, int covered)
            => new(
                "A native Vulkan command list asked for the layout of a subresource range that CONTAINS "
                + covered.ToString(CultureInfo.InvariantCulture) + " narrower tracked range(s), which have no one "
                + "layout between them: a mip chain mid-generation has level N-1 in TRANSFER_SRC_OPTIMAL and level "
                + "N in TRANSFER_DST_OPTIMAL. Transitioning that range IS defined, because it makes the range "
                + "uniform, so ask for the transition rather than the layout, or query the pieces. The image is 0x"
                + image.Image.ToString("X", CultureInfo.InvariantCulture) + ", asked for over mips "
                + Describe(image.Range) + ".");

        static string Describe(in VulkanImageSubrange range)
            => range.BaseMipLevel.ToString(CultureInfo.InvariantCulture) + "+"
                + range.LevelCount.ToString(CultureInfo.InvariantCulture) + " layers "
                + range.BaseArrayLayer.ToString(CultureInfo.InvariantCulture) + "+"
                + range.LayerCount.ToString(CultureInfo.InvariantCulture);

        /// <summary>One tracked range: what it is, where the list moved it to, and where <c>End</c> puts it back.
        /// </summary>
        /// <param name="Image">The <c>VkImage</c>.</param>
        /// <param name="Subrange">The range as plain numbers, which is half the entry's identity.</param>
        /// <param name="Range">The same range as the barrier names it, kept so a restore does not recompute the
        /// aspect mask.</param>
        /// <param name="Current">The layout this recording last moved it to.</param>
        /// <param name="Resting">The canonical resting layout it is restored to.</param>
        readonly record struct Entry(
            ulong Image, VulkanImageSubrange Subrange, ImageSubresourceRange Range, ImageLayout Current,
            ImageLayout Resting);
    }
}
