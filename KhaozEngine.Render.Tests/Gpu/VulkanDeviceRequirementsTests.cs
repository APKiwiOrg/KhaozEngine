using System;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The DECISION half of the native Vulkan support probe, driven device-free: section 5.2's four hard
    /// requirements plus section 4.1's three further reads, from
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>, each failed one at a time against fabricated
    /// <see cref="VulkanDeviceFacts"/>.
    /// <para>
    /// This is the entire reason the probe is split in two. READING those values needs a loader, an instance and a
    /// driver, which this developer machine has none of and only one CI leg has. DECIDING on them needs nothing at
    /// all, so every requirement in the design is covered on every leg, including the ones nobody can produce a
    /// device to fail: no test rig in this fleet can offer a device with no coherent host-visible memory type or
    /// with <c>maxDescriptorSetUniformBuffersDynamic</c> below the Vulkan required minimum, and both are checked
    /// here regardless.
    /// </para>
    /// </summary>
    public sealed class VulkanDeviceRequirementsTests
    {
        // Vulkan packs a version as variant(3) major(7) minor(10) patch(12), so 1.2.0 is this. Written out by
        // hand ONCE, here, because the whole point of the rows below is driving values a device would report and
        // the production code deliberately never packs one. The round-trip row underneath is what keeps this
        // constant honest rather than asserted-by-assumption.
        const uint Vulkan120 = (1u << 22) | (2u << 12);

        /// <summary>
        /// The everything-present case, which every row below is a single mutation away from. Asserted on its own
        /// first, because a baseline that quietly failed for its own reason would make all seven rejection rows
        /// pass while proving nothing.
        /// </summary>
        [Fact]
        public void ACapableDevice_IsAccepted()
        {
            Assert.Null(VulkanDeviceRequirements.MissingRequirement(Capable(), presentationRequired: false));
            Assert.Null(VulkanDeviceRequirements.MissingRequirement(Capable(), presentationRequired: true));
        }

        /// <summary>
        /// A device below the 1.3 floor is rejected on its VERSION and named, which is what "fails loudly on a 1.2
        /// machine instead of crashing on frame one" means (decision V-N2).
        /// </summary>
        [Fact]
        public void BelowTheVersionFloor_IsRejected_WithTheVersionNamed()
        {
            string? missing = VulkanDeviceRequirements.MissingRequirement(
                Capable() with { ApiVersion = Vulkan120 }, presentationRequired: false);

            Assert.NotNull(missing);
            Assert.Contains("1.2.0", missing, StringComparison.Ordinal);
            Assert.Contains("1.3", missing, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE ORDER OF THE CHECKS IS PART OF THE CONTRACT, and this is the row that pins it. All three mandatory
        /// features are 1.3 core, so a real 1.2 device reports every one of them missing, and a probe that checked
        /// features first would tell a tester three unrelated-looking things instead of the one true thing. The
        /// version is decisive and is reported alone.
        /// </summary>
        [Fact]
        public void A12Device_ReadsAsAVersionProblem_NotAsThreeMissingFeatures()
        {
            VulkanDeviceFacts twelve = Capable() with
            {
                ApiVersion = Vulkan120,
                DynamicRendering = false,
                Synchronization2 = false,
                TimelineSemaphore = false,
            };

            string? missing = VulkanDeviceRequirements.MissingRequirement(twelve, presentationRequired: false);

            Assert.NotNull(missing);
            Assert.Contains("apiVersion", missing, StringComparison.Ordinal);
            // "it reports no ..." is how all three feature rejections open, and none of them is the answer here.
            Assert.DoesNotContain("it reports no", missing, StringComparison.Ordinal);
        }

        /// <summary>
        /// The three mandatory features, each failed on its own on a device that is otherwise at the floor. They
        /// are formalities on a conformant 1.3 driver and they are checked anyway, because the alternative to
        /// failing here is failing at the first <c>vkCmdBeginRendering</c>, <c>vkCmdPipelineBarrier2</c> or
        /// timeline wait, none of which names the feature it needed.
        /// </summary>
        [Theory]
        [InlineData("dynamicRendering")]
        [InlineData("synchronization2")]
        [InlineData("timelineSemaphore")]
        public void AMissingMandatoryFeature_IsRejected_ByName(string feature)
        {
            VulkanDeviceFacts facts = feature switch
            {
                "dynamicRendering" => Capable() with { DynamicRendering = false },
                "synchronization2" => Capable() with { Synchronization2 = false },
                "timelineSemaphore" => Capable() with { TimelineSemaphore = false },
                _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "unknown feature row"),
            };

            string? missing = VulkanDeviceRequirements.MissingRequirement(facts, presentationRequired: false);

            Assert.NotNull(missing);
            Assert.Contains(feature, missing, StringComparison.Ordinal);
        }

        /// <summary>
        /// V-M4's read. The uniform ring is PINNED to a host-visible <c>HOST_COHERENT</c> type, and 9.2's whole
        /// no-flush-required claim rests on there being one, so a device reporting none is refused rather than run
        /// with a ring whose writes the GPU may never see.
        /// </summary>
        [Fact]
        public void NoCoherentHostVisibleMemoryType_IsRejected()
        {
            string? missing = VulkanDeviceRequirements.MissingRequirement(
                Capable() with { HasCoherentHostVisibleMemoryType = false }, presentationRequired: false);

            Assert.NotNull(missing);
            Assert.Contains("HOST_COHERENT", missing, StringComparison.Ordinal);
        }

        /// <summary>
        /// Section 8.3's FOURTH defence, and the only one of that section's four that answers for the MACHINE at
        /// runtime. A device below the count the engine's pipeline layouts spend falls back through the reported
        /// path instead of throwing partway into a run.
        /// </summary>
        [Fact]
        public void BelowTheDynamicUniformDescriptorLimit_IsRejected_WithBothNumbersNamed()
        {
            string? missing = VulkanDeviceRequirements.MissingRequirement(
                Capable() with
                {
                    MaxDescriptorSetUniformBuffersDynamic =
                        VulkanDeviceRequirements.RequiredDynamicUniformBuffers - 1,
                },
                presentationRequired: false);

            Assert.NotNull(missing);
            Assert.Contains("maxDescriptorSetUniformBuffersDynamic", missing, StringComparison.Ordinal);
            Assert.Contains("7", missing, StringComparison.Ordinal);
            Assert.Contains("8", missing, StringComparison.Ordinal);
        }

        /// <summary>
        /// Exactly AT the limit is accepted, which is the boundary the off-by-one would sit on. The engine spends
        /// the whole budget by construction (section 8.3 bounds the shipped layouts at this same number), so a
        /// device reporting precisely Vulkan's required minimum is the common case rather than an edge one.
        /// </summary>
        [Fact]
        public void ExactlyAtTheDynamicUniformDescriptorLimit_IsAccepted()
        {
            Assert.Null(VulkanDeviceRequirements.MissingRequirement(
                Capable() with
                {
                    MaxDescriptorSetUniformBuffersDynamic =
                        VulkanDeviceRequirements.RequiredDynamicUniformBuffers,
                },
                presentationRequired: false));
        }

        /// <summary>One graphics queue is the entire queue model (V-N5), so a device with no graphics family at
        /// all cannot run the backend on either path.</summary>
        [Fact]
        public void NoGraphicsQueueFamily_IsRejected()
        {
            string? missing = VulkanDeviceRequirements.MissingRequirement(
                Capable() with { HasGraphicsQueueFamily = false }, presentationRequired: false);

            Assert.NotNull(missing);
            Assert.Contains("GRAPHICS", missing, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE ONE REQUIREMENT THAT DIFFERS BY PATH, which is why it is a parameter rather than a field of the
        /// facts. A graphics family that cannot present is fatal to a windowed run and irrelevant to a headless
        /// one, and <c>IsSupported()</c> receives no window, so the probe asks with false and swapchain creation
        /// asks the same method with true. Both halves are pinned here so neither can drift into asking the
        /// question the other way round.
        /// </summary>
        [Theory]
        [InlineData(false, null)]
        [InlineData(true, "present")]
        public void ANonPresentingGraphicsFamily_IsFatalOnlyOnTheWindowedPath(
            bool presentationRequired, string? expectedFragment)
        {
            string? missing = VulkanDeviceRequirements.MissingRequirement(
                Capable() with { GraphicsFamilyPresents = false }, presentationRequired);

            if (expectedFragment is null)
            {
                Assert.Null(missing);
                return;
            }

            Assert.NotNull(missing);
            Assert.Contains(expectedFragment, missing, StringComparison.Ordinal);
        }

        /// <summary>
        /// Null is the ONLY yes, so no rejection may be an empty or whitespace string. Stated as its own row
        /// because the caller branches on null and logs the value, and a blank rejection would read in a session
        /// log as a machine that failed for no reason at all.
        /// </summary>
        [Fact]
        public void EveryRejection_IsARealSentence()
        {
            VulkanDeviceFacts[] broken =
            {
                Capable() with { ApiVersion = Vulkan120 },
                Capable() with { DynamicRendering = false },
                Capable() with { Synchronization2 = false },
                Capable() with { TimelineSemaphore = false },
                Capable() with { HasCoherentHostVisibleMemoryType = false },
                Capable() with { MaxDescriptorSetUniformBuffersDynamic = 0 },
                Capable() with { HasGraphicsQueueFamily = false },
                Capable() with { GraphicsFamilyPresents = false },
            };

            foreach (VulkanDeviceFacts facts in broken)
            {
                string? missing = VulkanDeviceRequirements.MissingRequirement(facts, presentationRequired: true);
                Assert.False(string.IsNullOrWhiteSpace(missing),
                    "A rejected device must say what it is missing, and this one was turned away with nothing "
                    + "to print: " + facts);
            }
        }

        /// <summary>
        /// The version formatter, and the row that keeps <see cref="Vulkan120"/> above honest. Production code
        /// never packs a version and reads plenty of them, so the hand-packed constant these rows drive is the one
        /// piece of Vulkan bit layout in this file and it is checked against the formatter that unpacks it.
        /// </summary>
        [Fact]
        public void TheVersionFormatter_RoundTripsThePackedEncoding()
        {
            Assert.Equal("1.2.0", VulkanDeviceRequirements.FormatApiVersion(Vulkan120));
            Assert.Equal("1.3.0", VulkanDeviceRequirements.FormatApiVersion(
                VulkanDeviceRequirements.MinimumApiVersion));
        }

        // A device that meets every requirement, at exactly the floor rather than comfortably above it: the
        // version is the minimum and the descriptor limit is the Vulkan required minimum, so a rejection row that
        // accidentally reads a NEIGHBOURING field still fails rather than passing on slack.
        static VulkanDeviceFacts Capable() => new(
            DeviceName: "test device",
            ApiVersion: VulkanDeviceRequirements.MinimumApiVersion,
            DynamicRendering: true,
            Synchronization2: true,
            TimelineSemaphore: true,
            HasCoherentHostVisibleMemoryType: true,
            MaxDescriptorSetUniformBuffersDynamic: VulkanDeviceRequirements.RequiredDynamicUniformBuffers,
            HasGraphicsQueueFamily: true,
            GraphicsFamilyPresents: true);
    }
}
