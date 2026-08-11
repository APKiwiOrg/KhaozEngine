using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-G1: the native Metal backend's <see cref="GpuCapabilities"/> and the incumbent Metal backend's,
    /// compared member by member, with ZERO PERMITTED DIFFERENCES (section 14 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>).
    ///
    /// <para><b>ZERO, WHERE THE DIRECT3D 11 SIBLING HAD TO PERMIT ONE,</b> and for the reason the Vulkan sibling
    /// also gets to say zero. That backend exempts <see cref="GpuCapabilities.SupportsCompletionFences"/> because
    /// Veldrid's Direct3D 11 fence is a CPU-side submit receipt and the native one is real, so the incumbent's
    /// answer there is a defect the native backend corrects. There is no such defect here:
    /// <c>VeldridMap.SupportsCompletionFences</c> already answers true for <c>GraphicsBackend.Metal</c>, because
    /// the incumbent sets its fence from a command-buffer completion handler. So a difference this test finds is a
    /// bug in <see cref="MetalCapabilityRead"/> until proven otherwise, rather than a member to add to an
    /// exemption list.</para>
    ///
    /// <para><b>THE SPLIT.</b> Everything that DECIDES a capability is engine logic in
    /// <see cref="MetalCapabilityRead"/>: seven constants, the device-name pass-through and M-C3's sample-count
    /// walk. Those are plain <c>[Fact]</c>s here, on a machine with no Metal at all, driven off values written by
    /// hand. So is the COMPARER, plus the reflection check that it covers every member of
    /// <see cref="GpuCapabilities"/>, which is the guard that matters most: a member appended to that struct
    /// without a line added to the comparison would make the parity assertion silently weaker while staying
    /// green. The two-device half is a <c>[GpuFact]</c> and RUNS, on this developer machine and on the hosted
    /// <c>macos-26</c> leg, which is the difference from the Vulkan sibling's equivalent: that one landed dormant
    /// because no leg had a Vulkan device, and this one had a real second device to compare against on the day it
    /// was written.</para>
    ///
    /// <para><b>WHY THE MSAA MEMBER IS THE ONE TO CARE ABOUT.</b> A different answer there silently changes what
    /// <c>AntiAliasing.ResolveFor</c> clamps to, which changes the field look and the golden output. It would not
    /// throw and it would not log, and the <c>scene3d_hdr_msaa</c> golden is baked under the incumbent's answer.
    /// This assertion is the only thing that would notice. It is also the member that made this row build M-C3's
    /// walk rather than leave the capability pinned at 1: the incumbent reports 4 on an Apple M2 Max, so a pin
    /// would have been a parity FAILURE on the first machine the row ran on rather than a conservative
    /// placeholder.</para>
    ///
    /// <para><b>ITS OWN COMPARER, NOT THE OTHER TWO FILES'.</b> The duplication is deliberate and is the shape
    /// phase 3 settled on: the three comparers do not have to agree with each other, they each have to agree with
    /// <see cref="GpuCapabilities"/>, and each file carries its own reflection guard saying so. A member appended
    /// to the struct then fails all three, which is the loud outcome, and no backend's assertion can be weakened
    /// by an edit made for another one's sake.</para>
    ///
    /// <para><b>THE WHOLE CLASS SITS IN THE <c>NativeDeviceLifecycle</c> COLLECTION,</b> which costs the
    /// device-free rows nothing measurable and is what the two-device row needs. See the collection's own
    /// definition for why.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class NativeVsVeldridMetalCapabilityParityTests
    {
        readonly ITestOutputHelper _out;
        public NativeVsVeldridMetalCapabilityParityTests(ITestOutputHelper o) => _out = o;

        // ---------------------------------------------------------------------------------------------------
        // The device-free half: the capability assembly, from probed inputs, with no Metal anywhere.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>Section 14's table, member by member. Seven of the nine are constants of what the incumbent
        /// answers on Metal, and they are asserted BY VALUE here rather than by reading the constant back, so a
        /// change to one of them fails this test rather than agreeing with itself.</summary>
        [Fact]
        public void Assemble_ProducesTheTableInSection14()
        {
            GpuCapabilities caps = MetalCapabilityRead.Assemble("Apple M2 Max", maxMsaaSampleCount: 4);

            Assert.False(caps.ClipSpaceYInverted);
            Assert.True(caps.DepthRangeZeroToOne);
            Assert.Equal("Apple M2 Max", caps.DeviceName);
            Assert.True(caps.SamplerAnisotropy);
            Assert.False(caps.SamplerLodBias);
            Assert.Equal(4, caps.MaxMsaaSampleCount);
            Assert.True(caps.SupportsShadowMaps);
            Assert.True(caps.SupportsCompute);
            Assert.True(caps.SupportsCompletionFences);
        }

        /// <summary>The sample count is a PARAMETER rather than a constant, because the device owns that answer
        /// and the capability must not be able to disagree with the walk.</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        public void Assemble_TakesTheSampleCountFromTheCaller(int samples)
            => Assert.Equal(samples, MetalCapabilityRead.Assemble("d", samples).MaxMsaaSampleCount);

        /// <summary>
        /// THE NAME THE SEAM CARRIES IS THE DEVICE'S OWN, VERBATIM. The incumbent stores <c>_device.name</c> as it
        /// comes and hands it back unchanged, and <see cref="GpuCapabilities.DeviceName"/> is compared string for
        /// string by the assertion below, so any normalisation on the native path alone is a capability difference
        /// on exactly the devices that need normalising. WHITESPACE SURVIVES, which the padded case pins
        /// deliberately, for the reason neither sibling backend trims either. Apple's own names do not pad today,
        /// which is precisely why a trim would ship green here and fail on somebody else's machine.
        /// </summary>
        [Theory]
        [InlineData("Apple M2 Max", "Apple M2 Max")]
        [InlineData("Apple M2 Max  ", "Apple M2 Max  ")]
        [InlineData("  AMD Radeon Pro 5500M", "  AMD Radeon Pro 5500M")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void ReportedDeviceName_IsTheDevicesOwnStringAndIsNeverTrimmed(string? raw, string expected)
        {
            Assert.Equal(expected, MetalCapabilityRead.ReportedDeviceName(raw));
            Assert.Equal(expected, MetalCapabilityRead.Assemble(raw, 1).DeviceName);
        }

        /// <summary>
        /// M-C3's WALK IS DOWNWARD, and this is the row that says why it may not be upward. The counts a device
        /// supports are not required to be contiguous, so a device answering yes to 4 and 16 and no to 8 stops an
        /// upward walk at 4 and under-reports the machine by a factor of four. The incumbent walks its
        /// <c>_supportedSampleCounts</c> array from the top for the same reason, so this is a reproduction rather
        /// than a preference.
        /// </summary>
        [Fact]
        public void HighestSupportedSampleCount_TakesTheTrueMaximumAcrossAHoledSupportSet()
        {
            var supported = new HashSet<int> { 1, 2, 4, 16 };
            Assert.Equal(16, MetalCapabilityRead.HighestSupportedSampleCount(supported.Contains));
        }

        /// <summary>The shapes a real device produces: Apple silicon answers up to 4, and an older or stranger
        /// device may answer only 1. Both come out of the same walk.</summary>
        [Theory]
        [InlineData(new[] { 1 }, 1)]
        [InlineData(new[] { 1, 2 }, 2)]
        [InlineData(new[] { 1, 2, 4 }, 4)]
        [InlineData(new[] { 1, 2, 4, 8 }, 8)]
        [InlineData(new[] { 1, 2, 4, 8, 16, 32 }, 32)]
        public void HighestSupportedSampleCount_ReadsTheTopOfARealSupportSet(int[] supported, int expected)
        {
            var set = new HashSet<int>(supported);
            Assert.Equal(expected, MetalCapabilityRead.HighestSupportedSampleCount(set.Contains));
        }

        /// <summary>
        /// A DEVICE THAT ANSWERS NO TO EVERYTHING REPORTS ONE SAMPLE, which is the incumbent's own floor: its walk
        /// ends in <c>return TextureSampleCount.Count1</c> after finding nothing. Unreachable in practice, because
        /// the machine probe already refuses a device that answers no to
        /// <c>-supportsTextureSampleCount:1</c>, and asserted anyway because the fallback is what a reader has to
        /// trust when reading the walk.
        /// </summary>
        [Fact]
        public void HighestSupportedSampleCount_FallsBackToOneSample()
        {
            Assert.Equal(1, MetalCapabilityRead.HighestSupportedSampleCount(_ => false));
            Assert.Equal(MetalCapabilityRead.NoMultisampling,
                MetalCapabilityRead.HighestSupportedSampleCount(_ => false));
        }

        /// <summary>The walk asks each count ONCE and stops at the first yes, which is what makes it six selector
        /// sends at worst and one at best on the machines that matter. Asserted because a walk that kept going
        /// would still return the right number and would cost every device creation five pointless sends.</summary>
        [Fact]
        public void HighestSupportedSampleCount_StopsAtTheFirstYes()
        {
            var asked = new List<int>();
            int answer = MetalCapabilityRead.HighestSupportedSampleCount(count =>
            {
                asked.Add(count);
                return count == 8;
            });

            Assert.Equal(8, answer);
            Assert.Equal(new[] { 32, 16, 8 }, asked);
        }

        /// <summary>
        /// THE SHADOW-MAP MEMBER IS A CONSTANT ON THIS BACKEND, AND THAT IS THE ROW'S ONE CORRECTION TO SECTION
        /// 14's TABLE. The table calls the native source "the incumbent's own question", which on the Vulkan
        /// sibling is a real <c>vkGetPhysicalDeviceFormatProperties</c> read. Asking the incumbent that question
        /// on METAL has no device call in it at all: <c>VeldridMap.SupportsShadowMaps</c> calls
        /// <c>GetPixelFormatSupport</c>, <c>MTLGraphicsDevice.GetPixelFormatSupportCore</c> asks
        /// <c>MTLFormats.IsFormatSupported</c>, whose switch has no <c>R32_Float</c> case and falls to
        /// <c>default: return true</c>, and the rest of that method only fills a properties struct before
        /// returning true for a 2D texture. So the incumbent answers TRUE on every Metal device that exists, and
        /// reproducing the question faithfully means reproducing the constant. Asking Metal a REAL question here
        /// could only produce a difference, which on this member is not a red leg but the shadow path degrading to
        /// blob shadows on one backend with nothing reported.
        /// </summary>
        [Fact]
        public void TheShadowMapQuestionIsAConstantOnMetalRatherThanADeviceRead()
        {
            Assert.True(MetalCapabilityRead.SupportsShadowMaps);
            Assert.True(MetalCapabilityRead.Assemble("d", 1).SupportsShadowMaps);
            Assert.True(MetalCapabilityRead.Assemble("", 32).SupportsShadowMaps);
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

        /// <summary>Two identical capability sets report NOTHING, which is the shape M-G1 asserts against the two
        /// real devices. Asserted separately from the completeness guard above, because a comparer that named
        /// every member unconditionally would pass that one.</summary>
        [Fact]
        public void TheComparerReportsNothingWhenTheTwoSetsAgree()
        {
            GpuCapabilities caps = MetalCapabilityRead.Assemble("Apple M2 Max", 4);

            Assert.Empty(Differences(caps, caps));
        }

        /// <summary>And it names the one member that moved, rather than answering a bool. On
        /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/> that is the difference between a one-line fix and a
        /// golden investigation, and it is the member most likely to be the one that moves here.</summary>
        [Fact]
        public void TheComparerNamesTheMemberThatMoved()
        {
            GpuCapabilities incumbent = MetalCapabilityRead.Assemble("Apple M2 Max", 4);
            GpuCapabilities native = MetalCapabilityRead.Assemble("Apple M2 Max", 1);

            Assert.Equal(new[] { nameof(GpuCapabilities.MaxMsaaSampleCount) }, Differences(incumbent, native));
        }

        // ---------------------------------------------------------------------------------------------------
        // The two-device half. It RUNS wherever a Metal device exists.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// M-G1 ITSELF: both devices in one process, both capability sets read, every member compared, and NOTHING
        /// permitted to differ.
        /// <para>
        /// BOTH devices come up through <see cref="GpuDeviceContext"/>, the native one by naming its kind to
        /// <see cref="GpuDeviceContext.CreateHeadless(GpuBackendKind)"/>. Two backends in one process is what that
        /// overload was made public for, and a context reports its adopted device's own
        /// <see cref="GpuCapabilities"/> verbatim, so the route changes nothing about what is compared.
        /// </para>
        /// <para>
        /// Returns early, having asserted nothing, in THREE cases, and all three are facts about the MACHINE
        /// rather than about the code: not macOS at all, a machine the native backend's own functional probe
        /// refuses, and an incumbent that did not come up on Metal, where there is nothing to be at parity WITH.
        /// The Linux and Windows legs take the first, which is why it is a dormant return rather than a skip: the
        /// zero-skipped gate under <c>KE_GPU_TESTS=1</c> would read a skip as a failure.
        /// </para>
        /// <para>
        /// WHEN <see cref="GpuCapabilities.DeviceName"/> IS THE ONLY MEMBER THAT DIFFERS, check
        /// <c>KE_METAL_DEVICE</c> first. That pin selects a device for the NATIVE backend alone while the
        /// incumbent takes <c>MTLCreateSystemDefaultDevice()</c> unconditionally, so on a multi-GPU Mac a pin puts
        /// the two backends on DIFFERENT devices, and the parity failure that produces is a machine fact wearing
        /// the shape of a bug in the read. CI pins nothing at all (M-G2), because a hosted <c>macos-26</c> runner
        /// has one device and no accident available.
        /// </para>
        /// </summary>
        [GpuFact]
        public void NativeAndVeldridMetalCapabilitiesDoNotDifferAtAll()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            using GpuDeviceContext incumbent = GpuDeviceContext.CreateHeadless();
            if (incumbent.Backend != GpuBackendKind.Metal)
            {
                _out.WriteLine($"dormant: the incumbent device came up on {incumbent.Backend}, not Metal.");
                return;
            }

            using GpuDeviceContext native = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);

            // THE NATIVE SIDE IS THE NATIVE BACKEND, asserted rather than assumed from the kind that was asked
            // for. A selection that quietly fell back to the incumbent would compare Veldrid against Veldrid and
            // report zero differences for the one reason that means nothing, which is the failure a parity row
            // cannot see from its own output.
            Assert.Equal(GpuBackendKind.MetalNative, native.Backend);
            Assert.IsType<MetalGpuDevice>(native.GpuDevice);

            GpuCapabilities fromVeldrid = incumbent.Capabilities;
            GpuCapabilities fromNative = native.Capabilities;

            _out.WriteLine($"veldrid: device='{fromVeldrid.DeviceName}' msaa={fromVeldrid.MaxMsaaSampleCount} "
                + $"shadows={fromVeldrid.SupportsShadowMaps} aniso={fromVeldrid.SamplerAnisotropy} "
                + $"lodBias={fromVeldrid.SamplerLodBias} fences={fromVeldrid.SupportsCompletionFences}");
            _out.WriteLine($"native:  device='{fromNative.DeviceName}' msaa={fromNative.MaxMsaaSampleCount} "
                + $"shadows={fromNative.SupportsShadowMaps} aniso={fromNative.SamplerAnisotropy} "
                + $"lodBias={fromNative.SamplerLodBias} fences={fromNative.SupportsCompletionFences}");

            Assert.Empty(Differences(fromVeldrid, fromNative));

            // Both halves of the member that makes M-G1's bar zero rather than one, asserted rather than left to
            // the emptiness above: equal and FALSE would satisfy the comparison and would mean the
            // deferred-destruction path had quietly lost its fence on both backends at once.
            Assert.True(fromVeldrid.SupportsCompletionFences);
            Assert.True(fromNative.SupportsCompletionFences);

            // And the MSAA member is asserted as more than a match, because two devices both answering 1 would
            // compare equal while meaning the walk had found nothing. FOUR rather than one, because one is the
            // walk's own floor and asserting it is asserting nothing: the scene3d_hdr_msaa golden is baked at 4
            // samples, so a Metal leg that answered less would clamp AntiAliasing.ResolveFor below the number the
            // golden was baked under and change the image on both backends at once.
            Assert.True(fromNative.MaxMsaaSampleCount >= 4,
                $"the native Metal device reported {fromNative.MaxMsaaSampleCount} as its highest sample count, "
                + "and every Metal device the engine supports answers at least 4. Below that the sample-count "
                + "walk found nothing rather than the machine being unusual.");
            Assert.Equal(fromVeldrid.MaxMsaaSampleCount, fromNative.MaxMsaaSampleCount);
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

    /// <summary>
    /// THE ONE PLACE THAT ANSWERS "can this machine build a native Metal device", for the rows that need a SECOND
    /// device beside the suite's own. The lifecycle rows each carry their own inline copy of this pair because
    /// they hold an <c>ITestOutputHelper</c> field and answer through a
    /// <c>[SupportedOSPlatformGuard]</c> method, and this is the shared form for the rows that do not.
    /// <para>
    /// IT IS A DORMANT RETURN RATHER THAN A SKIP, which is phase 3's row-19 lesson: under <c>KE_GPU_TESTS=1</c>
    /// the Windows and Linux legs run this assembly in strict mode where a skip is a failure, so a row that has no
    /// device to talk to records the reason and asserts nothing.
    /// </para>
    /// </summary>
    static class MetalDormancy
    {
        /// <summary>True when a native Metal device can be created here. Writes the reason it cannot to
        /// <paramref name="output"/>, so a dormant run says which of the two machine facts it hit.
        /// <para>
        /// A <c>[SupportedOSPlatformGuard]</c> rather than a plain bool, and it is honest: the first thing this
        /// asks is <c>KhaozEngineMetal.IsPlatformSupported</c>, so a true answer really does imply macOS. That is
        /// what lets a caller read a macOS-only member after it without CA1416 firing, which the lifecycle rows
        /// already do through their own inline copy of this pair.
        /// </para>
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
        internal static bool NativeDeviceAvailable(ITestOutputHelper output)
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                output.WriteLine("dormant: not macOS, so there is no native Metal device to compare.");
                return false;
            }

            string? missing = MissingRequirement();
            if (missing is null) return true;

            output.WriteLine("dormant: this machine cannot run the native Metal backend (" + missing + ").");
            return false;
        }

        // Split out under the guard so CA1416 can see that the probe is only ever read on macOS. The caller's own
        // IsPlatformSupported check is what makes that true, and the analyzer reads the guard at the call site.
        [System.Runtime.Versioning.SupportedOSPlatform("macos")]
        static string? MissingRequirement() => MetalSupportProbe.MissingRequirement();
    }
}
