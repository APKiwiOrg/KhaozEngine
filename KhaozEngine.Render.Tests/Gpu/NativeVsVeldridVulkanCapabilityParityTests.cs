using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION V-T3: the native Vulkan backend's <see cref="GpuCapabilities"/> and the incumbent Vulkan
    /// backend's, compared member by member, with ZERO PERMITTED DIFFERENCES (V-G1, section 14 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>).
    ///
    /// <para><b>ZERO, WHERE THE DIRECT3D 11 SIBLING HAD TO PERMIT ONE.</b> That backend exempts
    /// <see cref="GpuCapabilities.SupportsCompletionFences"/> because Veldrid's Direct3D 11 fence is a CPU-side
    /// submit receipt and the native one is real, so the incumbent's answer is a defect the native backend
    /// corrects. There is no such defect here: <c>VeldridMap.SupportsCompletionFences</c> already answers true
    /// for <c>GraphicsBackend.Vulkan</c>. So a difference this test finds is a bug in
    /// <see cref="VulkanCapabilityRead"/> until proven otherwise, rather than a member to add to an exemption
    /// list.</para>
    ///
    /// <para><b>THE SPLIT.</b> Everything that DECIDES a capability from a probed input is engine logic in
    /// <see cref="VulkanCapabilityRead"/>: the five constants, the device-name normalisation and the sample-count
    /// floor. Those are plain <c>[Fact]</c>s here, on a machine with no Vulkan loader at all, driven off values
    /// written by hand. So is the COMPARER, plus the reflection check that it covers every member of
    /// <see cref="GpuCapabilities"/>, which is the guard that matters most: a member appended to that struct
    /// without a line added to the comparison would make the parity assertion silently weaker while staying
    /// green. The two-device half is a <c>[GpuFact]</c> and needs a real Vulkan device, which nothing on the
    /// current legs has: it first RUNS on the <c>vulkan-native</c> leg row 19
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/529) installs, against lavapipe. Until then its
    /// device-free half and this comparer are what the row actually delivers, which is said out loud here rather
    /// than left for a reader to infer from a green run that asserted nothing.</para>
    ///
    /// <para><b>THE MSAA MEMBER IS THE ONE THIS TEST CANNOT SATISFY YET, AND SAYING SO IS THE POINT.</b>
    /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/> is pinned to
    /// <see cref="VulkanCapabilityRead.NoMultisampling"/> until row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525) reproduces the incumbent's own
    /// <c>GetSampleCountLimit</c> computation, which V-C5 rules is READ OFF the incumbent rather than invented so
    /// that "asserted identical" holds by construction rather than by luck. The comparer covers the member from
    /// today, so the day the two-device row runs against a lavapipe that reports more than one sample it names
    /// exactly that member, which is the correct failure rather than a silent pass.</para>
    ///
    /// <para><b>THE WHOLE CLASS SITS IN THE <c>NativeDeviceLifecycle</c> COLLECTION,</b> which costs the
    /// device-free rows nothing measurable and is what the two-device row needs. See the collection's own
    /// definition for why.</para>
    ///
    /// <para><b>ITS OWN COMPARER, NOT THE DIRECT3D 11 FILE'S.</b> The duplication is deliberate: the two
    /// comparers do not have to agree with each other, they each have to agree with
    /// <see cref="GpuCapabilities"/>, and each file carries its own reflection guard saying so. A member appended
    /// to the struct then fails both, which is the loud outcome, and neither backend's assertion can be weakened
    /// by an edit made for the other one's sake.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class NativeVsVeldridVulkanCapabilityParityTests
    {
        readonly ITestOutputHelper _out;
        public NativeVsVeldridVulkanCapabilityParityTests(ITestOutputHelper o) => _out = o;

        // ---------------------------------------------------------------------------------------------------
        // The device-free half: the capability assembly, from probed inputs, with no device anywhere.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>Section 14's table, member by member. Five of the nine are constants of the configuration
        /// this backend creates, and they are asserted BY VALUE here rather than by reading the constant back, so
        /// a change to one of them fails this test rather than agreeing with itself.</summary>
        [Fact]
        public void Assemble_ProducesTheTableInSection14()
        {
            GpuCapabilities caps = VulkanCapabilityRead.Assemble(
                "llvmpipe (LLVM 17.0.6, 256 bits)", samplerAnisotropy: true,
                supportsShadowMaps: true, maxMsaaSampleCount: 4);

            Assert.False(caps.ClipSpaceYInverted);
            Assert.True(caps.DepthRangeZeroToOne);
            Assert.Equal("llvmpipe (LLVM 17.0.6, 256 bits)", caps.DeviceName);
            Assert.True(caps.SamplerAnisotropy);
            Assert.True(caps.SamplerLodBias);
            Assert.Equal(4, caps.MaxMsaaSampleCount);
            Assert.True(caps.SupportsShadowMaps);
            Assert.True(caps.SupportsCompute);
            Assert.True(caps.SupportsCompletionFences);
        }

        /// <summary>The two members a DEVICE answers are parameters rather than constants, because the feature
        /// chain and the format-properties read own those answers and the capability must not be able to disagree
        /// with either.</summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Assemble_TakesTheTwoDeviceAnswersFromTheCaller(bool anisotropy, bool shadows)
        {
            GpuCapabilities caps = VulkanCapabilityRead.Assemble("d", anisotropy, shadows, 1);

            Assert.Equal(anisotropy, caps.SamplerAnisotropy);
            Assert.Equal(shadows, caps.SupportsShadowMaps);
        }

        /// <summary>
        /// THE NAME THE SEAM CARRIES IS THE DRIVER'S OWN, and the substitution that makes a rejection line
        /// readable stays on the log's side. The incumbent performs no substitution, and
        /// <see cref="GpuCapabilities.DeviceName"/> is compared string for string by the assertion below, so a
        /// synthetic name here would be a capability difference on exactly the devices that report none.
        /// WHITESPACE SURVIVES, which the padded case pins deliberately, for the reason the Direct3D 11 backend
        /// does not trim either: a trim on one path alone fails parity on every machine whose vendor pads.
        /// </summary>
        [Theory]
        [InlineData("llvmpipe (LLVM 17.0.6, 256 bits)", "llvmpipe (LLVM 17.0.6, 256 bits)")]
        [InlineData("NVIDIA GeForce RTX 4070\0\0\0", "NVIDIA GeForce RTX 4070")]
        [InlineData("AMD Radeon RX 7900 XT  ", "AMD Radeon RX 7900 XT  ")]
        [InlineData("\0\0\0", "")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void ReportedDeviceName_IsTheDriversOwnStringAndNeverASubstitute(string? raw, string expected)
        {
            Assert.Equal(expected, VulkanCapabilityRead.ReportedDeviceName(raw));
            Assert.Equal(expected, VulkanCapabilityRead.Assemble(raw, true, true, 1).DeviceName);
        }

        /// <summary>The floor is one sample, which is how the seam spells "no MSAA", and it is what the capability
        /// carries until row 15 supplies the incumbent's own computation.</summary>
        [Fact]
        public void NoMultisampling_IsOneSample()
        {
            Assert.Equal(1, VulkanCapabilityRead.NoMultisampling);
            Assert.Equal(1, VulkanCapabilityRead.Assemble("d", true, true,
                VulkanCapabilityRead.NoMultisampling).MaxMsaaSampleCount);
        }

        // ---------------------------------------------------------------------------------------------------
        // The comparer, and the guard that keeps it complete.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// THE GUARD THAT MAKES THE PARITY ASSERTION MEAN ANYTHING. <see cref="Differences"/> is a hand-written
        /// member-by-member comparison, and a member appended to <see cref="GpuCapabilities"/> without a line
        /// added there would make the zero-difference assertion silently weaker while staying green. Reflection
        /// over the struct's public properties is what catches that, and it is why the comparer returns NAMES
        /// rather than a bool.
        /// </summary>
        [Fact]
        public void TheComparerCoversEveryMemberOfGpuCapabilities()
        {
            var compared = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in Differences(Everything(false), Everything(true))) compared.Add(name);

            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (PropertyInfo p in typeof(GpuCapabilities).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                declared.Add(p.Name);

            Assert.Equal(declared, compared);
        }

        /// <summary>Two identical capability sets report NOTHING, which is the shape V-G1 asserts against the two
        /// real devices. Asserted separately from the completeness guard above, because a comparer that named
        /// every member unconditionally would pass that one.</summary>
        [Fact]
        public void TheComparerReportsNothingWhenTheTwoSetsAgree()
        {
            GpuCapabilities caps = VulkanCapabilityRead.Assemble("llvmpipe", true, true, 4);

            Assert.Empty(Differences(caps, caps));
        }

        /// <summary>And it names the one member that moved, rather than answering a bool. On
        /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/> that is the difference between a one-line fix and a
        /// golden investigation, and it is the member most likely to be the one that moves here.</summary>
        [Fact]
        public void TheComparerNamesTheMemberThatMoved()
        {
            GpuCapabilities incumbent = VulkanCapabilityRead.Assemble("llvmpipe", true, true, 4);
            GpuCapabilities native = VulkanCapabilityRead.Assemble("llvmpipe", true, true, 1);

            Assert.Equal(new[] { nameof(GpuCapabilities.MaxMsaaSampleCount) }, Differences(incumbent, native));
        }

        // ---------------------------------------------------------------------------------------------------
        // The two-device half. Dormant until a leg has a Vulkan device (row 19).
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// DECISION V-T3 ITSELF: both devices in one process, both capability sets read, every member compared,
        /// and NOTHING permitted to differ.
        /// <para>
        /// BOTH devices come up through <see cref="GpuDeviceContext"/>, the native one by naming its kind to
        /// <see cref="GpuDeviceContext.CreateHeadless(GpuBackendKind)"/>. Two backends in one process is what that
        /// overload was made public for, and it is what keeps the second device inside the process-wide creation
        /// gate that the Vulkan loader on lavapipe needs: reaching into <c>GpuBackendProviders</c> and calling the
        /// provider's own creation would build a device around the outside of the gate every other device in the
        /// run went through. A context reports its adopted device's own <see cref="GpuCapabilities"/> verbatim,
        /// so the route changes nothing about what is compared.
        /// </para>
        /// <para>
        /// Returns early, having asserted nothing, in TWO cases, and both are facts about the MACHINE rather than
        /// about the code: a machine the native backend's own functional probe refuses, or an incumbent that did
        /// not come up on Vulkan, so there is nothing to be at parity WITH. The first reads
        /// <see cref="GpuBackendSelector.IsBackendSupported"/>, which resolves a loader, creates a throwaway
        /// instance at the 1.3 floor and reads every physical device against the design's requirements. That is
        /// the shape #504 settled on the Direct3D 11 side, where a feature-deficient box used to go red for a
        /// machine fact, and it matters more here: this developer machine has no Vulkan loader at all, and every
        /// leg but the one row 19 builds is in the same position.
        /// </para>
        /// <para>
        /// THERE IS NO PLATFORM EARLY RETURN, unlike the Direct3D 11 sibling's first one. Vulkan is not a Windows
        /// API and <c>KhaozEngine.Gpu.Vulkan</c> carries no <c>[SupportedOSPlatformGuard]</c> (V-P1), so "can
        /// this machine do it" is entirely the probe's question and an operating-system check here would be a
        /// second answer to it.
        /// </para>
        /// </summary>
        [GpuFact]
        public void NativeAndVeldridVulkanCapabilitiesDoNotDifferAtAll()
        {
            // The probe first, because it is the cheap machine fact and answering it second would build a whole
            // incumbent device on every leg that was never going to have a second one to compare it against.
            if (!GpuBackendSelector.IsBackendSupported(GpuBackendKind.VulkanNative))
            {
                _out.WriteLine("dormant: this machine cannot run the native Vulkan backend, so there is no "
                    + "second device to compare.");
                return;
            }

            using GpuDeviceContext incumbent = GpuDeviceContext.CreateHeadless();
            if (incumbent.Backend != GpuBackendKind.Vulkan)
            {
                _out.WriteLine($"dormant: the incumbent device came up on {incumbent.Backend}, not Vulkan.");
                return;
            }

            using GpuDeviceContext native = GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative);
            GpuCapabilities fromVeldrid = incumbent.Capabilities;
            GpuCapabilities fromNative = native.Capabilities;

            _out.WriteLine($"veldrid: device='{fromVeldrid.DeviceName}' msaa={fromVeldrid.MaxMsaaSampleCount} "
                + $"shadows={fromVeldrid.SupportsShadowMaps} aniso={fromVeldrid.SamplerAnisotropy} "
                + $"fences={fromVeldrid.SupportsCompletionFences}");
            _out.WriteLine($"native:  device='{fromNative.DeviceName}' msaa={fromNative.MaxMsaaSampleCount} "
                + $"shadows={fromNative.SupportsShadowMaps} aniso={fromNative.SamplerAnisotropy} "
                + $"fences={fromNative.SupportsCompletionFences}");

            Assert.Empty(Differences(fromVeldrid, fromNative));

            // Both halves of the member that made V-G1 stricter than the Direct3D 11 backend's bar, asserted
            // rather than left to the emptiness above: equal and FALSE would satisfy the comparison and would
            // mean the deferred-destruction path had quietly lost its fence on both backends at once.
            Assert.True(fromVeldrid.SupportsCompletionFences);
            Assert.True(fromNative.SupportsCompletionFences);
        }

        // Which members of the two sets disagree, by name and in declaration order. Names rather than a bool so a
        // failure says WHICH member moved.
        static IReadOnlyList<string> Differences(in GpuCapabilities a, in GpuCapabilities b)
        {
            var differences = new List<string>();
            if (a.ClipSpaceYInverted != b.ClipSpaceYInverted) differences.Add(nameof(GpuCapabilities.ClipSpaceYInverted));
            if (a.DepthRangeZeroToOne != b.DepthRangeZeroToOne) differences.Add(nameof(GpuCapabilities.DepthRangeZeroToOne));
            if (!string.Equals(a.DeviceName, b.DeviceName, StringComparison.Ordinal)) differences.Add(nameof(GpuCapabilities.DeviceName));
            if (a.SamplerAnisotropy != b.SamplerAnisotropy) differences.Add(nameof(GpuCapabilities.SamplerAnisotropy));
            if (a.SamplerLodBias != b.SamplerLodBias) differences.Add(nameof(GpuCapabilities.SamplerLodBias));
            if (a.MaxMsaaSampleCount != b.MaxMsaaSampleCount) differences.Add(nameof(GpuCapabilities.MaxMsaaSampleCount));
            if (a.SupportsShadowMaps != b.SupportsShadowMaps) differences.Add(nameof(GpuCapabilities.SupportsShadowMaps));
            if (a.SupportsCompute != b.SupportsCompute) differences.Add(nameof(GpuCapabilities.SupportsCompute));
            if (a.SupportsCompletionFences != b.SupportsCompletionFences) differences.Add(nameof(GpuCapabilities.SupportsCompletionFences));
            return differences;
        }

        // Two capability sets that disagree about EVERYTHING, so Differences names every member it knows and the
        // reflection guard above can compare that set against the struct's real one.
        static GpuCapabilities Everything(bool value) => new(
            clipSpaceYInverted: value,
            depthRangeZeroToOne: value,
            deviceName: value ? "a" : "b",
            samplerAnisotropy: value,
            samplerLodBias: value,
            maxMsaaSampleCount: value ? 8 : 1,
            supportsShadowMaps: value,
            supportsCompute: value,
            supportsCompletionFences: value);
    }
}
