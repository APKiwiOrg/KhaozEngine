using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION T4: the native backend's <see cref="GpuCapabilities"/> and the incumbent's, compared field by
    /// field, with <see cref="GpuCapabilities.SupportsCompletionFences"/> asserted as the ONE permitted
    /// difference (decisions G1 and C5, section 11 of the design doc).
    ///
    /// <para><b>THE SPLIT, AND THE DORMANCY THAT ENDED.</b> This file is two halves, and the second one went
    /// live with the device row (https://github.com/APKiwiOrg/KhaozEngine/issues/497):
    /// <list type="bullet">
    ///   <item><description><b>The device-free half runs everywhere.</b> Everything that DECIDES a
    ///   capability from a probed input is engine logic in <see cref="D3D11CapabilityRead"/>: the five constants,
    ///   the descending sample-count walk, the min-over-three-formats fold, the adapter-name NUL cut, and the
    ///   out-of-range sample-count guard. Those are plain <c>[Fact]</c>s here, on macOS and Linux, driven off
    ///   fakes. So is the COMPARER itself, plus a reflection check that it covers every member of
    ///   <see cref="GpuCapabilities"/>, which is the guard that matters most: a member appended to that struct
    ///   without being added to the comparison would otherwise make the parity assertion silently weaker.</description></item>
    ///   <item><description><b>The two-device half is a <c>[GpuFact]</c> and it now CREATES BOTH DEVICES.</b> It
    ///   landed dormant, keyed to the <see cref="NotSupportedException"/> the unbuilt provider raised, and that
    ///   key is gone: creation is real, so a native device that refuses to be created is a failure rather than a
    ///   reason to skip. The two early returns left are facts about the MACHINE (not Windows, or an incumbent
    ///   that did not come up on Direct3D11), and they are what make it a parity test rather than a Windows-only
    ///   one.</description></item>
    /// </list></para>
    ///
    /// <para><b>Why the MSAA member is the one to care about.</b> A different answer there silently changes what
    /// <c>AntiAliasing.ResolveFor</c> picks, which changes the field look and the golden output. It would not
    /// throw and it would not log. The parity assertion is the only thing that would notice.</para>
    ///
    /// <para><b>THE WHOLE CLASS SITS IN THE <c>NativeDeviceLifecycle</c> COLLECTION,</b> which costs the
    /// device-free rows above nothing measurable and is what the two-device row needs. See the collection's own
    /// definition for why.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class NativeVsVeldridCapabilityParityTests
    {
        readonly ITestOutputHelper _out;
        public NativeVsVeldridCapabilityParityTests(ITestOutputHelper o) => _out = o;

        // ---------------------------------------------------------------------------------------------------
        // The device-free half: the capability assembly, from probed inputs, against fakes.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>Section 11's table, member by member. Five of the nine are constants of the feature levels
        /// this backend requires, and they are asserted by VALUE here rather than by reading the constant back,
        /// so a change to one of them fails this test rather than agreeing with itself.</summary>
        [Fact]
        public void Assemble_ProducesTheTableInSection11()
        {
            GpuCapabilities caps = D3D11CapabilityRead.Assemble(
                "NVIDIA GeForce RTX 4070", maxMsaaSampleCount: 8,
                supportsShadowMaps: true, supportsCompletionFences: true);

            Assert.False(caps.ClipSpaceYInverted);
            Assert.True(caps.DepthRangeZeroToOne);
            Assert.Equal("NVIDIA GeForce RTX 4070", caps.DeviceName);
            Assert.True(caps.SamplerAnisotropy);
            Assert.True(caps.SamplerLodBias);
            Assert.Equal(8, caps.MaxMsaaSampleCount);
            Assert.True(caps.SupportsShadowMaps);
            Assert.True(caps.SupportsCompute);
            Assert.True(caps.SupportsCompletionFences);
        }

        /// <summary>Completion fences are a PARAMETER rather than a constant, because the fence subsystem owns the
        /// answer and the capability must not be able to disagree with the fence path.</summary>
        [Fact]
        public void Assemble_TakesCompletionFencesFromTheCaller()
        {
            Assert.False(D3D11CapabilityRead.Assemble("x", 1, true, supportsCompletionFences: false)
                .SupportsCompletionFences);
            Assert.True(D3D11CapabilityRead.Assemble("x", 1, true, supportsCompletionFences: true)
                .SupportsCompletionFences);
        }

        /// <summary>A null adapter name never reaches the seam as null: <see cref="GpuCapabilities.DeviceName"/>
        /// is documented as empty when the backend reports nothing.</summary>
        [Fact]
        public void Assemble_NeverProducesANullDeviceName()
        {
            Assert.Equal("", D3D11CapabilityRead.Assemble(null!, 1, true, true).DeviceName);
        }

        /// <summary><c>DXGI_ADAPTER_DESC::Description</c> is a fixed 128-wide-char buffer, so cutting at the first
        /// NUL is what "trailing nulls trimmed" means for it, and that cut is the whole of what this does.
        /// WHITESPACE SURVIVES, which the two padded cases below pin deliberately: at least one vendor pads its
        /// description with a space, the incumbent reports <c>desc.Description</c> raw, and
        /// <see cref="GpuCapabilities.DeviceName"/> is compared string for string by the parity assertion, so
        /// trimming here alone would fail parity on exactly those machines. A trim is allowed later only if both
        /// paths and that assertion move together.</summary>
        [Theory]
        [InlineData("Microsoft Basic Render Driver\0\0\0\0", "Microsoft Basic Render Driver")]
        [InlineData("Radeon RX 7900 XT  ", "Radeon RX 7900 XT  ")]
        [InlineData("  Intel(R) UHD Graphics\0junk", "  Intel(R) UHD Graphics")]
        [InlineData("\0\0\0", "")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("Apple M2", "Apple M2")]
        public void TrimAdapterName_ReadsTheDescriptionAsACStringAndKeepsWhatIsInsideIt(string? raw, string expected)
        {
            Assert.Equal(expected, D3D11CapabilityRead.TrimAdapterName(raw));
        }

        /// <summary>The walk is DOWNWARD from 32, because the supported counts are not required to be contiguous.
        /// A driver that supports 4x and 16x but not 8x would stop an upward walk at 4 and under-report the
        /// device, which on this member means a menu that silently offers less MSAA than the card has.</summary>
        [Fact]
        public void HighestSupportedSampleCount_TakesTheTrueMaximumAcrossAHoledSupportSet()
        {
            var supported = new HashSet<int> { 2, 4, 16 };
            Assert.Equal(16, D3D11CapabilityRead.HighestSupportedSampleCount(c => supported.Contains(c) ? 1 : 0));
        }

        /// <summary>Zero quality levels is Direct3D's "not supported", so a device that answers zero for every
        /// count reports no multisampling rather than the count the walk started at.</summary>
        [Fact]
        public void HighestSupportedSampleCount_FallsToOneWhenNothingIsSupported()
        {
            Assert.Equal(1, D3D11CapabilityRead.HighestSupportedSampleCount(_ => 0));
        }

        /// <summary>The walk never asks about 1: a single sample is not multisampling and Direct3D always
        /// supports it, so a query for it would be a call whose answer cannot change the result.</summary>
        [Fact]
        public void HighestSupportedSampleCount_NeverQueriesTheSingleSampleCase()
        {
            var asked = new List<int>();
            D3D11CapabilityRead.HighestSupportedSampleCount(c => { asked.Add(c); return 0; });

            Assert.Equal(new[] { 32, 16, 8, 4, 2 }, asked);
        }

        /// <summary>Decision C4's fold. Every attachment of a framebuffer must support the count, so the answer is
        /// the MIN over the three formats the 3D scene's MRT renders into.</summary>
        [Theory]
        [InlineData(8, 8, 8, 8)]
        [InlineData(8, 4, 8, 4)]
        [InlineData(2, 8, 8, 2)]
        [InlineData(8, 8, 1, 1)]
        public void MinOverFormats_TakesTheLowestOfTheThreeMrtFormats(int colour, int depthColour, int depth, int expected)
        {
            Assert.Equal(expected, D3D11CapabilityRead.MinOverFormats(colour, depthColour, depth));
        }

        /// <summary>Section 11 says any query failure yields 1, and the Windows caller signals a failure by
        /// answering 1 or below. Nothing below 1 can escape the fold, because a sample count of 0 handed to
        /// Direct3D is an invalid argument rather than "no MSAA".</summary>
        [Theory]
        [InlineData(0, 8, 8)]
        [InlineData(-1, 8, 8)]
        [InlineData(8, 8, 0)]
        public void MinOverFormats_FoldsAnyFailedQueryToOne(int colour, int depthColour, int depth)
        {
            Assert.Equal(1, D3D11CapabilityRead.MinOverFormats(colour, depthColour, depth));
        }

        /// <summary>Decision C4's throw: an out-of-range request is a fault rather than something to round down,
        /// because the engine already has the one place a request is meant to be clamped and a count arriving
        /// above the maximum came from a caller that skipped it.</summary>
        [Fact]
        public void UnsupportedSampleCountMessage_RefusesACountAboveTheDeviceMaximum()
        {
            string? message = D3D11CapabilityRead.UnsupportedSampleCountMessage(requested: 8, maxSupported: 4);

            Assert.NotNull(message);
            Assert.Contains("8", message, StringComparison.Ordinal);
            Assert.Contains("4", message, StringComparison.Ordinal);
            Assert.Contains("AntiAliasing.ResolveFor", message, StringComparison.Ordinal);
        }

        /// <summary>A count at or below the maximum passes, and so does 1 whatever the maximum says: 1 is not
        /// multisampling at all, and <c>GpuTextureDescription</c> already normalises 0 to it.</summary>
        [Theory]
        [InlineData(1u, 1)]
        [InlineData(1u, 8)]
        [InlineData(4u, 4)]
        [InlineData(2u, 8)]
        [InlineData(1u, 0)]
        public void UnsupportedSampleCountMessage_PassesEveryCountTheDeviceCanDo(uint requested, int max)
        {
            Assert.Null(D3D11CapabilityRead.UnsupportedSampleCountMessage(requested, max));
        }

        // ---------------------------------------------------------------------------------------------------
        // The comparer, and the guard that keeps it complete.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// THE GUARD THAT MAKES THE PARITY ASSERTION MEAN ANYTHING. <see cref="Differences"/> is a hand-written
        /// member-by-member comparison, and a member appended to <see cref="GpuCapabilities"/> without a line
        /// added there would make every parity check silently weaker while staying green. Reflection over the
        /// struct's public properties is what catches that, and it is why the comparer returns NAMES rather than
        /// a bool.
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

        /// <summary>Two capability sets that differ ONLY in completion fences report exactly that, which is the
        /// shape decision T4 asserts against the two real devices.</summary>
        [Fact]
        public void TheComparerReportsCompletionFencesAloneWhenThatIsTheOnlyDifference()
        {
            GpuCapabilities veldrid = D3D11CapabilityRead.Assemble("WARP", 8, true, supportsCompletionFences: false);
            GpuCapabilities native = D3D11CapabilityRead.Assemble("WARP", 8, true, supportsCompletionFences: true);

            Assert.Equal(new[] { nameof(GpuCapabilities.SupportsCompletionFences) }, Differences(veldrid, native));
        }

        // ---------------------------------------------------------------------------------------------------
        // The shared sampler pair, against the incumbent's built-ins. Device-free, so it runs on every OS.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// THE SHARED PAIR IS SEAM-CONTRACT WRAP, AND IT MUST TRACK THE INCUMBENT BYTE FOR BYTE UNTIL PHASE 3
        /// RETIRES IT. <see cref="IGpuDevice.PointSampler"/> and <see cref="IGpuDevice.LinearSampler"/> are the
        /// samplers most of the engine renders through, the incumbent builds them from
        /// <c>Veldrid.SamplerDescription.Point</c> and <c>.Linear</c>, and while both backends exist a difference
        /// between the two pairs is a difference in every scene that samples past the edge of a texture.
        /// <para>
        /// THE FIELD THIS EXISTS FOR IS THE ADDRESS MODE. The native device used to build its pair from the
        /// engine's <see cref="GpuSamplerDescription.Point"/> / <see cref="GpuSamplerDescription.Linear"/>
        /// statics, whose ctor defaults every axis to <see cref="GpuSamplerAddress.Clamp"/>: the same names, the
        /// opposite behaviour, and nothing to notice it. CI run 30963173087 noticed it as
        /// <c>scene3d_texbillboard</c> (worst 0.393) and <c>scene3d_particles_flipbook</c> (worst 0.359).
        /// </para>
        /// <para>
        /// The last four assertions are the values the seam does not expose at all, so they cannot be compared
        /// through a <see cref="GpuSamplerDescription"/>: they are asserted against the incumbent directly, and
        /// <c>D3D11Sampler</c> hardcodes exactly these four (decision G1).
        /// </para>
        /// </summary>
        [Fact]
        public void TheSharedSamplerPairMirrorsTheIncumbentsBuiltIns()
        {
            AssertMirrorsTheIncumbent(D3D11SharedSamplers.Point, Veldrid.SamplerDescription.Point,
                GpuSamplerFilter.MinPointMagPointMipPoint, Veldrid.SamplerFilter.MinPoint_MagPoint_MipPoint);
            AssertMirrorsTheIncumbent(D3D11SharedSamplers.Linear, Veldrid.SamplerDescription.Linear,
                GpuSamplerFilter.MinLinearMagLinearMipLinear, Veldrid.SamplerFilter.MinLinear_MagLinear_MipLinear);
        }

        static void AssertMirrorsTheIncumbent(in GpuSamplerDescription ours, in Veldrid.SamplerDescription theirs,
            GpuSamplerFilter ourFilter, Veldrid.SamplerFilter theirFilter)
        {
            // Address modes, all three axes, in both directions: the incumbent really is wrap, and ours really is
            // the same wrap. Asserting only the mapping would pass on a day the incumbent changed too.
            Assert.Equal(Veldrid.SamplerAddressMode.Wrap, theirs.AddressModeU);
            Assert.Equal(Veldrid.SamplerAddressMode.Wrap, theirs.AddressModeV);
            Assert.Equal(Veldrid.SamplerAddressMode.Wrap, theirs.AddressModeW);
            Assert.Equal(Mapped(theirs.AddressModeU), ours.AddressModeU);
            Assert.Equal(Mapped(theirs.AddressModeV), ours.AddressModeV);
            Assert.Equal(Mapped(theirs.AddressModeW), ours.AddressModeW);

            // Filter, and the engine kind it maps to.
            Assert.Equal(theirFilter, theirs.Filter);
            Assert.Equal(ourFilter, ours.Filter);

            // The two remaining fields the seam DOES expose.
            Assert.Equal(theirs.MaximumAnisotropy, ours.MaximumAnisotropy);
            Assert.Equal(theirs.LodBias, ours.MipLodBias);

            // And the four the seam does not, which D3D11Sampler hardcodes to exactly these.
            Assert.Equal(0u, theirs.MinimumLod);
            Assert.Equal(uint.MaxValue, theirs.MaximumLod);
            Assert.Null(theirs.ComparisonKind);
            Assert.Equal(Veldrid.SamplerBorderColor.TransparentBlack, theirs.BorderColor);
        }

        static GpuSamplerAddress Mapped(Veldrid.SamplerAddressMode mode) => mode switch
        {
            Veldrid.SamplerAddressMode.Wrap => GpuSamplerAddress.Wrap,
            Veldrid.SamplerAddressMode.Mirror => GpuSamplerAddress.Mirror,
            Veldrid.SamplerAddressMode.Clamp => GpuSamplerAddress.Clamp,
            _ => GpuSamplerAddress.Border,
        };

        // ---------------------------------------------------------------------------------------------------
        // The two-device half. Live since the device row.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// DECISION T4 ITSELF: both devices in one process, both capability sets read, every member compared, and
        /// completion fences asserted as the only difference.
        /// <para>
        /// BOTH devices come up through <see cref="GpuDeviceContext"/>, the native one by naming its kind to
        /// <see cref="GpuDeviceContext.CreateHeadless(GpuBackendKind)"/>. Two backends in one process is what that
        /// overload was made public for, and it is what keeps the second device inside the process-wide creation
        /// gate: reaching into <c>GpuBackendProviders</c> and calling the provider's own <c>CreateHeadless</c>
        /// creates a device around the outside of the gate every other device in the run went through. The
        /// capability set is unchanged by the move, since a context reports its adopted device's own
        /// <see cref="GpuCapabilities"/> verbatim.
        /// </para>
        /// <para>
        /// Returns early, having asserted nothing, in THREE cases, and every one is a fact about the MACHINE
        /// rather than about the code: not Windows, an incumbent that did not come up on Direct3D11 so there is
        /// nothing to be at parity WITH, or a Windows box that cannot run the native backend at all. The last one
        /// is https://github.com/APKiwiOrg/KhaozEngine/issues/504, and it reads the package's own functional probe
        /// through <see cref="GpuBackendSelector.IsBackendSupported"/>, which checks
        /// <c>ConstantBufferOffsetting</c> and <c>MapNoOverwriteOnDynamicConstantBuffer</c> (decision I2's
        /// machine-incapability arm). WARP on the CI leg satisfies both, so nothing in CI changes. What changes is
        /// a feature-deficient Windows box running the suite by hand, which used to go red for a machine fact.
        /// </para>
        /// <para>
        /// The early return that is GONE is a different one: it caught the <see cref="NotSupportedException"/> the
        /// provider raised while creation was unbuilt, and creation is built, so a native device that refuses to
        /// create on a machine the probe just called capable is a failure of exactly the kind this test exists to
        /// report.
        /// </para>
        /// </summary>
        [GpuFact]
        public void NativeAndVeldridCapabilitiesDifferInCompletionFencesAndNothingElse()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported)
            {
                _out.WriteLine("dormant: not Windows, so there is no native Direct3D 11 device to compare.");
                return;
            }

            using GpuDeviceContext incumbent = GpuDeviceContext.CreateHeadless();
            if (incumbent.Backend != GpuBackendKind.Direct3D11)
            {
                _out.WriteLine($"dormant: the incumbent device came up on {incumbent.Backend}, not Direct3D11.");
                return;
            }

            if (!GpuBackendSelector.IsBackendSupported(GpuBackendKind.Direct3D11Native))
            {
                _out.WriteLine("dormant: this Windows machine cannot run the native Direct3D 11 backend, so "
                    + "there is no second device to compare.");
                return;
            }

            using GpuDeviceContext native = GpuDeviceContext.CreateHeadless(GpuBackendKind.Direct3D11Native);
            GpuCapabilities fromVeldrid = incumbent.Capabilities;
            GpuCapabilities fromNative = native.Capabilities;

            _out.WriteLine($"veldrid: adapter='{fromVeldrid.DeviceName}' msaa={fromVeldrid.MaxMsaaSampleCount} "
                + $"shadows={fromVeldrid.SupportsShadowMaps} fences={fromVeldrid.SupportsCompletionFences}");
            _out.WriteLine($"native:  adapter='{fromNative.DeviceName}' msaa={fromNative.MaxMsaaSampleCount} "
                + $"shadows={fromNative.SupportsShadowMaps} fences={fromNative.SupportsCompletionFences}");

            Assert.Equal(new[] { nameof(GpuCapabilities.SupportsCompletionFences) },
                Differences(fromVeldrid, fromNative));
            Assert.False(fromVeldrid.SupportsCompletionFences);
            Assert.True(fromNative.SupportsCompletionFences);
        }

        // Which members of the two sets disagree, by name and in declaration order. Names rather than a bool so a
        // failure says WHICH field moved, which on MaxMsaaSampleCount is the difference between a one-line fix
        // and a golden investigation.
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
