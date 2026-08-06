using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>One rung of a preference ladder: what a type MUST carry and what it must NOT.</summary>
    /// <param name="Required">Every trait the type has to have. An empty mask matches anything, which is what a
    /// last-resort rung is.</param>
    /// <param name="Forbidden">Any trait that disqualifies the type. Used to express "device-local but not
    /// host-visible" and "host-visible but not device-local", which are the two preferences that cannot be written
    /// as a required mask and that separate a discrete card from a unified-memory one.</param>
    internal readonly record struct VulkanMemoryTypeRung(VulkanMemoryTrait Required, VulkanMemoryTrait Forbidden);

    /// <summary>
    /// THE MEMORY-TYPE PREFERENCE LADDERS of section 9.1, as data, and the first-match walk over them. Decisions
    /// V-M1 and V-M4.
    ///
    /// <para><b>DEVICE-FREE ON PURPOSE, exactly like <see cref="VulkanDeviceRequirements"/>.</b> Every rung is a
    /// comparison against a translated <see cref="VulkanMemoryTypeInfo"/>, so a test fabricates a device's memory
    /// types and pins which rung each usage lands on, one at a time, with no loader anywhere. Choosing the wrong
    /// memory type does not fail: it runs, slowly or incorrectly, on some machines and not others, which is
    /// precisely the class of defect a device-free test can hold and a golden cannot.</para>
    ///
    /// <para><b>THE LADDERS ARE ORDERED PREFERENCE, NOT REQUIREMENT, WITH ONE EXCEPTION.</b>
    /// <see cref="VulkanMemoryUsage.Ring"/> has exactly one rung and no fallback (V-M4): 9.2's whole no-barrier
    /// argument rests on the ring's memory being <c>HOST_COHERENT</c>, so a device that cannot supply one is
    /// refused rather than silently run with a flush the ring exists to remove. The spec requires such a type to
    /// exist and row 2's probe already checks for one, so this fails loudly on a device that cannot happen.</para>
    ///
    /// <para><b>COHERENT IS PREFERRED EVERYWHERE, and cached is preferred for READBACK.</b> That pairing is the
    /// whole of the flush-and-invalidate story. The incumbent has no <c>vkFlushMappedMemoryRanges</c> and no
    /// <c>vkInvalidateMappedMemoryRanges</c> anywhere and rests entirely on a coherent type existing. Preferring
    /// coherent keeps the common path free, and preferring CACHED for readback is what makes a non-coherent type
    /// reachable at all: an uncached host read of a whole framebuffer is slow enough to matter, so readback takes
    /// the cached type and pays for it with a real invalidate.</para>
    ///
    /// <para><b>TWO TRAITS ARE NEVER SELECTED, on any rung.</b> <see cref="VulkanMemoryTrait.LazilyAllocated"/> can
    /// only back a transient attachment and a driver may commit nothing for it. <see cref="VulkanMemoryTrait.Protected"/>
    /// needs a protected-capable device and queue, which this backend does not create. Both are excluded once, in
    /// <see cref="Choose"/>, rather than repeated in every rung's forbidden mask, because a rule repeated eight
    /// times is a rule that will one day be repeated seven.</para>
    /// </summary>
    internal static class VulkanMemoryTypeSelection
    {
        /// <summary>What <see cref="Choose"/> returns when no type on the device satisfies any rung of the
        /// ladder for the requested usage AND is permitted by the resource's own type mask.</summary>
        internal const int NoType = -1;

        // The traits that disqualify a type before any rung is consulted. See the class note.
        const VulkanMemoryTrait NeverSelected = VulkanMemoryTrait.LazilyAllocated | VulkanMemoryTrait.Protected;

        const VulkanMemoryTrait Visible = VulkanMemoryTrait.HostVisible;
        const VulkanMemoryTrait Coherent = VulkanMemoryTrait.HostCoherent;
        const VulkanMemoryTrait Cached = VulkanMemoryTrait.HostCached;
        const VulkanMemoryTrait Local = VulkanMemoryTrait.DeviceLocal;

        // DEVICE-LOCAL, for everything the GPU reads and the CPU does not touch after upload.
        //
        // Rung 1 asks for device-local and NOT host-visible, which is the rung that matters on a discrete card:
        // its host-visible device-local type is the resizable-BAR window, historically 256 MiB, and filling it
        // with ordinary meshes and textures is how an allocator turns a fast path into an out-of-memory. Rung 2
        // drops the exclusion, which is what every unified-memory device takes (integrated GPUs and lavapipe
        // report every type as both). Rung 3 takes anything at all, for a device with no device-local type in the
        // resource's own mask, where running out of host memory later beats refusing to allocate now.
        static readonly VulkanMemoryTypeRung[] deviceLocal =
        [
            new(Local, Visible),
            new(Local, VulkanMemoryTrait.None),
            new(VulkanMemoryTrait.None, VulkanMemoryTrait.None),
        ];

        // UPLOAD staging: the CPU writes, the GPU reads once.
        //
        // Rung 1 is host-visible coherent and NOT device-local, the mirror of the device-local ladder's first
        // rung and for the same reason: bytes on their way to VRAM should not be occupying VRAM. Rung 2 drops
        // the exclusion for unified memory. Rung 3 gives up coherence and takes any host-visible type, which is
        // where the flush path stops being theoretical.
        static readonly VulkanMemoryTypeRung[] upload =
        [
            new(Visible | Coherent, Local),
            new(Visible | Coherent, VulkanMemoryTrait.None),
            new(Visible, VulkanMemoryTrait.None),
        ];

        // THE RING: one rung, no fallback (V-M4). See the class note.
        static readonly VulkanMemoryTypeRung[] ring =
        [
            new(Visible | Coherent, VulkanMemoryTrait.None),
        ];

        // READBACK staging: the GPU writes, the CPU reads.
        //
        // Rung 1 is the type that is BOTH cached and coherent, which several drivers do expose and which is the
        // best of both: fast host reads with no invalidate. Rung 2 is cached WITHOUT coherence, deliberately
        // preferred over rung 3's coherent-but-uncached, because an uncached read of a whole surface costs far
        // more than one vkInvalidateMappedMemoryRanges per map. THIS IS THE RUNG THAT MAKES THE INVALIDATE PATH
        // REAL, and the reason it is written here rather than left as a defensive branch further down.
        static readonly VulkanMemoryTypeRung[] readback =
        [
            new(Visible | Cached | Coherent, VulkanMemoryTrait.None),
            new(Visible | Cached, VulkanMemoryTrait.None),
            new(Visible | Coherent, VulkanMemoryTrait.None),
            new(Visible, VulkanMemoryTrait.None),
        ];

        /// <summary>
        /// The ladder for <paramref name="usage"/>, in preference order. Exposed so a test pins the rungs
        /// THEMSELVES rather than only their consequences: a reordered ladder that still picks the same type on
        /// the fabricated device in front of it is exactly the regression a behavioural test misses.
        /// </summary>
        internal static IReadOnlyList<VulkanMemoryTypeRung> Ladder(VulkanMemoryUsage usage) => usage switch
        {
            VulkanMemoryUsage.DeviceLocal => deviceLocal,
            VulkanMemoryUsage.Upload => upload,
            VulkanMemoryUsage.Ring => ring,
            VulkanMemoryUsage.Readback => readback,
            _ => throw new ArgumentOutOfRangeException(nameof(usage), usage,
                "The native Vulkan allocator has no memory-type ladder for this usage. Every member of "
                + "VulkanMemoryUsage must have one, because there is no sensible default: a usage with no ladder "
                + "would fall through to whatever type happened to be first."),
        };

        /// <summary>
        /// Choose a memory type for <paramref name="usage"/>, restricted to the types
        /// <paramref name="memoryTypeBits"/> permits, walking the ladder rung by rung and taking the FIRST type
        /// that matches within a rung.
        /// <para>
        /// FIRST WITHIN A RUNG, in index order, which is the convention every Vulkan allocator uses and the one
        /// the spec's own guidance describes: implementations are required to list types so that a more desirable
        /// type precedes a less desirable one with the same properties. So "first match" is the driver's own
        /// preference order, not an arbitrary one.
        /// </para>
        /// </summary>
        /// <param name="usage">What the memory is for, which selects the ladder.</param>
        /// <param name="memoryTypeBits">The resource's own <c>VkMemoryRequirements.memoryTypeBits</c>: bit
        /// <c>i</c> set means type <c>i</c> is legal for this resource. <c>uint.MaxValue</c> means unrestricted,
        /// which is what a caller with no resource yet passes.</param>
        /// <param name="types">The device's memory types, in index order.</param>
        /// <param name="rung">Which rung of the ladder answered, zero-based, or -1 when none did. For the log
        /// line and for the test that pins the ladder walk rather than only its result.</param>
        /// <returns>The chosen type's index, or <see cref="NoType"/>.</returns>
        internal static int Choose(VulkanMemoryUsage usage, uint memoryTypeBits,
            IReadOnlyList<VulkanMemoryTypeInfo> types, out int rung)
        {
            ArgumentNullException.ThrowIfNull(types);

            IReadOnlyList<VulkanMemoryTypeRung> ladder = Ladder(usage);

            for (int r = 0; r < ladder.Count; r++)
            {
                VulkanMemoryTypeRung step = ladder[r];

                for (int i = 0; i < types.Count; i++)
                {
                    VulkanMemoryTypeInfo type = types[i];

                    if ((memoryTypeBits & (1u << (int)type.Index)) == 0) continue;
                    if (type.HasAny(NeverSelected)) continue;
                    if (!type.Has(step.Required)) continue;
                    if (type.HasAny(step.Forbidden)) continue;

                    rung = r;
                    return (int)type.Index;
                }
            }

            rung = NoType;
            return NoType;
        }

        /// <summary>
        /// The sentence thrown when <see cref="Choose"/> answers <see cref="NoType"/>. It names the usage, the
        /// mask and every type the device actually reported, because the only two ways to get here are a resource
        /// whose <c>memoryTypeBits</c> excludes everything the usage needs, and a device that does not expose a
        /// type the Vulkan spec requires it to.
        /// </summary>
        internal static string Unsatisfiable(VulkanMemoryUsage usage, uint memoryTypeBits,
            IReadOnlyList<VulkanMemoryTypeInfo> types)
        {
            ArgumentNullException.ThrowIfNull(types);

            var reported = new List<string>(types.Count);
            for (int i = 0; i < types.Count; i++)
            {
                reported.Add(types[i].Index.ToString(CultureInfo.InvariantCulture) + ": " + types[i].Traits
                    + " (heap " + types[i].HeapIndex.ToString(CultureInfo.InvariantCulture) + ")");
            }

            string ringNote = usage == VulkanMemoryUsage.Ring
                ? " The ring has no fallback rung by decision (V-M4): its memory must be HOST_COHERENT for the "
                    + "per-frame write path to need no flush, and the support probe already refuses a device that "
                    + "reports no host-visible coherent type, so reaching this means either the machine changed "
                    + "or the resource's own memoryTypeBits excluded every coherent type."
                : string.Empty;

            return $"The native Vulkan allocator found no memory type for {usage} within memoryTypeBits 0x"
                + memoryTypeBits.ToString("x8", CultureInfo.InvariantCulture) + ". The device reported "
                + (reported.Count == 0 ? "no memory types at all" : string.Join(", ", reported)) + "."
                + ringNote;
        }
    }
}
