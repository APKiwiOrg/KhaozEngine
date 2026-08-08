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
    /// <para><b>WHY LIST-LOCAL RATHER THAN ON THE TEXTURE (V-F7, section 2.5).</b> The incumbent tracks Vulkan
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
    /// Both numbers are countable off <see cref="VulkanCountingCmdSink"/>, because a budget that froze only the
    /// call count would pass a recorder that put a barrier per draw into one batch.</para>
    ///
    /// <para><b>NOTHING HERE IS SYNCHRONISED</b>, on the same grounds as the list that owns it: one list records on
    /// one thread at a time and this tracker is that list's alone. That is the property this whole design exists
    /// to make true.</para>
    /// </summary>
    internal sealed class VulkanLayoutTracker
    {
        readonly IVulkanBarrierRecorder _recorder;
        readonly List<Entry> _touched = new();

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

        /// <summary>How many subresource ranges this recording has moved away from rest and not yet restored.
        /// The number MV5's bound is stated in: barriers per frame proportional to passes times touched textures.
        /// </summary>
        internal int TouchedCount => _touched.Count;

        /// <summary>
        /// The layout <paramref name="image"/> is in as far as this recording is concerned, which is its RESTING
        /// layout until this list transitions it. Never <c>UNDEFINED</c>: a list assumes every texture is at rest
        /// when it starts, so there is no untracked state for it to be in.
        /// </summary>
        internal ImageLayout LayoutOf(in VulkanTrackedImage image)
        {
            RequireImage(image);

            int index = IndexOf(image);
            return index < 0 ? image.RestingLayout : _touched[index].Current;
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

            if (!Stage(image, target, 0)) return;

            _recorder.Emit(commandBuffer, _batch.AsSpan(0, 1));
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

            int count = 0;
            for (int i = 0; i < colour.Length; i++)
            {
                var attachment = VulkanTrackedImage.ForAttachment(in colour[i]);
                if (Stage(attachment, attachment.AttachmentLayout, count)) count++;
            }

            if (framebuffer.HasDepth)
            {
                VulkanAttachment depth = framebuffer.Depth;
                var attachment = VulkanTrackedImage.ForAttachment(in depth);
                if (Stage(attachment, attachment.AttachmentLayout, count)) count++;
            }

            if (count == 0) return;

            _recorder.Emit(commandBuffer, _batch.AsSpan(0, count));
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

                _batch[count++] = VulkanImageTransition.For(
                    entry.Image, entry.Range, entry.Current, entry.Resting);
            }

            _touched.Clear();

            if (count == 0) return;

            _recorder.Emit(commandBuffer, _batch.AsSpan(0, count));
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
        internal void Reset() => _touched.Clear();

        // ONE STAGED BARRIER INTO THE BATCH AT slot, plus the entry update, returning whether anything was staged.
        // The state is updated with the barrier rather than after the emit, so a batch of N transitions and the
        // map cannot disagree about which of the N happened.
        bool Stage(in VulkanTrackedImage image, ImageLayout target, int slot)
        {
            int index = IndexOf(image);
            ImageLayout current = index < 0 ? image.RestingLayout : _touched[index].Current;

            // ALREADY THERE. This is the clause that keeps the barrier count per PASS rather than per draw: a
            // second sampled bind of the same texture in one pass finds it in SHADER_READ_ONLY_OPTIMAL already.
            if (current == target) return false;

            EnsureCapacity(slot + 1);

            ImageSubresourceRange range = index < 0 ? image.SubresourceRange : _touched[index].Range;
            _batch[slot] = VulkanImageTransition.For(image.Image, range, current, target);

            if (index < 0)
            {
                _touched.Add(new Entry(image.Image, image.Range, range, target, image.RestingLayout));
                return true;
            }

            _touched[index] = _touched[index] with { Current = target };
            return true;
        }

        // The entry for this image AND this exact range, or -1. It keeps scanning past a hit deliberately, so an
        // overlapping range recorded under a different shape is caught rather than silently mistracked.
        int IndexOf(in VulkanTrackedImage image)
        {
            int found = -1;

            for (int i = 0; i < _touched.Count; i++)
            {
                Entry entry = _touched[i];
                if (entry.Image != image.Image) continue;

                if (entry.Subrange == image.Range)
                {
                    found = i;
                    continue;
                }

                if (entry.Subrange.Overlaps(image.Range)) throw Overlapping(image, entry);
            }

            return found;
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

        // TWO RANGES OF ONE IMAGE THAT OVERLAP WITHOUT BEING EQUAL CANNOT BOTH BE TRACKED. Transitioning one would
        // leave the other entry claiming a layout the image no longer has, and the restore at End would then emit
        // a barrier whose OLD layout is a lie, which the validation layer reports and which corrupts contents
        // without it. Disjoint ranges are fine and are how a mip chain is generated a level at a time.
        static InvalidOperationException Overlapping(in VulkanTrackedImage image, in Entry entry)
            => new(
                "A native Vulkan command list transitioned one image through two OVERLAPPING subresource ranges "
                + "that are not the same range. Tracking is per subresource range (V-F6), so two entries that "
                + "share a subresource would disagree about its layout the moment either of them moved, and the "
                + "resting-layout restore at End would then name an old layout the image is not in. Use one range "
                + "shape per image within a recording, or split the wider transition into the disjoint ranges the "
                + "narrower one uses. The image is 0x"
                + entry.Image.ToString("X", CultureInfo.InvariantCulture) + ", tracked over mips "
                + Describe(entry.Subrange) + " and asked for over mips " + Describe(image.Range) + ".");

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
