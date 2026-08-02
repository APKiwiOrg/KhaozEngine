using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The APPEND AUDIT for <see cref="GpuBackendKind.Direct3D11Native"/>: one test per site that switches on a
    /// backend kind, compares against one, or derives a string from one. Section 4.3 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c> enumerates thirteen, and this file walks all
    /// of them in the order that table lists them (decision I5). Device-free, so the whole audit runs under a
    /// plain <c>dotnet test</c> on any OS.
    /// <para>
    /// Why an audit and not a sweep after the fact. Appending an enum member is safe for the ENUM. It is not safe
    /// for the code around it, and the failure shape is the problem: a C# switch expression carrying a discard
    /// does NOT throw for an unlisted member, so THREE of the thirteen sites degraded the new backend silently.
    /// Two dropped its driver-threading diagnostics, leaving a session that still looks healthy while the two
    /// telemetry fields written to diagnose exactly this backend go missing. The third asked Veldrid for a METAL
    /// device on Windows, which fails naming an API nobody selected.
    /// </para>
    /// <para>
    /// Three rows of the table are deliberately not asserted here, and each is a decision rather than a gap.
    /// <c>VeldridMap.SupportsCompletionFences</c> switches on Veldrid's own <c>GraphicsBackend</c> and not on
    /// <see cref="GpuBackendKind"/> at all, so it is not an append site (it is in the table only so a later reader
    /// does not re-raise it). <c>VeldridGpuDevice</c>'s Metal frame-capture check lives inside the Veldrid
    /// wrapper, which a provider-built device never becomes, so the native kind cannot reach it. And the fourth
    /// creation-path row is asserted through its observable behaviour below rather than by naming a private
    /// switch.
    /// </para>
    /// <para>
    /// Rows 6 and 8 are asserted in <see cref="GpuBackendKindAppendAuditRegistryTests"/> instead of here, because
    /// they are the only two that REGISTER a provider under the real kind and so cannot share the parallel pool.
    /// Everything in this class is pure, so it does.
    /// </para>
    /// </summary>
    public sealed class GpuBackendKindAppendAuditTests
    {
        // --- the enum itself (decision I1) ---

        /// <summary>
        /// The published values, pinned. A consuming game persists the player's backend choice as a stored
        /// preference and hands it back as a <see cref="GpuBackendKind"/>, so a renumbering repoints every saved
        /// graphics setting at a different backend, silently, on the next launch.
        /// </summary>
        [Theory]
        [InlineData(GpuBackendKind.Metal, 0)]
        [InlineData(GpuBackendKind.Vulkan, 1)]
        [InlineData(GpuBackendKind.Direct3D11, 2)]
        [InlineData(GpuBackendKind.OpenGL, 3)]
        [InlineData(GpuBackendKind.Direct3D11Native, 4)]
        public void Ordinals_ArePinnedAndAppendOnly(GpuBackendKind kind, int expected)
            => Assert.Equal(expected, (int)kind);

        /// <summary>
        /// The native kind is a SEPARATE member rather than a flag on the incumbent one, which is what makes the
        /// telemetry header, the session log and a golden filename each name the implementation that actually ran.
        /// </summary>
        [Fact]
        public void TheNativeKind_IsDistinctFromTheVeldridOne()
            => Assert.NotEqual(GpuBackendKind.Direct3D11, GpuBackendKind.Direct3D11Native);

        /// <summary>
        /// Both implementations answer the Direct3D11 family predicate, and nothing else does. This is the one
        /// source the driver-threading probe and its log line share, so a drift here is a drift in both.
        /// </summary>
        [Fact]
        public void IsDirect3D11_CoversBothImplementations_AndNothingElse()
        {
            Assert.True(GpuBackendKind.Direct3D11.IsDirect3D11());
            Assert.True(GpuBackendKind.Direct3D11Native.IsDirect3D11());
            Assert.False(GpuBackendKind.Metal.IsDirect3D11());
            Assert.False(GpuBackendKind.Vulkan.IsDirect3D11());
            Assert.False(GpuBackendKind.OpenGL.IsDirect3D11());
        }

        // --- row 1: GpuDeviceContext.LogThreadingCaps, and row 2: D3D11ThreadingProbe.IsApplicable ---
        // Both are the same silent degradation: an equality check against the Veldrid kind alone drops the driver
        // threading INFO line, its WARN arm, and the two telemetry fields the probe feeds, on the one backend the
        // probe was written for. The probe's pure guard is the observable half.

        [Theory]
        [InlineData(GpuBackendKind.Direct3D11Native, true, true)]
        [InlineData(GpuBackendKind.Direct3D11Native, false, false)]
        [InlineData(GpuBackendKind.Direct3D11, true, true)]
        [InlineData(GpuBackendKind.Metal, true, false)]
        [InlineData(GpuBackendKind.Vulkan, true, false)]
        [InlineData(GpuBackendKind.OpenGL, true, false)]
        public void ThreadingProbe_AppliesToBothDirect3D11Implementations(
            GpuBackendKind backend, bool isWindows, bool expected)
            => Assert.Equal(expected, D3D11ThreadingProbe.IsApplicable(backend, isWindows));

        /// <summary>
        /// The log line's own gate reads the same predicate the probe does, so the two cannot disagree about
        /// whether an answer was worth asking for and worth printing.
        /// </summary>
        [Fact]
        public void TheThreadingLogGate_AndTheProbeGate_AgreeOnEveryKind()
        {
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
                Assert.Equal(kind.IsDirect3D11(), D3D11ThreadingProbe.IsApplicable(kind, isWindows: true));
        }

        // --- row 3: the CreateForWindow / CreateHeadless Veldrid switches, the worst of the three ---

        /// <summary>
        /// The native kind never reaches the Veldrid creation switch, because every entry into that path branches
        /// on <see cref="GpuBackendProviders.RequiresProvider"/> first. With nothing registered the observable
        /// outcome is the provider-missing exception naming the one line that fixes it, NOT a Veldrid failure
        /// about Metal, which is what the discard arm produced before this change.
        /// </summary>
        [Fact]
        public void CreateForWindow_OnTheNativeKind_NeverAsksVeldridForAMetalDevice()
        {
            GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuDeviceContext.CreateForWindow(default, 640, 480, true, GpuBackendKind.Direct3D11Native));

            Assert.Equal(GpuBackendKind.Direct3D11Native, ex.Backend);
            Assert.DoesNotContain("Metal", ex.Message);
        }

        /// <summary>
        /// The fourteenth site, and the one section 4.3 does not list because it did not exist when the table was
        /// written. <see cref="GpuBackendProviders.RequiresProvider"/> is stated as "everything the built-in path
        /// does not build", so an APPENDED member is provider-backed with NO edit, which is the safe direction:
        /// forgetting throws a message naming the missing registration instead of routing the new kind into the
        /// switch above.
        /// </summary>
        [Fact]
        public void TheNativeKind_IsProviderBacked_WithNoEditToTheRegistry()
        {
            Assert.True(GpuBackendProviders.RequiresProvider(GpuBackendKind.Direct3D11Native));
            Assert.False(GpuBackendProviders.RequiresProvider(GpuBackendKind.Direct3D11));
        }

        // --- row 4: GpuBackendSelector.ToVeldrid ---

        /// <summary>
        /// It carried a discard (<c>_ =&gt; GraphicsBackend.Metal</c>), so a missing arm here was never a compile
        /// error and never a <c>SwitchExpressionException</c>: it was a wrong answer. The native kind now throws
        /// saying what is actually wrong.
        /// </summary>
        [Fact]
        public void ToVeldrid_ThrowsForTheNativeKind_RatherThanAnsweringMetal()
        {
            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => GpuBackendSelector.ToVeldrid(GpuBackendKind.Direct3D11Native));

            Assert.Contains(nameof(GpuBackendKind.Direct3D11Native), ex.Message);
            Assert.Contains("RequiresProvider", ex.Message);
        }

        // --- row 5: GpuBackendSelector.TryParseBackend ---

        [Theory]
        [InlineData("d3d11-native")]
        [InlineData("direct3d11-native")]
        [InlineData("D3D11-Native")]
        [InlineData("  DIRECT3D11-NATIVE\t")]
        public void TryParseBackend_RecognizesBothNativeTokens(string value)
        {
            Assert.True(GpuBackendSelector.TryParseBackend(value, out GpuBackendKind backend));
            Assert.Equal(GpuBackendKind.Direct3D11Native, backend);
        }

        /// <summary>
        /// The suffix must not bleed either way. A tester who wanted the incumbent and typed <c>d3d11</c> gets the
        /// incumbent, and a typo'd suffix is an unrecognized override (a loud diagnostic) rather than a silent run
        /// on the wrong implementation under the right name, which is the exact attribution failure a separate
        /// member exists to prevent.
        /// </summary>
        [Theory]
        [InlineData("d3d11", GpuBackendKind.Direct3D11)]
        [InlineData("direct3d11", GpuBackendKind.Direct3D11)]
        public void TryParseBackend_KeepsTheIncumbentTokensPointingAtTheIncumbent(string value, GpuBackendKind expected)
        {
            Assert.True(GpuBackendSelector.TryParseBackend(value, out GpuBackendKind backend));
            Assert.Equal(expected, backend);
        }

        [Theory]
        [InlineData("d3d11native")]
        [InlineData("d3d11_native")]
        [InlineData("native")]
        [InlineData("d3d11-nativ")]
        public void TryParseBackend_RejectsANearMissRatherThanGuessing(string value)
        {
            Assert.False(GpuBackendSelector.TryParseBackend(value, out _));
            // And an unrecognized value keeps its raw text for the diagnostic, so the tester is told their
            // variable did nothing instead of reading the OS default as the backend they asked for.
            GpuBackendSelection selection = GpuBackendSelector.Resolve(value, OSPlatformKind.Windows);
            Assert.Equal(GpuBackendSource.UnrecognizedOverride, selection.Source);
            Assert.Equal(value, selection.RequestedOverride);
        }

        /// <summary>The whole point of the token: it is what a field soak sets, so it has to reach the selection.</summary>
        [Fact]
        public void ANativeTokenOverride_WinsOverTheWindowsProbe_AndReportsItself()
        {
            GpuBackendSelection selection =
                GpuBackendSelector.Resolve("d3d11-native", OSPlatformKind.Windows);

            Assert.Equal(GpuBackendKind.Direct3D11Native, selection.Backend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.Equal("d3d11-native", selection.RequestedOverride);
        }

        // --- row 6: GpuBackendSelector.IsBackendSupported, asserted in GpuBackendKindAppendAuditRegistryTests
        // below. It registers a provider under the real kind, so it belongs off the parallel pool. ---

        // --- row 7: GpuBackendSelector.ProbeOS, unchanged until the flip (I4) ---

        /// <summary>
        /// Windows still probes to the INCUMBENT. Flipping the default is the last step of the rollout, after all
        /// five gates, not a side effect of the member existing. Until then the native leg is exercised by being
        /// named.
        /// </summary>
        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.Metal)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Vulkan)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.Vulkan)]
        public void ProbeOS_StillAnswersTheIncumbent_OnEveryOs(OSPlatformKind os, GpuBackendKind expected)
        {
            Assert.Equal(expected, GpuBackendSelector.ProbeOS(os));
            Assert.NotEqual(GpuBackendKind.Direct3D11Native, GpuBackendSelector.ProbeOS(os));
        }

        // --- row 8: GpuBackendSelector._windowCandidates, likewise asserted in
        // GpuBackendKindAppendAuditRegistryTests below, for the same reason. ---

        // --- rows 9 and 10: FrameCap.Resolve and DisplaySettings.RequiresFrameCapWarning ---
        // Correct by DEFAULT (the native kind falls into the uncapped arm, identical to the incumbent), recorded
        // because this is the arm #380's present-pacing work will revisit and a later edit must not quietly
        // reclassify the native leg.

        [Theory]
        [InlineData(PresentMode.Vsync)]
        [InlineData(PresentMode.Immediate)]
        public void FrameCapAuto_ResolvesTheNativeKindExactlyLikeTheIncumbent(PresentMode present)
        {
            int incumbent = FrameCap.Auto.Resolve(GpuBackendKind.Direct3D11, present, 144);
            int native = FrameCap.Auto.Resolve(GpuBackendKind.Direct3D11Native, present, 144);

            Assert.Equal(0, incumbent);
            Assert.Equal(incumbent, native);
        }

        [Theory]
        [InlineData(PresentMode.Vsync)]
        [InlineData(PresentMode.Immediate)]
        public void FrameCapWarning_IsSilentOnTheNativeKind_ExactlyLikeTheIncumbent(PresentMode present)
        {
            Assert.False(DisplaySettings.RequiresFrameCapWarning(GpuBackendKind.Direct3D11, present, 0));
            Assert.False(DisplaySettings.RequiresFrameCapWarning(GpuBackendKind.Direct3D11Native, present, 0));
        }

        // --- row 11: GoldenCompare, at BOTH filename sites (decision I3) ---

        /// <summary>
        /// The strongest free proof in the whole port: the native backend is held to the incumbent's 36
        /// already-committed reference grids, unmodified, on the same WARP rasterizer, at the same tolerance.
        /// Deriving the filename from the enum name would have thrown it away by orphaning every one of them
        /// behind a token nothing had ever baked, with a red that reads as "golden missing" rather than "you
        /// renamed the family".
        /// </summary>
        [Fact]
        public void BothDirect3D11Implementations_ShareOneGoldenFamily()
        {
            Assert.Equal("direct3d11", GoldenCompare.GoldenBackendToken(GpuBackendKind.Direct3D11));
            Assert.Equal("direct3d11", GoldenCompare.GoldenBackendToken(GpuBackendKind.Direct3D11Native));
        }

        [Theory]
        [InlineData(GpuBackendKind.Metal, "metal")]
        [InlineData(GpuBackendKind.Vulkan, "vulkan")]
        [InlineData(GpuBackendKind.OpenGL, "opengl")]
        public void EveryOtherBackend_KeepsItsOwnGoldenFamily(GpuBackendKind kind, string expected)
            => Assert.Equal(expected, GoldenCompare.GoldenBackendToken(kind));

        /// <summary>
        /// No member may be left without a decided family. The mapping throws rather than guessing, and this is
        /// what turns that throw into a device-free red instead of a golden-missing failure on a GPU leg nobody
        /// runs locally.
        /// </summary>
        [Fact]
        public void EveryBackendKind_HasADecidedGoldenFamily()
        {
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
            {
                string token = GoldenCompare.GoldenBackendToken(kind);
                Assert.False(string.IsNullOrWhiteSpace(token));
                Assert.Equal(token.ToLowerInvariant(), token);
            }
        }

        [Fact]
        public void AnUnmappedKind_ThrowsRatherThanInventingAFamily()
            => Assert.Throws<NotSupportedException>(() => GoldenCompare.GoldenBackendToken((GpuBackendKind)9001));

        /// <summary>
        /// The bake refusal (I3). A backend that is a GUEST in another's family must not write into it: the file
        /// it would produce is exactly the file it would then have compared against, so the overwrite proves
        /// nothing and destroys both the reference under test AND the owning implementation's, with nothing left
        /// to notice it by.
        /// </summary>
        [Fact]
        public void Baking_IsRefusedOnTheNativeKind_UnlessTheFamilyOverrideIsSet()
        {
            string? refusal = GoldenCompare.BakeRefusal(GpuBackendKind.Direct3D11Native, familyOverride: false);

            Assert.NotNull(refusal);
            Assert.Contains("KE_UPDATE_GOLDENS", refusal);
            // Actionable on its own: the reader is looking at a red bake they expected to be a write.
            Assert.Contains(GoldenCompare.FamilyOverrideEnvVar, refusal);

            Assert.Null(GoldenCompare.BakeRefusal(GpuBackendKind.Direct3D11Native, familyOverride: true));
        }

        /// <summary>A backend that OWNS its family bakes as it always did. The guard must not cost the ordinary
        /// rebake anything, or it gets worked around instead of respected.</summary>
        [Theory]
        [InlineData(GpuBackendKind.Metal)]
        [InlineData(GpuBackendKind.Vulkan)]
        [InlineData(GpuBackendKind.Direct3D11)]
        [InlineData(GpuBackendKind.OpenGL)]
        public void Baking_IsAllowedOnEveryBackendThatOwnsItsFamily(GpuBackendKind kind)
            => Assert.Null(GoldenCompare.BakeRefusal(kind, familyOverride: false));

        // --- row 13: GpuDeviceContext.CreateOrFallBack's requested-versus-fallback comparison ---

        /// <summary>
        /// A value comparison, not a switch, so it is invisible to any arm sweep. It is correct by default for
        /// exactly one reason, and the reason is worth pinning: the native kind is never EQUAL to what
        /// <see cref="GpuBackendSelector.ProbeOS"/> returns on any OS, so a native request never short-circuits
        /// the "nothing to fall back TO" guard and always routes through the functional probe instead.
        /// </summary>
        [Fact]
        public void ANativeRequest_IsNeverItsOwnFallback_OnAnyOs()
        {
            foreach (OSPlatformKind os in Enum.GetValues<OSPlatformKind>())
                Assert.NotEqual(GpuBackendKind.Direct3D11Native, GpuBackendSelector.ProbeOS(os));
        }

        // --- decision I4: no new telemetry field ---

        /// <summary>
        /// The session header writes the enum NAME, so the attribution a soak session depends on is already
        /// carried and no field is added. This is the half of I4 that decided against reusing the incumbent
        /// member: under that shape both implementations would report <c>"Direct3D11"</c> here and every existing
        /// reader would ignore whatever secondary field said which one ran.
        /// </summary>
        [Fact]
        public void TheSessionHeader_NamesTheNativeBackend_WithNoNewField()
        {
            var selection = new GpuBackendSelection(
                GpuBackendKind.Direct3D11Native, GpuBackendSource.EnvironmentOverride, "d3d11-native");

            var info = new TelemetrySessionInfo().WithGpu(selection, "Microsoft Basic Render Driver", null,
                new GpuThreadingCaps(DriverCommandLists: true, DriverConcurrentCreates: false));

            Assert.Equal("Direct3D11Native", info.GpuBackend);
            Assert.Equal("EnvironmentOverride", info.GpuBackendSource);
            Assert.Equal("d3d11-native", info.GpuRequestedOverride);
            // And the threading fields the two probe sites feed survive on the native leg, which is what rows 1
            // and 2 exist to keep true.
            Assert.True(info.DriverCommandLists);
            Assert.False(info.DriverConcurrentCreates);
        }
    }

    /// <summary>
    /// Rows 6 and 8 of the append audit, split out of <see cref="GpuBackendKindAppendAuditTests"/> because they
    /// are the two that REGISTER a fake provider under the real
    /// <see cref="GpuBackendKind.Direct3D11Native"/> kind, and the registry plus the support cache behind it are
    /// process-wide.
    /// <para>
    /// What that costs on the parallel pool is a rare red with no bug under it:
    /// <c>GpuBackendSelectorTests.IsBackendSupported_NeverThrows_ForAnyBackend</c> walks every enum member and
    /// probes it, so a concurrent run of it lands on this fake in the window where it is registered and bumps the
    /// very probe counter row 6 asserts is exactly one. Registering under a SENTINEL kind is the other way out and
    /// is what <c>GpuBackendProvidersTests</c> does, but these two rows are auditing the REAL member, which is the
    /// whole point of them, so the collection is the fix. The cost is two tests off the pool, not the other
    /// twenty-odd pure ones.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class GpuBackendKindAppendAuditRegistryTests
    {
        // --- row 6: GpuBackendSelector.IsBackendSupported ---

        /// <summary>
        /// Veldrid cannot answer for a backend it does not implement, so the native kind is routed to its
        /// provider's own functional probe. With no provider the answer is false, and it is not cached as false,
        /// so registering later still gets to answer for real.
        /// </summary>
        [Fact]
        public void IsBackendSupported_AsksTheNativeProvider_NotVeldrid()
        {
            Assert.False(GpuBackendSelector.IsBackendSupported(GpuBackendKind.Direct3D11Native));

            var provider = new FakeBackendProvider(GpuBackendKind.Direct3D11Native) { Supported = true };
            using (Registered(GpuBackendKind.Direct3D11Native, provider))
            {
                Assert.True(GpuBackendSelector.IsBackendSupported(GpuBackendKind.Direct3D11Native));
                Assert.Equal(1, provider.SupportProbes);
            }

            Assert.False(GpuBackendSelector.IsBackendSupported(GpuBackendKind.Direct3D11Native));
        }

        // --- row 8: GpuBackendSelector._windowCandidates, unchanged until default-ready (I4) ---

        /// <summary>
        /// A settings screen offers an API, not an implementation of one. Two entries both reading "Direct3D 11"
        /// is a choice nobody outside this repo can make, so the native kind stays off the offered list until it
        /// becomes what "Direct3D 11" means.
        /// </summary>
        [Fact]
        public void SupportedBackends_NeverOffersTheNativeKind_ToAPlayer()
        {
            using (Registered(GpuBackendKind.Direct3D11Native,
                new FakeBackendProvider(GpuBackendKind.Direct3D11Native) { Supported = true }))
            {
                Assert.DoesNotContain(GpuBackendKind.Direct3D11Native, GpuBackendSelector.SupportedBackends());
            }
        }

        static BackendProviderScope Registered(GpuBackendKind backend, IGpuBackendProvider provider)
            => new(backend, provider);
    }

    /// <summary>
    /// The headless creation path's provider branch, which only became reachable from a test when the selector
    /// learned the <c>d3d11-native</c> token: <see cref="GpuDeviceContext.CreateHeadless()"/> resolves the backend
    /// from the live environment, so before the token existed no environment value could name a provider-backed
    /// kind and the branch could only be reasoned about.
    /// <para>
    /// It needs a process-wide environment mutation, hence the non-parallel collection. That is not
    /// bookkeeping: <c>GoldenCompare</c> reads the same variable to pick a golden family, so a golden test running
    /// concurrently with this one would look for a reference under whichever backend this test had set.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class HeadlessProviderCreationTests
    {
        /// <summary>
        /// The whole selection-to-adoption path in one call, driven the way a soak session drives it: name the
        /// backend in <c>KE_GRAPHICS_BACKEND</c> and get a device from the registered provider, with its own
        /// driver-threading answer carried through onto the context.
        /// </summary>
        [Fact]
        public void CreateHeadless_OnANamedProviderBackend_AdoptsTheProvidersDevice()
        {
            var device = new FakeGpuDevice(GpuBackendKind.Direct3D11Native);
            var caps = new GpuThreadingCaps(DriverCommandLists: true, DriverConcurrentCreates: false);
            var provider = new FakeBackendProvider(GpuBackendKind.Direct3D11Native)
            {
                Device = device,
                ThreadingCaps = caps,
            };

            using (new EnvScope(GpuBackendSelector.EnvVarName, "d3d11-native"))
            using (new BackendProviderScope(GpuBackendKind.Direct3D11Native, provider))
            {
                using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();

                Assert.Same(device, ctx.GpuDevice);
                Assert.Equal(GpuBackendKind.Direct3D11Native, ctx.Backend);
                Assert.Equal(GpuBackendSource.EnvironmentOverride, ctx.Selection.Source);
                Assert.Equal(caps, ctx.ThreadingCaps);

                Assert.Equal(1, provider.HeadlessCreations);
                Assert.Equal(0, provider.WindowedCreations);
                // Headless has never probed and never fallen back on any backend: it propagates its failure, so a
                // headless run cannot quietly change backend and file its golden images under one that never
                // rendered them.
                Assert.Equal(0, provider.SupportProbes);
            }
        }

        /// <summary>
        /// And with nothing registered it throws naming the missing registration, rather than falling through to
        /// the Veldrid switch whose discard arm used to ask for a Metal device.
        /// </summary>
        [Fact]
        public void CreateHeadless_OnANamedProviderBackend_ThrowsWhenNothingIsRegistered()
        {
            using (new EnvScope(GpuBackendSelector.EnvVarName, "direct3d11-native"))
            using (new BackendProviderScope(GpuBackendKind.Direct3D11Native, provider: null))
            {
                GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                    GpuDeviceContext.CreateHeadless);

                Assert.Equal(GpuBackendKind.Direct3D11Native, ex.Backend);
                Assert.DoesNotContain("Metal", ex.Message);
            }
        }
    }

    /// <summary>
    /// Groups the tests that mutate PROCESS-WIDE graphics backend state, of which there are two kinds and both
    /// belong here: <c>KE_GRAPHICS_BACKEND</c>, which <c>GoldenCompare</c> also reads to pick a golden family, and
    /// the <c>GpuBackendProviders</c> registry (plus the support cache keyed off it) whenever the kind being
    /// registered under is a REAL one. <c>DisableParallelization</c> keeps the whole group off the parallel pool,
    /// so nothing else is running while either is temporarily something other than what the run was launched with.
    /// <para>
    /// Named for the state rather than for the env var on purpose. The registry half was the miss: a test that
    /// registers a fake under a real kind reads as ordinary local setup, right up until a concurrent collection
    /// enumerating <c>GpuBackendKind</c> probes that fake and moves its counters.
    /// </para>
    /// </summary>
    [CollectionDefinition("GraphicsBackendGlobalState", DisableParallelization = true)]
    public sealed class GraphicsBackendGlobalStateCollection { }

    /// <summary>Sets an environment variable for the duration of a test and puts the previous value back,
    /// including putting back "not set at all", which is not the same thing as empty.</summary>
    internal sealed class EnvScope : IDisposable
    {
        readonly string _name;
        readonly string? _previous;

        internal EnvScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    /// <summary>
    /// Registers (or, with a null provider, temporarily REMOVES) a backend provider for the duration of a test and
    /// restores whatever was there before. The registry is process-wide static state, so a leaked fake would
    /// follow every later test in the run.
    /// <para>
    /// Restoring rather than simply unregistering matters because these tests name a REAL backend kind: once
    /// <c>KhaozEngine.Gpu.D3D11</c> exists, this test assembly registers a genuine provider for it at load, and a
    /// scope that unregistered on the way out would take that away from every test after it.
    /// </para>
    /// </summary>
    internal sealed class BackendProviderScope : IDisposable
    {
        readonly GpuBackendKind _backend;
        readonly IGpuBackendProvider? _previous;

        internal BackendProviderScope(GpuBackendKind backend, IGpuBackendProvider? provider)
        {
            _backend = backend;
            GpuBackendProviders.TryGet(backend, out _previous);
            if (provider is null) GpuBackendProviders.Unregister(backend);
            else GpuBackendProviders.Register(backend, provider);
        }

        public void Dispose()
        {
            if (_previous is null) GpuBackendProviders.Unregister(_backend);
            else GpuBackendProviders.Register(_backend, _previous);
        }
    }
}
