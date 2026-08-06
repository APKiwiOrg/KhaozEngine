using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Section 9.1's memory-type preference ladders, decisions V-M1 and V-M4: device-local for static resources,
    /// host-visible coherent for upload and for the ring, host-visible CACHED preferred for readback.
    /// <para>
    /// DEVICE-FREE, over fabricated memory types, which is the same shape row 2 took with
    /// <c>VulkanDeviceRequirements</c> over <c>VulkanDeviceFacts</c>. Choosing the wrong memory type does not
    /// fail: it runs, slowly or incorrectly, on some machines and not on others, so the only way to hold the
    /// decision is to fabricate each machine shape and pin what it answers.
    /// </para>
    /// <para>
    /// THE RUNGS THEMSELVES ARE PINNED as well as their consequences. A reordered ladder that happens to pick the
    /// same type on the fabricated device in front of it is exactly the regression a purely behavioural test
    /// misses, and the ladder is the artifact section 9.1 actually specifies.
    /// </para>
    /// </summary>
    public sealed class VulkanMemoryTypeSelectionTests
    {
        const VulkanMemoryTrait Local = VulkanMemoryTrait.DeviceLocal;
        const VulkanMemoryTrait Visible = VulkanMemoryTrait.HostVisible;
        const VulkanMemoryTrait Coherent = VulkanMemoryTrait.HostCoherent;
        const VulkanMemoryTrait Cached = VulkanMemoryTrait.HostCached;
        const VulkanMemoryTrait None = VulkanMemoryTrait.None;

        /// <summary>The static-resource ladder: device-local and NOT host-visible first (the discrete card's
        /// resizable-BAR window is not where meshes go), then device-local at all, then anything.</summary>
        [Fact]
        public void TheDeviceLocalLadder_IsExactlyThreeRungs()
            => Assert.Equal(
                new VulkanMemoryTypeRung[]
                {
                    new(Local, Visible),
                    new(Local, None),
                    new(None, None),
                },
                VulkanMemoryTypeSelection.Ladder(VulkanMemoryUsage.DeviceLocal));

        /// <summary>The upload ladder: host-visible coherent and NOT device-local first, then coherent at all,
        /// then any host-visible type, which is the rung where a flush becomes real.</summary>
        [Fact]
        public void TheUploadLadder_IsExactlyThreeRungs()
            => Assert.Equal(
                new VulkanMemoryTypeRung[]
                {
                    new(Visible | Coherent, Local),
                    new(Visible | Coherent, None),
                    new(Visible, None),
                },
                VulkanMemoryTypeSelection.Ladder(VulkanMemoryUsage.Upload));

        /// <summary>
        /// THE RING HAS ONE RUNG AND NO FALLBACK (V-M4). This is the row that would catch somebody adding a
        /// "reasonable" fallback to it: 9.2's whole no-barrier argument rests on the ring's memory being coherent,
        /// and a fallback would silently make every uniform write need a per-frame flush over every written
        /// segment, which is exactly the per-frame work the ring exists to remove.
        /// </summary>
        [Fact]
        public void TheRingLadder_IsOneRungAndHasNoFallback()
        {
            IReadOnlyList<VulkanMemoryTypeRung> ladder = VulkanMemoryTypeSelection.Ladder(VulkanMemoryUsage.Ring);

            Assert.Equal(new VulkanMemoryTypeRung[] { new(Visible | Coherent, None) }, ladder);
        }

        /// <summary>
        /// The readback ladder, and the ordering of its middle two rungs is the interesting part: cached WITHOUT
        /// coherence is preferred over coherent WITHOUT caching. An uncached host read of a whole surface costs
        /// far more than one <c>vkInvalidateMappedMemoryRanges</c> per map, and that preference is what makes the
        /// invalidate path real code rather than a defensive branch.
        /// </summary>
        [Fact]
        public void TheReadbackLadder_PrefersCachedOverCoherent()
            => Assert.Equal(
                new VulkanMemoryTypeRung[]
                {
                    new(Visible | Cached | Coherent, None),
                    new(Visible | Cached, None),
                    new(Visible | Coherent, None),
                    new(Visible, None),
                },
                VulkanMemoryTypeSelection.Ladder(VulkanMemoryUsage.Readback));

        /// <summary>
        /// A DISCRETE CARD's usual layout, which is the shape all four ladders are written for: a pure
        /// device-local type, a plain upload type, a cached readback type, and a small host-visible device-local
        /// window. Every usage lands on its FIRST rung, which is what "these ladders describe real hardware"
        /// means.
        /// </summary>
        /// <remarks>A <c>[Fact]</c> over all four rather than a <c>[Theory]</c> per usage, because xUnit needs a
        /// public test method and <c>VulkanMemoryUsage</c> is internal, so it cannot appear in a signature
        /// here.</remarks>
        [Fact]
        public void OnADiscreteCard_EveryUsageLandsOnItsFirstRung()
        {
            (VulkanMemoryUsage Usage, int Type)[] expected =
            [
                (VulkanMemoryUsage.DeviceLocal, 0),
                (VulkanMemoryUsage.Upload, 1),
                (VulkanMemoryUsage.Ring, 1),
                (VulkanMemoryUsage.Readback, 2),
            ];

            foreach ((VulkanMemoryUsage usage, int type) in expected)
            {
                int chosen = VulkanMemoryTypeSelection.Choose(usage, uint.MaxValue, Discrete(), out int rung);

                Assert.Equal(type, chosen);
                Assert.Equal(0, rung);
            }
        }

        /// <summary>
        /// A UNIFIED-MEMORY DEVICE, which is every integrated GPU and lavapipe: every type is device-local AND
        /// host-visible, so the static ladder's first rung matches nothing and it drops to the second. This is the
        /// machine the Vulkan CI leg actually runs on, so a ladder that only worked on the discrete shape would
        /// fail there and nowhere else.
        /// </summary>
        [Fact]
        public void OnUnifiedMemory_TheStaticLadderDropsToItsSecondRung()
        {
            int chosen = VulkanMemoryTypeSelection.Choose(
                VulkanMemoryUsage.DeviceLocal, uint.MaxValue, Unified(), out int rung);

            Assert.Equal(0, chosen);
            Assert.Equal(1, rung);
        }

        /// <summary>On unified memory the upload ladder drops to its second rung for the mirror reason: there is
        /// no host-visible type that is not also device-local.</summary>
        [Fact]
        public void OnUnifiedMemory_TheUploadLadderDropsToItsSecondRung()
        {
            int chosen = VulkanMemoryTypeSelection.Choose(
                VulkanMemoryUsage.Upload, uint.MaxValue, Unified(), out int rung);

            Assert.Equal(0, chosen);
            Assert.Equal(1, rung);
        }

        /// <summary>
        /// THE CASE THE INVALIDATE PATH EXISTS FOR: a device whose only cached type is NOT coherent. Readback
        /// takes it anyway, at rung 1, and the chosen type reports no coherence, which is what makes the chunk
        /// emit a real <c>vkInvalidateMappedMemoryRanges</c> and raise its own suballocation alignment to
        /// <c>nonCoherentAtomSize</c>.
        /// </summary>
        [Fact]
        public void ReadbackTakesACachedNonCoherentType_WhichIsWhatReachesInvalidate()
        {
            IReadOnlyList<VulkanMemoryTypeInfo> types =
            [
                new(0, 0, Local),
                new(1, 1, Visible | Coherent),
                new(2, 1, Visible | Cached),
            ];

            int chosen = VulkanMemoryTypeSelection.Choose(
                VulkanMemoryUsage.Readback, uint.MaxValue, types, out int rung);

            Assert.Equal(2, chosen);
            Assert.Equal(1, rung);
            Assert.False(types[chosen].HostCoherent);
            Assert.True(types[chosen].HostVisible);
        }

        /// <summary>The resource's own <c>memoryTypeBits</c> is honoured, so a mask that excludes the preferred
        /// type moves the answer down the ladder rather than producing an illegal one. A bind against a type the
        /// requirements forbade is a validation error, not a slow path.</summary>
        [Fact]
        public void AMemoryTypeBitsMask_ExcludesTypesFromEveryRung()
        {
            // Type 0 is the pure device-local one. Excluding it forces the second rung's device-local match.
            int chosen = VulkanMemoryTypeSelection.Choose(
                VulkanMemoryUsage.DeviceLocal, memoryTypeBits: 0b1110, Discrete(), out int rung);

            Assert.Equal(3, chosen);
            Assert.Equal(1, rung);
        }

        /// <summary>A mask with nothing in it satisfies no rung of any ladder, which is the general no-type
        /// answer rather than a special case.</summary>
        [Fact]
        public void AnEmptyMask_ChoosesNothing()
        {
            int chosen = VulkanMemoryTypeSelection.Choose(
                VulkanMemoryUsage.Upload, memoryTypeBits: 0, Discrete(), out int rung);

            Assert.Equal(VulkanMemoryTypeSelection.NoType, chosen);
            Assert.Equal(VulkanMemoryTypeSelection.NoType, rung);
        }

        /// <summary>
        /// LAZILY ALLOCATED AND PROTECTED ARE NEVER CHOSEN, on any ladder, including the static ladder's
        /// match-anything last rung. Lazily allocated memory can only back a transient attachment and a driver may
        /// commit nothing for it. Protected memory needs a protected-capable device and queue, which this backend
        /// does not create.
        /// </summary>
        [Fact]
        public void LazyAndProtectedTypes_AreNeverChosen()
        {
            IReadOnlyList<VulkanMemoryTypeInfo> types =
            [
                new(0, 0, Local | VulkanMemoryTrait.LazilyAllocated),
                new(1, 0, Local | Visible | Coherent | Cached | VulkanMemoryTrait.Protected),
            ];

            VulkanMemoryUsage[] every =
            [
                VulkanMemoryUsage.DeviceLocal,
                VulkanMemoryUsage.Upload,
                VulkanMemoryUsage.Ring,
                VulkanMemoryUsage.Readback,
            ];

            foreach (VulkanMemoryUsage usage in every)
            {
                Assert.Equal(VulkanMemoryTypeSelection.NoType,
                    VulkanMemoryTypeSelection.Choose(usage, uint.MaxValue, types, out _));
            }
        }

        /// <summary>
        /// A DEVICE WITH NO COHERENT HOST-VISIBLE TYPE refuses the ring outright, and the sentence it produces
        /// says WHY there is no fallback rather than reading as an ordinary out-of-memory. Row 2's probe already
        /// refuses such a device, so reaching this means the machine changed between the probe and the call, and
        /// the message has to say so or the reader will look for a memory leak.
        /// </summary>
        [Fact]
        public void WithNoCoherentType_TheRingIsRefusedWithItsOwnReason()
        {
            IReadOnlyList<VulkanMemoryTypeInfo> types =
            [
                new(0, 0, Local),
                new(1, 1, Visible | Cached),
            ];

            Assert.Equal(VulkanMemoryTypeSelection.NoType,
                VulkanMemoryTypeSelection.Choose(VulkanMemoryUsage.Ring, uint.MaxValue, types, out _));

            string message = VulkanMemoryTypeSelection.Unsatisfiable(
                VulkanMemoryUsage.Ring, uint.MaxValue, types);

            Assert.Contains("V-M4", message, System.StringComparison.Ordinal);
            Assert.Contains("HOST_COHERENT", message, System.StringComparison.Ordinal);

            // Upload survives the same device by dropping to its last rung, which is the whole point of the ring
            // being the ONE hard requirement rather than the rule everywhere.
            Assert.Equal(1, VulkanMemoryTypeSelection.Choose(
                VulkanMemoryUsage.Upload, uint.MaxValue, types, out int uploadRung));
            Assert.Equal(2, uploadRung);
        }

        /// <summary>The static ladder's last rung takes ANYTHING, so a resource whose mask excludes every
        /// device-local type still allocates rather than refusing. Running out of host memory later beats
        /// refusing to allocate now.</summary>
        [Fact]
        public void TheStaticLadder_FallsAllTheWayToAnything()
        {
            IReadOnlyList<VulkanMemoryTypeInfo> types = [new(0, 0, Visible | Coherent)];

            Assert.Equal(0, VulkanMemoryTypeSelection.Choose(
                VulkanMemoryUsage.DeviceLocal, uint.MaxValue, types, out int rung));
            Assert.Equal(2, rung);
        }

        /// <summary>V-M4's own read, over the translated types rather than over the driver's struct a second
        /// time: this is what the support probe refuses a device on.</summary>
        [Fact]
        public void HasCoherentHostVisibleType_AnswersOverTheTranslatedTypes()
        {
            Assert.True(new VulkanMemoryFacts(Discrete(), 64, 4096).HasCoherentHostVisibleType);
            Assert.False(new VulkanMemoryFacts([new(0, 0, Local), new(1, 1, Visible | Cached)], 64, 4096)
                .HasCoherentHostVisibleType);
            Assert.False(VulkanMemoryFacts.Empty.HasCoherentHostVisibleType);
        }

        // A discrete card's usual four types: pure VRAM, a plain upload heap, a cached readback heap, and the
        // small host-visible device-local window (resizable BAR).
        static IReadOnlyList<VulkanMemoryTypeInfo> Discrete() =>
        [
            new(0, 0, Local),
            new(1, 1, Visible | Coherent),
            new(2, 1, Visible | Coherent | Cached),
            new(3, 0, Local | Visible | Coherent),
        ];

        // Unified memory: every type is device-local and host-visible, which is what an integrated GPU and
        // lavapipe both report.
        static IReadOnlyList<VulkanMemoryTypeInfo> Unified() =>
        [
            new(0, 0, Local | Visible | Coherent),
            new(1, 0, Local | Visible | Coherent | Cached),
        ];
    }
}
