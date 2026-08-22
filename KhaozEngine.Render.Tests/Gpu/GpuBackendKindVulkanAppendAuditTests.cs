using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The APPEND AUDIT for <see cref="GpuBackendKind.VulkanNative"/>, the second one. Section 4.2 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> walks the same thirteen sites
    /// <see cref="GpuBackendKindAppendAuditTests"/> discovered for Direct3D 11, and this file answers them for
    /// Vulkan in the order that table lists them. Device-free, so the whole audit runs under a plain
    /// <c>dotnet test</c> on any OS.
    /// <para>
    /// Why a second file rather than more rows in the first one. The append is a DIFF now, not a discovery
    /// (decision V-I2), and the interesting content is the four rows where the two appends answer DIFFERENTLY:
    /// the driver-threading probe and its log line take no edit at all here, the Veldrid creation switches ride
    /// an arm phase 2 already made explicit, and the default flip means something else entirely because
    /// <see cref="GpuBackendSelector.ProbeOS"/> maps Linux to <see cref="GpuBackendKind.Vulkan"/>. Keeping those
    /// beside a copy of the D3D11 rows would bury them. The rows the two appends genuinely SHARE (the pinned
    /// ordinals, the every-member theories) stay in the first file with a row added, not duplicated here.
    /// </para>
    /// <para>
    /// Rows 3, 6 and 8 are in <see cref="GpuBackendKindVulkanAppendAuditRegistryTests"/> for the reason the
    /// Direct3D 11 audit splits the same three out: they touch the process-wide provider registry under the REAL
    /// kind, so they cannot share the parallel pool.
    /// </para>
    /// </summary>
    public sealed class GpuBackendKindVulkanAppendAuditTests
    {
        // --- the enum itself (decision V-I1). The ordinal is pinned in GpuBackendKindAppendAuditTests, with the
        // other four, because "no member ever moves" is one claim about one enum and splitting it across two
        // files is how half of it stops being checked. ---

        /// <summary>
        /// A separate member from the Veldrid Vulkan kind, which is what makes the telemetry header, the session
        /// log and a golden filename each name the implementation that actually ran.
        /// </summary>
        [Fact]
        public void TheNativeKind_IsDistinctFromTheVeldridOne()
            => Assert.NotEqual(GpuBackendKind.Vulkan, GpuBackendKind.VulkanNative);

        /// <summary>
        /// Both implementations answer the Vulkan family predicate, and nothing else does (decision V-I5). It has
        /// no reader in the engine yet, unlike <c>IsDirect3D11</c>: it exists because the question gets asked at
        /// more than one site as the backend lands, and a copy of it spelled out at each site drifts. Pinned now
        /// so the first site to use it inherits a checked answer rather than a plausible one.
        /// </summary>
        [Fact]
        public void IsVulkan_CoversBothImplementations_AndNothingElse()
        {
            Assert.True(GpuBackendKind.Vulkan.IsVulkan());
            Assert.True(GpuBackendKind.VulkanNative.IsVulkan());
            Assert.False(GpuBackendKind.Metal.IsVulkan());
            Assert.False(GpuBackendKind.Direct3D11.IsVulkan());
            Assert.False(GpuBackendKind.Direct3D11Native.IsVulkan());
            Assert.False(GpuBackendKind.MetalNative.IsVulkan());
            Assert.False(GpuBackendKind.OpenGL.IsVulkan());
        }

        // The predicates never claiming one kind twice was a pairwise assertion here while there were two of
        // them. A third predicate made it a theory over every member against every predicate, which is the first
        // file's half of the split, so it moved to
        // GpuBackendKindAppendAuditTests.TheFamilyPredicates_NeverClaimTheSameKindTwice rather than being copied
        // into a third file with one term added.

        // --- rows 1 and 2: GpuDeviceContext.LogThreadingCaps and D3D11ThreadingProbe.IsApplicable. NO CHANGE,
        // and this is the first of the four rows that answer differently than they did for Direct3D11Native.
        // Both gate on IsDirect3D11, which correctly excludes both Vulkan implementations, and there is no
        // D3D11_FEATURE_DATA_THREADING analogue to log. ThreadingCaps and ThreadingProbeFailure are both null on
        // this backend, which the record already documents as "there was nothing to ask". Pinned by the row added
        // to GpuBackendKindAppendAuditTests.ThreadingProbe_AppliesToBothDirect3D11Implementations, which is one
        // theory over one probe and gains an InlineData rather than a copy. ---

        // --- row 3: the CreateForWindow / CreateHeadless Veldrid switches. In the registry class below. ---

        /// <summary>
        /// The fourteenth site, stated negatively so it needs no edit:
        /// <see cref="GpuBackendProviders.RequiresProvider"/> is "everything the built-in path does not build",
        /// and <c>IsBuiltIn</c> is a positive membership test over the four Veldrid kinds rather than a switch.
        /// So an APPENDED member is provider-backed with no edit at all, and forgetting one throws a message
        /// naming the missing registration instead of routing the new kind into the Veldrid creation switch.
        /// </summary>
        [Fact]
        public void TheNativeKind_IsProviderBacked_WithNoEditToTheRegistry()
        {
            Assert.True(GpuBackendProviders.RequiresProvider(GpuBackendKind.VulkanNative));
            Assert.False(GpuBackendProviders.RequiresProvider(GpuBackendKind.Vulkan));
        }

        // --- row 4: GpuBackendSelector.ToVeldrid, one more explicit throwing arm ---

        /// <summary>
        /// There IS a <c>GraphicsBackend.Vulkan</c>, which is exactly why this arm matters more here than it did
        /// for Direct3D 11. Mapping the native kind onto it would not fail: it would quietly build the INCUMBENT
        /// Veldrid Vulkan device and attribute a whole soak session to the implementation that did not run.
        /// </summary>
        [Fact]
        public void ToVeldrid_ThrowsForTheNativeKind_RatherThanAnsweringTheIncumbent()
        {
            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => GpuBackendSelector.ToVeldrid(GpuBackendKind.VulkanNative));

            Assert.Contains(nameof(GpuBackendKind.VulkanNative), ex.Message);
            Assert.Contains("RequiresProvider", ex.Message);
        }

        // --- row 5: GpuBackendSelector.TryParseBackend, the two new tokens (V-I1) ---

        [Theory]
        [InlineData("vulkan-native")]
        [InlineData("vk-native")]
        [InlineData("Vulkan-Native")]
        [InlineData("  VK-NATIVE\t")]
        public void TryParseBackend_RecognizesBothNativeTokens(string value)
        {
            Assert.True(GpuBackendSelector.TryParseBackend(value, out GpuBackendKind backend));
            Assert.Equal(GpuBackendKind.VulkanNative, backend);
        }

        /// <summary>
        /// The suffix must not bleed either way, and the incumbent token keeps pointing at the incumbent
        /// INDEFINITELY, which is decision V-RO2 rather than a transitional state: <c>vulkan</c> is the kill
        /// switch every structural bet in the Vulkan design leans on, so an A/B against the native backend is one
        /// environment variable away.
        /// </summary>
        [Fact]
        public void TryParseBackend_KeepsTheIncumbentTokenPointingAtTheIncumbent()
        {
            Assert.True(GpuBackendSelector.TryParseBackend("vulkan", out GpuBackendKind backend));
            Assert.Equal(GpuBackendKind.Vulkan, backend);
        }

        [Theory]
        [InlineData("vulkannative")]
        [InlineData("vulkan_native")]
        [InlineData("vknative")]
        [InlineData("vulkan-nativ")]
        [InlineData("vk")]
        public void TryParseBackend_RejectsANearMissRatherThanGuessing(string value)
        {
            Assert.False(GpuBackendSelector.TryParseBackend(value, out _));
            // And an unrecognized value keeps its raw text for the diagnostic, so the tester is told their
            // variable did nothing instead of reading the OS default as the backend they asked for.
            GpuBackendSelection selection = GpuBackendSelector.Resolve(value, OSPlatformKind.Linux);
            Assert.Equal(GpuBackendSource.UnrecognizedOverride, selection.Source);
            Assert.Equal(value, selection.RequestedOverride);
        }

        /// <summary>
        /// The whole point of the token: a field soak sets it, so it has to reach the selection. Driven against
        /// LINUX rather than Windows, which is where the second difference from the Direct3D 11 audit shows: the
        /// probe it has to beat here answers <see cref="GpuBackendKind.Vulkan"/>, the SAME API, so an override
        /// that silently failed to parse would land on the incumbent implementation of the very backend the soak
        /// is measuring, and the session would look entirely correct.
        /// </summary>
        [Theory]
        [InlineData("vulkan-native")]
        [InlineData("vk-native")]
        public void ANativeTokenOverride_WinsOverTheLinuxProbe_AndReportsItself(string token)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(token, OSPlatformKind.Linux);

            Assert.Equal(GpuBackendKind.VulkanNative, selection.Backend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.Equal(token, selection.RequestedOverride);
        }

        // --- row 6: GpuBackendSelector.IsBackendSupported. In the registry class below. ---

        // --- row 7: GpuBackendSelector.ProbeOS, FLIPPED at 17.40.0, and the third row that means
        // something different here ---

        /// <summary>
        /// LINUX probes to the native backend since 17.40.0, and Linux is the operating system this member's
        /// flip changed. That is the whole difference from the Direct3D 11 audit's version of this row, where the
        /// flip is a Windows one: all three arms moved in one release, but they are three different defaults and
        /// a reader who assumes one enum append flips one default would have the wrong OS in mind.
        /// <para>
        /// Only the Linux row is here. The full four-OS mapping is the same assertion for all three appends, so
        /// it is pinned once in <c>GpuBackendKindAppendAuditTests.ProbeOS_AnswersTheNativeBackend_OnEveryOs</c>,
        /// and "this kind is never what the fallback answers" is walked over every OS by
        /// <see cref="ANativeRequest_IsNeverItsOwnFallback_OnAnyOs"/> below.
        /// </para>
        /// </summary>
        [Fact]
        public void ProbeOS_AnswersTheNativeBackend_OnLinux()
        {
            Assert.Equal(GpuBackendKind.VulkanNative, GpuBackendSelector.ProbeOS(OSPlatformKind.Linux));
            // The incumbent is still one token away, and is what a failed native creation falls back to.
            Assert.Equal(GpuBackendKind.Vulkan, GpuBackendSelector.IncumbentFor(OSPlatformKind.Linux));
        }

        // --- row 8: GpuBackendSelector._windowCandidates. In the registry class below. ---

        // --- rows 9 and 10: FrameCap.Resolve and DisplaySettings.RequiresFrameCapWarning ---
        // Correct by DEFAULT for the reason the Direct3D 11 rows are: both gate on Metal, so the native Vulkan
        // kind falls into the uncapped arm identically to the incumbent. Recorded because this is the arm #380's
        // present-pacing work will revisit, and a later edit must not quietly reclassify a native leg.

        [Theory]
        [InlineData(PresentMode.Vsync)]
        [InlineData(PresentMode.Immediate)]
        public void FrameCapAuto_ResolvesTheNativeKindExactlyLikeTheIncumbent(PresentMode present)
        {
            int incumbent = FrameCap.Auto.Resolve(GpuBackendKind.Vulkan, present, 144);
            int native = FrameCap.Auto.Resolve(GpuBackendKind.VulkanNative, present, 144);

            Assert.Equal(0, incumbent);
            Assert.Equal(incumbent, native);
        }

        [Theory]
        [InlineData(PresentMode.Vsync)]
        [InlineData(PresentMode.Immediate)]
        public void FrameCapWarning_IsSilentOnTheNativeKind_ExactlyLikeTheIncumbent(PresentMode present)
        {
            Assert.False(DisplaySettings.RequiresFrameCapWarning(GpuBackendKind.Vulkan, present, 0));
            Assert.False(DisplaySettings.RequiresFrameCapWarning(GpuBackendKind.VulkanNative, present, 0));
        }

        // --- row 11: GoldenCompare, at BOTH filename sites (decision V-I3) ---

        /// <summary>
        /// The guest mapping. The native backend is held to the incumbent's already-committed reference grids,
        /// unmodified, on the same rasterizer, at the same tolerance, which is one implementation checking the
        /// other. Owning a <c>vulkan-native</c> family instead was rejected outright, because a family of its own
        /// is a backend comparing against a reference it baked itself and checks nothing at all.
        /// </summary>
        [Fact]
        public void BothVulkanImplementations_ShareOneGoldenFamily()
        {
            Assert.Equal("vulkan", GoldenCompare.GoldenBackendToken(GpuBackendKind.Vulkan));
            Assert.Equal("vulkan", GoldenCompare.GoldenBackendToken(GpuBackendKind.VulkanNative));
        }

        /// <summary>
        /// And it does not disturb the OTHER shared family. Asserted because both guest mappings are arms of one
        /// switch, so the cheapest way to get this wrong is an edit that points the new arm at the wrong string
        /// and moves nothing else, which no golden run on either backend would notice until a filename is built.
        /// </summary>
        [Fact]
        public void TheTwoGuestMappings_DoNotCrossOver()
        {
            Assert.NotEqual(GoldenCompare.GoldenBackendToken(GpuBackendKind.VulkanNative),
                GoldenCompare.GoldenBackendToken(GpuBackendKind.Direct3D11Native));
            Assert.Equal("direct3d11", GoldenCompare.GoldenBackendToken(GpuBackendKind.Direct3D11Native));
        }

        /// <summary>
        /// The bake refusal, and the thing worth pinning is that it derives guest-ness GENERICALLY (V-I3): the
        /// token does not match the kind's own name under the <c>OrdinalIgnoreCase</c> compare
        /// <c>BakeRefusal</c> already used, which is what makes <c>vulkan</c> an OWNER token for
        /// <see cref="GpuBackendKind.Vulkan"/> and a GUEST token for <see cref="GpuBackendKind.VulkanNative"/>
        /// under one rule rather than a list somebody has to append to. So this row went green with no edit to
        /// the refusal at all, and it is asserted precisely because "no edit needed" is indistinguishable from
        /// "nobody checked".
        /// </summary>
        [Fact]
        public void Baking_IsRefusedOnTheNativeKind_UnlessTheFamilyOverrideIsSet()
        {
            string? refusal = GoldenCompare.BakeRefusal(GpuBackendKind.VulkanNative, familyOverride: false);

            Assert.NotNull(refusal);
            Assert.Contains("KE_UPDATE_GOLDENS", refusal);
            Assert.Contains(GoldenCompare.FamilyOverrideEnvVar, refusal);
            // The owning backend is nameable from the message alone, which is the action the reader has to take.
            Assert.Contains("vulkan", refusal);

            Assert.Null(GoldenCompare.BakeRefusal(GpuBackendKind.VulkanNative, familyOverride: true));
        }

        // That the incumbent still bakes as it always did is the other half of this row, and it is not asserted
        // here: GpuBackendKindAppendAuditTests.Baking_IsAllowedOnEveryBackendThatOwnsItsFamily already walks all
        // four owning backends, Vulkan among them, which is the stronger form of the same claim.

        // --- row 12: VeldridMap.SupportsCompletionFences and VeldridGpuDevice's Metal frame capture, neither an
        // append site. The first switches on Veldrid's own GraphicsBackend rather than on GpuBackendKind, and it
        // already answers true for GraphicsBackend.Vulkan, which is why V-G1 can demand ZERO capability
        // differences where Direct3D 11 had to permit one. The second lives inside the Veldrid wrapper, which a
        // provider-built device never becomes. Named here so a later reader does not re-raise them. ---

        // --- row 13: GpuDeviceContext.CreateOrFallBack's requested-versus-fallback comparison, and the fourth
        // row whose REASONING differs ---

        /// <summary>
        /// A value comparison rather than a switch, so it is invisible to any arm sweep, and correct by default
        /// for a reason worth pinning: the native kind is never EQUAL to what
        /// <see cref="GpuBackendSelector.ProbeOS"/> returns on any OS, so a native request never short-circuits
        /// the "nothing to fall back TO" guard and always routes through the functional probe.
        /// <para>
        /// The consequence differs from Direct3D 11's, and the soak depends on it. On Linux the fallback is
        /// <see cref="GpuBackendKind.Vulkan"/> while the request is <see cref="GpuBackendKind.VulkanNative"/>, so
        /// a machine whose native creation fails falls back to the INCUMBENT Vulkan backend and reports
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/>, while a missing REGISTRATION still throws. Those
        /// two look alike in a log line and telling them apart is the whole of decision V-I4.
        /// </para>
        /// </summary>
        [Fact]
        public void ANativeRequest_IsNeverItsOwnFallback_OnAnyOs()
        {
            foreach (OSPlatformKind os in Enum.GetValues<OSPlatformKind>())
                Assert.NotEqual(GpuBackendKind.VulkanNative, GpuBackendSelector.IncumbentFor(os));
        }

        // --- decision V-I1's other half: no new telemetry field ---

        /// <summary>
        /// The session header writes the enum NAME, so the attribution a soak session depends on is carried with
        /// no field added and no code changed. Under the rejected shape, where both implementations reused
        /// <see cref="GpuBackendKind.Vulkan"/>, every existing reader would report <c>"Vulkan"</c> and ignore
        /// whatever secondary field said which one ran.
        /// </summary>
        [Fact]
        public void TheSessionHeader_NamesTheNativeBackend_WithNoNewField()
        {
            var selection = new GpuBackendSelection(
                GpuBackendKind.VulkanNative, GpuBackendSource.EnvironmentOverride, "vulkan-native");

            var info = new TelemetrySessionInfo().WithGpu(selection, "llvmpipe", null, null);

            Assert.Equal("VulkanNative", info.GpuBackend);
            Assert.Equal("EnvironmentOverride", info.GpuBackendSource);
            Assert.Equal("vulkan-native", info.GpuRequestedOverride);
            // And the two driver-threading fields stay ABSENT, which is rows 1 and 2's answer showing up in the
            // artifact they feed: there is no D3D11_FEATURE_DATA_THREADING analogue on this backend, so "there
            // was nothing to ask" has to read as null rather than as a default.
            Assert.Null(info.DriverCommandLists);
            Assert.Null(info.DriverConcurrentCreates);
        }
    }

    /// <summary>
    /// Rows 3, 6 and 8 of the Vulkan append audit, split out of
    /// <see cref="GpuBackendKindVulkanAppendAuditTests"/> because they touch the process-wide provider registry
    /// under the real <see cref="GpuBackendKind.VulkanNative"/> kind. The full reasoning for the split, and for
    /// the collection, is on <see cref="GpuBackendKindAppendAuditRegistryTests"/> and is not repeated.
    /// <para>
    /// The ambient state here is a REGISTERED provider, because <c>KhaozEngine.Gpu.Vulkan</c> exists and
    /// <c>KhaozEngine.TestSupport.Gpu/VulkanBackendRegistration.cs</c> registers its real one at test discovery.
    /// So every row that means "nothing is registered" says so explicitly with a scope, which is the stronger
    /// form anyway: it asserts what the code does with no provider present rather than what it happens to do
    /// given today's ambient registration.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class GpuBackendKindVulkanAppendAuditRegistryTests
    {
        // --- row 3: the CreateForWindow / CreateHeadless Veldrid switches. RIDES the arm phase 2 made explicit,
        // and the obligation this row carries is a VERIFICATION rather than an edit: the message has to name the
        // provider registry generically, or a Vulkan wiring fault reads as a Direct3D 11 one. ---

        /// <summary>
        /// The native kind never reaches the Veldrid creation switch, because every entry into that path branches
        /// on <see cref="GpuBackendProviders.RequiresProvider"/> first. With nothing registered the observable
        /// outcome is the provider-missing exception naming the one line that fixes it.
        /// </summary>
        [Fact]
        public void CreateForWindow_OnTheNativeKind_NeverAsksVeldridForADevice()
        {
            using (Unregistered(GpuBackendKind.VulkanNative))
            {
                GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                    () => GpuDeviceContext.CreateForWindow(default, 640, 480, true, GpuBackendKind.VulkanNative));

                Assert.Equal(GpuBackendKind.VulkanNative, ex.Backend);
                // Not Metal, which is what the pre-phase-2 discard arm asked for.
                Assert.DoesNotContain("Metal", ex.Message);
                // And the ACTIONABLE line is this backend's own. The second provider-backed backend is what
                // turned the old message wrong: written when there was one, it named KhaozEngineD3D11.Register()
                // as though that were the generic instruction, so a Vulkan wiring fault told the reader to
                // register Direct3D 11. It states the convention now, and this is the row that holds it there.
                Assert.Contains("KhaozEngineVulkan.Register()", ex.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The headless twin, through the public named-backend overload, which is the entry a backend-parity
        /// harness uses. Same exception, same absence of an unrelated API in the message.
        /// </summary>
        [Fact]
        public void CreateHeadless_OnTheNativeKind_ThrowsNamingTheMissingRegistration()
        {
            using (Unregistered(GpuBackendKind.VulkanNative))
            {
                GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                    () => GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative));

                Assert.Equal(GpuBackendKind.VulkanNative, ex.Backend);
                Assert.DoesNotContain("Metal", ex.Message);
                Assert.Contains("KhaozEngineVulkan.Register()", ex.Message, StringComparison.Ordinal);
            }
        }

        // --- row 6: GpuBackendSelector.IsBackendSupported ---

        /// <summary>
        /// Veldrid cannot answer for a backend it does not implement, and here it would answer WRONGLY rather
        /// than not at all: <c>GraphicsDevice.IsBackendSupported(GraphicsBackend.Vulkan)</c> is a perfectly good
        /// answer about a different implementation. So the native kind routes to its own provider's functional
        /// probe. With no provider the answer is false and is not cached as false, so registering later still
        /// gets to answer for real.
        /// </summary>
        [Fact]
        public void IsBackendSupported_AsksTheNativeProvider_NotVeldrid()
        {
            using (Unregistered(GpuBackendKind.VulkanNative))
            {
                Assert.False(GpuBackendSelector.IsBackendSupported(GpuBackendKind.VulkanNative));

                var provider = new FakeBackendProvider(GpuBackendKind.VulkanNative) { Supported = true };
                using (Registered(GpuBackendKind.VulkanNative, provider))
                {
                    Assert.True(GpuBackendSelector.IsBackendSupported(GpuBackendKind.VulkanNative));
                    Assert.Equal(1, provider.SupportProbes);
                }

                Assert.False(GpuBackendSelector.IsBackendSupported(GpuBackendKind.VulkanNative));
            }
        }

        // --- row 8: GpuBackendSelector._windowCandidates, FLIPPED at 17.40.0 with the default ---

        /// <summary>
        /// The native kind is OFFERED now, because it is what "Vulkan" means on Linux from this release.
        /// Asserted with the provider registered and reporting SUPPORTED, since the offered list is probed and
        /// an unregistered kind answers no. What is still NOT asserted is that the incumbent is present:
        /// whether Veldrid Vulkan is offered is a fact about the machine (a developer Mac has no loader and
        /// answers no), and pinning it here would be pinning the runner rather than the candidate list.
        /// </summary>
        [Fact]
        public void SupportedBackends_OffersTheNativeKind_ToAPlayer()
        {
            using (Registered(GpuBackendKind.VulkanNative,
                new FakeBackendProvider(GpuBackendKind.VulkanNative) { Supported = true }))
            {
                Assert.Contains(GpuBackendKind.VulkanNative, GpuBackendSelector.SupportedBackends());
            }
        }

        /// <summary>
        /// The token-to-adoption path in one call, driven the way a soak session drives it: name the backend in
        /// <c>KE_GRAPHICS_BACKEND</c> and get a device from the registered provider. This is the row that turns
        /// the two new tokens from a parse result into a reachable backend.
        /// </summary>
        [Fact]
        public void CreateHeadless_OnTheNamedNativeToken_AdoptsTheProvidersDevice()
        {
            var device = new FakeGpuDevice(GpuBackendKind.VulkanNative);
            var provider = new FakeBackendProvider(GpuBackendKind.VulkanNative) { Device = device };

            using (new EnvScope(GpuBackendSelector.EnvVarName, "vulkan-native"))
            using (new BackendProviderScope(GpuBackendKind.VulkanNative, provider))
            {
                using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();

                Assert.Same(device, ctx.GpuDevice);
                Assert.Equal(GpuBackendKind.VulkanNative, ctx.Backend);
                Assert.Equal(GpuBackendSource.EnvironmentOverride, ctx.Selection.Source);
                Assert.Equal(1, provider.HeadlessCreations);
                Assert.Equal(0, provider.WindowedCreations);
                // Headless never probes and never falls back, on any backend: it propagates its failure, so a
                // headless run cannot quietly change backend and file its golden images under one that never
                // rendered them. That guarantee is what the shared golden family rests on.
                Assert.Equal(0, provider.SupportProbes);
            }
        }

        static BackendProviderScope Registered(GpuBackendKind backend, IGpuBackendProvider provider)
            => new(backend, provider);

        static BackendProviderScope Unregistered(GpuBackendKind backend) => new(backend, provider: null);
    }
}
