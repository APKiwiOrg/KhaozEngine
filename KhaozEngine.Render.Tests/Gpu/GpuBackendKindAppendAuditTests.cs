using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Rows 3, 6 and 8 are asserted in <see cref="GpuBackendKindAppendAuditRegistryTests"/> instead of here,
    /// because they are the ones that touch the process-wide provider registry under the REAL kind and so cannot
    /// share the parallel pool. Everything in this class is pure, so it does.
    /// </para>
    /// <para>
    /// THE SECOND APPEND, <see cref="GpuBackendKind.VulkanNative"/>, walks the same thirteen sites in
    /// <c>GpuBackendKindVulkanAppendAuditTests</c>, and THE THIRD, <see cref="GpuBackendKind.MetalNative"/>, in
    /// <c>GpuBackendKindMetalAppendAuditTests</c>. Each append's own rows live in their own file rather than here
    /// for the file-size ratchet's reason, and the split is legible: this file is the audit that DISCOVERED the
    /// sites, the other two are the audits that answered them again and record where the appends differ (four
    /// rows do for Vulkan, three for Metal, and Metal's three are the ones that degrade SILENTLY). What stays
    /// here is every row where the appends share ONE assertion rather than having one each: the pinned ordinals,
    /// the family predicates, and the theories that walk every member.
    /// </para>
    /// <para>
    /// The enumeration is thirteen rows and the real count is fifteen. Both extras were found by landing the
    /// second append rather than by reading the table: <c>GpuBackendProviders.RequiresProvider</c>, which stopped
    /// being an append site once it was stated as membership rather than a switch, and the unrecognized-override
    /// warning's token list, asserted below. Section 4.2 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> records both against the table, so the next
    /// append inherits the full list.
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
        [InlineData(GpuBackendKind.VulkanNative, 5)]
        [InlineData(GpuBackendKind.MetalNative, 6)]
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
            Assert.False(GpuBackendKind.VulkanNative.IsDirect3D11());
            Assert.False(GpuBackendKind.MetalNative.IsDirect3D11());
        }

        /// <summary>
        /// No kind is claimed by more than one family predicate. Worth asserting once rather than reasoning
        /// about, because the three are read by different subsystems and a member that satisfied two would get a
        /// Direct3D 11 driver diagnostic written about a Metal session, or a software frame cap applied to a
        /// Vulkan one.
        /// <para>
        /// Here rather than in either native audit file because it is a theory over EVERY member, which is this
        /// file's half of the split. It grew from a pair to a trio when
        /// <see cref="GpuBackendKind.MetalNative"/> landed, and growing is the point: the cost of a fourth
        /// predicate is one term here, not a new test nobody writes.
        /// </para>
        /// </summary>
        [Fact]
        public void TheFamilyPredicates_NeverClaimTheSameKindTwice()
        {
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
            {
                int claims = (kind.IsDirect3D11() ? 1 : 0) + (kind.IsVulkan() ? 1 : 0) + (kind.IsMetal() ? 1 : 0);
                Assert.True(claims <= 1, $"{kind} is claimed by {claims} family predicates");
            }
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
        [InlineData(GpuBackendKind.VulkanNative, true, false)]
        [InlineData(GpuBackendKind.MetalNative, true, false)]
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

        // --- row 3: the CreateForWindow / CreateHeadless Veldrid switches, the worst of the three. Asserted in
        // GpuBackendKindAppendAuditRegistryTests below, because pinning "nothing registered" is now itself a
        // registry mutation: KhaozEngine.Gpu.D3D11 exists and a static constructor on GpuFactAttribute in the
        // shared KhaozEngine.TestSupport.Gpu project registers its real provider, fired at test discovery
        // (KhaozEngine.TestSupport.Gpu/D3D11BackendRegistration.cs). ---

        /// <summary>
        /// The fourteenth site, and the one section 4.3 does not list because it did not exist when the table was
        /// written. <see cref="GpuBackendProviders.RequiresProvider"/> was "everything the built-in path does not
        /// build" and is CONSTANT TRUE since 18.0.0, because there is no built-in path left. An APPENDED member
        /// is provider-backed with nothing to remember, which is the safe direction and now the only one:
        /// forgetting the registration throws a message naming it rather than routing the new kind anywhere.
        /// </summary>
        [Fact]
        public void EveryKind_IsProviderBacked_WithNoEditToTheRegistry()
        {
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
                Assert.True(GpuBackendProviders.RequiresProvider(kind));
        }

        // --- row 4: GpuBackendSelector.ToVeldrid, DELETED in 18.0.0 with the backend it mapped onto ---

        /// <summary>
        /// The row that used to live here mapped a <see cref="GpuBackendKind"/> onto Veldrid's own backend enum,
        /// behind a discard (<c>_ =&gt; GraphicsBackend.Metal</c>), so a missing arm was never a compile error
        /// and never a <c>SwitchExpressionException</c>: it was a wrong answer, and the worst of them asked for a
        /// Metal device on Windows. It is gone with the Veldrid backend, and what stands in its place is the
        /// property that made it dangerous being impossible: there is no map from a kind to an implementation any
        /// more, only the registry, and an unregistered kind throws by name.
        /// </summary>
        [Fact]
        public void ThereIsNoBackendMapLeftToGetWrong_OnlyTheRegistry()
        {
            var ex = Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuBackendProviders.Require((GpuBackendKind)9003));

            Assert.Contains("9003", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Register()", ex.Message, StringComparison.Ordinal);
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

        // --- the FIFTEENTH site, and the one BOTH appends walked past: the token list the unrecognized-override
        // WARN prints. It was a literal in GpuDeviceContext, so it named five tokens while the parser accepted
        // six, and the field consequence is the whole reason that warning exists. A typo'd KE_GRAPHICS_BACKEND
        // does not fail: the run boots on the OS probe and looks entirely normal, so this list is the only clue
        // the tester gets that their variable did nothing. One naming a token they cannot type, or missing the one
        // they meant, sends them hunting a machine problem instead. It is single-sourced on
        // GpuBackendSelector.RecognizedTokens now, and these rows are what keeps it agreeing with the switch.
        //
        // Here rather than in either native audit because it is a theory over EVERY member, which is this file's
        // half of the split. ---

        /// <summary>
        /// Every token the warning offers parses, and parses to a DISTINCT and LIVE backend. A list carrying a
        /// stale token is worse than a short one, because it tells the reader to type something the parser
        /// rejects, or (since 18.0.0) something that names a backend the engine no longer has.
        /// </summary>
        [Fact]
        public void EveryTokenTheWarningNames_Parses_ToADistinctLiveBackend()
        {
            var named = new List<GpuBackendKind>();

            foreach (string token in WarningTokens())
            {
                Assert.True(GpuBackendSelector.TryParseBackend(token, out GpuBackendKind backend), token);
                Assert.False(GpuBackendSelector.IsRetired(backend), token);
                Assert.DoesNotContain(backend, named);
                named.Add(backend);
            }
        }

        /// <summary>
        /// And every LIVE member is named by one of them. This is the half an append breaks: the appending change
        /// edits the parse switch because nothing works without it, then leaves the list alone because nothing
        /// fails. The four retired members are deliberately absent, which is the other half of the same
        /// agreement: they still PARSE, so a script that sets one keeps working, but a diagnostic must not OFFER
        /// a backend that no longer exists.
        /// </summary>
        [Fact]
        public void EveryLiveBackendKind_IsNamedByTheWarning_AndNoRetiredOneIs()
        {
            var named = new HashSet<GpuBackendKind>();
            foreach (string token in WarningTokens())
            {
                if (GpuBackendSelector.TryParseBackend(token, out GpuBackendKind backend))
                    named.Add(backend);
            }

            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
            {
                if (GpuBackendSelector.IsRetired(kind)) Assert.DoesNotContain(kind, named);
                else Assert.Contains(kind, named);
            }
        }

        /// <summary>
        /// The rest of the sentence, pinned in one row because the token list is only actionable beside it: the
        /// variable to fix, the value that did nothing, and the backend that ran instead of it.
        /// </summary>
        [Fact]
        public void TheWarning_NamesTheVariable_TheTypo_AndTheBackendThatRanInstead()
        {
            string warning = GpuDeviceContext.UnrecognizedOverrideWarning("vulcan", GpuBackendKind.Metal);

            Assert.Contains(GpuBackendSelector.EnvVarName, warning, StringComparison.Ordinal);
            Assert.Contains("'vulcan'", warning, StringComparison.Ordinal);
            Assert.Contains(nameof(GpuBackendKind.Metal), warning, StringComparison.Ordinal);
        }

        // The tokens as the tester reads them, taken out of the real warning text rather than off the constant, so
        // the pin covers the composition too: a message that stopped interpolating the list would still satisfy a
        // test that read the constant directly.
        static IEnumerable<string> WarningTokens()
        {
            string warning = GpuDeviceContext.UnrecognizedOverrideWarning("vulcan", GpuBackendKind.Metal);
            int open = warning.IndexOf('(');
            int close = warning.IndexOf(')');

            Assert.True(open >= 0 && close > open + 1, warning);
            return warning.Substring(open + 1, close - open - 1).Split('/');
        }

        // --- row 6: GpuBackendSelector.IsBackendSupported, asserted in GpuBackendKindAppendAuditRegistryTests
        // below. It registers a provider under the real kind, so it belongs off the parallel pool. ---

        // --- row 7: GpuBackendSelector.ProbeOS, FLIPPED at 17.40.0 (the decision of 2026-08-22, ahead of I4's
        // remaining gates) ---

        /// <summary>
        /// Every OS probes to the ENGINE'S OWN backend. This row read "still answers the incumbent" for three
        /// appends and was the single line each program's rollout was pointed at, so it is the row whose flip WAS
        /// the release. The incumbent map that used to be pinned beside it, <c>IncumbentFor</c>, was deleted in
        /// 18.0.0 with the backend it named, and the retired member each of its arms answered is asserted here
        /// instead: what the probe answers now is a LIVE backend on every OS, which is the property a fallback
        /// landing on the probe depends on.
        /// </summary>
        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11Native)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.VulkanNative)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.VulkanNative)]
        public void ProbeOS_AnswersTheNativeBackend_OnEveryOs(OSPlatformKind os, GpuBackendKind expected)
        {
            Assert.Equal(expected, GpuBackendSelector.ProbeOS(os));
            Assert.False(GpuBackendSelector.IsRetired(GpuBackendSelector.ProbeOS(os)));
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

        // --- row 11: GoldenCompare, at BOTH filename sites (decision I3, superseded at 17.41.0) ---

        /// <summary>
        /// OWNED FROM 17.41.0. Decision I3 made this kind a GUEST in the incumbent's <c>direct3d11</c> family,
        /// which was the strongest free proof the port had: the native backend was held to 36 already-committed
        /// reference grids, unmodified, on the same WARP rasterizer, at the same tolerance. Row 2 of the Veldrid
        /// removal (<c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c> section 3,
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/685">#685</see>) promoted it to owner of
        /// <c>direct3d11-native</c>, because the incumbent that owns <c>direct3d11</c> is being deleted and a
        /// family whose owner is gone is a set of references nothing may ever re-bake. The guest-era agreement is
        /// not thrown away: the new family was seeded as a byte-identical COPY, asserted cell for cell by
        /// <c>GoldenFamilyCopyGoldenTests</c>.
        /// </summary>
        [Fact]
        public void EachDirect3D11Implementation_OwnsItsOwnGoldenFamily()
        {
            Assert.Equal("direct3d11", GoldenCompare.GoldenBackendToken(GpuBackendKind.Direct3D11));
            Assert.Equal("direct3d11-native", GoldenCompare.GoldenBackendToken(GpuBackendKind.Direct3D11Native));
        }

        [Theory]
        [InlineData(GpuBackendKind.Metal, "metal")]
        [InlineData(GpuBackendKind.MetalNative, "metal-native")]
        [InlineData(GpuBackendKind.Vulkan, "vulkan")]
        [InlineData(GpuBackendKind.VulkanNative, "vulkan-native")]
        [InlineData(GpuBackendKind.OpenGL, "opengl")]
        public void EveryOtherBackend_KeepsItsOwnGoldenFamily(GpuBackendKind kind, string expected)
            => Assert.Equal(expected, GoldenCompare.GoldenBackendToken(kind));

        /// <summary>
        /// SIX LIVE TOKENS AND SEVEN DISTINCT ONES, which is the whole of row 2 stated as one claim. Asserted as
        /// a set rather than arm by arm because the failure this catches is two arms returning the same string,
        /// which every per-arm equality above passes right over: a duplicate token silently merges two families
        /// and the run compares against grids another implementation baked.
        /// </summary>
        [Fact]
        public void NoTwoBackendKinds_ShareAGoldenFamily()
        {
            var tokens = Enum.GetValues<GpuBackendKind>()
                .Select(GoldenCompare.GoldenBackendToken)
                .ToList();

            Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        }

        /// <summary>
        /// No member may be left without a decided family. The mapping throws rather than guessing, and this is
        /// what turns that throw into a device-free red instead of a golden-missing failure on a GPU leg nobody
        /// runs locally. Since 17.41.0 it also pins that every token is the kind's OWN name, which is the rule
        /// <c>GoldenCompare.OwnsFamily</c> derives ownership from rather than from a second list.
        /// </summary>
        [Fact]
        public void EveryBackendKind_HasADecidedGoldenFamily()
        {
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
            {
                string token = GoldenCompare.GoldenBackendToken(kind);
                Assert.False(string.IsNullOrWhiteSpace(token));
                Assert.Equal(token.ToLowerInvariant(), token);
                Assert.True(GoldenCompare.OwnsFamily(kind, token));
            }
        }

        [Fact]
        public void AnUnmappedKind_ThrowsRatherThanInventingAFamily()
            => Assert.Throws<NotSupportedException>(() => GoldenCompare.GoldenBackendToken((GpuBackendKind)9001));

        /// <summary>
        /// The bake, which this kind is now ALLOWED to take. It owns <c>direct3d11-native</c>, so
        /// <c>KE_UPDATE_GOLDENS</c> writes the family it is itself checked against and nothing else, which is
        /// what owning a family means.
        /// </summary>
        [Fact]
        public void Baking_IsAllowedOnTheNativeKind_BecauseItOwnsItsFamily()
            => Assert.Null(GoldenCompare.BakeRefusal(GpuBackendKind.Direct3D11Native, familyOverride: false));

        /// <summary>A backend that OWNS its family bakes as it always did, and since 17.41.0 that is every live
        /// member of the enum. The guard must not cost the ordinary rebake anything, or it gets worked around
        /// instead of respected.</summary>
        [Theory]
        [InlineData(GpuBackendKind.Metal)]
        [InlineData(GpuBackendKind.MetalNative)]
        [InlineData(GpuBackendKind.Vulkan)]
        [InlineData(GpuBackendKind.VulkanNative)]
        [InlineData(GpuBackendKind.Direct3D11)]
        [InlineData(GpuBackendKind.Direct3D11Native)]
        [InlineData(GpuBackendKind.OpenGL)]
        public void Baking_IsAllowedOnEveryBackendThatOwnsItsFamily(GpuBackendKind kind)
            => Assert.Null(GoldenCompare.BakeRefusal(kind, familyOverride: false));

        /// <summary>
        /// THE GUEST RULE OUTLIVES THE LAST GUEST, and this is where it stays under test. No live kind trips
        /// <c>BakeRefusal</c> any more, so the only way to keep the rule honest is to hand it a token the kind
        /// does not own, which the internal overload exists for: it takes the token rather than deriving it, so
        /// a guest pairing can be pinned without a fake enum member existing. The rule is what the NEXT append
        /// that decides to share rather than own will be judged by, and the failure mode it guards is that a
        /// shared bake is undetectable after the fact.
        /// </summary>
        [Fact]
        public void AGuestPairing_IsStillRefused_EvenThoughNoLiveKindIsAGuest()
        {
            string? refusal = GoldenCompare.BakeRefusal(
                GpuBackendKind.Direct3D11Native, "direct3d11", familyOverride: false);

            Assert.NotNull(refusal);
            Assert.Contains("KE_UPDATE_GOLDENS", refusal);
            // Actionable on its own: the reader is looking at a red bake they expected to be a write.
            Assert.Contains(GoldenCompare.FamilyOverrideEnvVar, refusal);

            Assert.Null(GoldenCompare.BakeRefusal(
                GpuBackendKind.Direct3D11Native, "direct3d11", familyOverride: true));
        }

        // --- row 13: GpuDeviceContext.CreateOrFallBack's requested-versus-fallback comparison ---

        /// <summary>
        /// A value comparison, not a switch, so it is invisible to any arm sweep. What it decides is whether a
        /// failed request has anywhere to go: a request that already IS the platform default short-circuits the
        /// guard, because falling back onto the backend that just refused would warn about a change that is not
        /// one and then fail again for the same reason.
        /// <para>
        /// The fallback target moved TWICE. It was <see cref="GpuBackendSelector.ProbeOS"/>, then 17.40.0 pointed
        /// it at the Veldrid incumbent so a failed native request could try the other implementation, and 18.0.0
        /// pointed it back at the probe because there is no other implementation. So on WINDOWS a
        /// <see cref="GpuBackendKind.Direct3D11Native"/> request is now its own fallback and correctly has
        /// nowhere to go, which is what this row records rather than asserts away.
        /// </para>
        /// </summary>
        [Fact]
        public void ANativeRequest_IsItsOwnFallback_OnExactlyItsOwnPlatform()
        {
            foreach (OSPlatformKind os in Enum.GetValues<OSPlatformKind>())
            {
                bool isOwnPlatform = GpuBackendSelector.ProbeOS(os) == GpuBackendKind.Direct3D11Native;
                Assert.Equal(isOwnPlatform, os == OSPlatformKind.Windows);
            }
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
    /// Rows 3, 6 and 8 of the append audit, split out of <see cref="GpuBackendKindAppendAuditTests"/> because
    /// they are the ones that touch the provider registry under the real
    /// <see cref="GpuBackendKind.Direct3D11Native"/> kind, and the registry plus the support cache behind it are
    /// process-wide.
    /// <para>
    /// What that costs on the parallel pool is a rare red with no bug under it:
    /// <c>GpuBackendSelectorTests.IsBackendSupported_NeverThrows_ForAnyBackend</c> walks every enum member and
    /// probes it, so a concurrent run of it lands on a fake in the window where it is registered and bumps the
    /// very probe counter row 6 asserts is exactly one. Registering under a SENTINEL kind is the other way out and
    /// is what <c>GpuBackendProvidersTests</c> does, but these rows are auditing the REAL member, which is the
    /// whole point of them, so the collection is the fix. The cost is three tests off the pool, not the other
    /// twenty-odd pure ones.
    /// </para>
    /// <para>
    /// Every row here that means "nothing is registered" now says so with an explicit
    /// <c>BackendProviderScope(kind, provider: null)</c>, because nothing is no longer the ambient state:
    /// <c>KhaozEngine.Gpu.D3D11</c> exists and a static constructor on <c>GpuFactAttribute</c> in the shared
    /// <c>KhaozEngine.TestSupport.Gpu</c> project registers its REAL provider, fired at test discovery
    /// (<c>KhaozEngine.TestSupport.Gpu/D3D11BackendRegistration.cs</c>), with a thin module-initializer belt
    /// remaining in <c>KhaozEngine.Render.Tests</c> covering the registry tests that carry no <c>[GpuFact]</c>.
    /// Pinning the unregistered behaviour explicitly is the stronger form anyway: it asserts what the
    /// code does when no provider is present rather than what it happens to do given today's ambient
    /// registration, so it keeps holding the day a second backend package registers here too.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class GpuBackendKindAppendAuditRegistryTests
    {
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
            using (Unregistered(GpuBackendKind.Direct3D11Native))
            {
                GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                    () => GpuDeviceContext.CreateForWindow(default, 640, 480, true, GpuBackendKind.Direct3D11Native));

                Assert.Equal(GpuBackendKind.Direct3D11Native, ex.Backend);
                Assert.DoesNotContain("Metal", ex.Message);
                // The actionable line stays THIS backend's own. Pinned once the second provider-backed backend
                // arrived and the message stopped being able to name one entry point as though it were generic.
                Assert.Contains("KhaozEngineD3D11.Register()", ex.Message, StringComparison.Ordinal);
            }
        }

        // --- row 6: GpuBackendSelector.IsBackendSupported ---

        /// <summary>
        /// Veldrid cannot answer for a backend it does not implement, so the native kind is routed to its
        /// provider's own functional probe. With no provider the answer is false, and it is not cached as false,
        /// so registering later still gets to answer for real.
        /// </summary>
        [Fact]
        public void IsBackendSupported_AsksTheNativeProvider_NotVeldrid()
        {
            using (Unregistered(GpuBackendKind.Direct3D11Native))
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
        }

        // --- row 8: GpuBackendSelector._windowCandidates, FLIPPED at 17.40.0 with the default ---

        /// <summary>
        /// The native kind is OFFERED now, and the objection that kept it off the list is answered rather than
        /// waived: a settings screen offers an API and not an implementation of one, and the native row is what
        /// "Direct3D 11" MEANS from this release. The incumbent row beside it is the one-release opt-out.
        /// <para>
        /// Asserted with the provider registered and reporting SUPPORTED, because the offered list is probed:
        /// with no provider the kind answers unsupported and a game that never took the package still sees the
        /// list it always saw. That half is pinned below.
        /// </para>
        /// </summary>
        [Fact]
        public void SupportedBackends_OffersTheNativeKind_ToAPlayer()
        {
            using (Registered(GpuBackendKind.Direct3D11Native,
                new FakeBackendProvider(GpuBackendKind.Direct3D11Native) { Supported = true }))
            {
                Assert.Contains(GpuBackendKind.Direct3D11Native, GpuBackendSelector.SupportedBackends());
            }
        }

        /// <summary>
        /// The other half: with NO provider registered the native kind is not offered, so repinning the engine
        /// cannot put a row in a game's graphics dropdown that its own process could never create.
        /// </summary>
        [Fact]
        public void SupportedBackends_OmitsTheNativeKind_WhenNoProviderIsRegistered()
        {
            using (Unregistered(GpuBackendKind.Direct3D11Native))
            {
                Assert.DoesNotContain(GpuBackendKind.Direct3D11Native, GpuBackendSelector.SupportedBackends());
            }
        }

        static BackendProviderScope Registered(GpuBackendKind backend, IGpuBackendProvider provider)
            => new(backend, provider);

        // Temporarily takes the backend OUT of the registry and puts back whatever was there. Named so a reader
        // sees the intent at the call site: these rows assert what happens with no provider, which is a state the
        // test has to create now rather than one it inherits.
        static BackendProviderScope Unregistered(GpuBackendKind backend) => new(backend, provider: null);
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
