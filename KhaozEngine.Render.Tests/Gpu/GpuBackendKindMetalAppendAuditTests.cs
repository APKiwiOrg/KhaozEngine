using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The APPEND AUDIT for <see cref="GpuBackendKind.MetalNative"/>, the third one. Section 4.2 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> walks the same sites
    /// <see cref="GpuBackendKindAppendAuditTests"/> discovered for Direct3D 11 and
    /// <see cref="GpuBackendKindVulkanAppendAuditTests"/> answered for Vulkan, and this file answers them for
    /// Metal in the order that table lists them. Device-free, so the whole audit runs under a plain
    /// <c>dotnet test</c> on any OS.
    /// <para>
    /// THIS IS THE APPEND WHERE THE AUDIT STOPS BEING A FORMALITY. The first two appends found sites that were
    /// correct by default and rows that needed no edit. This one has THREE sites that answer differently from
    /// both predecessors and all three degrade SILENTLY: <see cref="FrameCap.Resolve"/> and
    /// <see cref="DisplaySettings.RequiresFrameCapWarning"/>, which apply a real software cap only on Metal and
    /// would have taken it away from a native Mac client without failing anything, and the Veldrid wrapper's
    /// frame-capture gate, which arms nothing on a native session and produces an empty output directory rather
    /// than an error. Nothing about those three is derivable from how the previous two appends went, which is the
    /// argument for walking the table every time rather than diffing the last one.
    /// </para>
    /// <para>
    /// The first two of those three took the incumbent's capped arm as a CONSERVATIVE DEFAULT rather than as a
    /// finding, and rollout gate 5 has since measured them (2026-08-11): the native present throttles the CPU
    /// from vsync alone, so both flipped to the incumbent alone. The rows below pin the measured answer, and each
    /// one asserts the incumbent's side too, because a flip that moved every backend would satisfy a one-sided
    /// assertion just as well as the right one does.
    /// </para>
    /// <para>
    /// Rows 3, 6 and 8 are in <see cref="GpuBackendKindMetalAppendAuditRegistryTests"/> for the reason both
    /// earlier audits split the same three out: they touch the process-wide provider registry under the REAL
    /// kind, so they cannot share the parallel pool.
    /// </para>
    /// </summary>
    public sealed class GpuBackendKindMetalAppendAuditTests
    {
        // --- the enum itself (decision M-I1). The ordinal is pinned in GpuBackendKindAppendAuditTests, with the
        // other five, because "no member ever moves" is one claim about one enum and splitting it across three
        // files is how two thirds of it stops being checked. ---

        /// <summary>
        /// A separate member from the Veldrid Metal kind, which is what makes the telemetry header, the session
        /// log and a golden filename each name the implementation that actually ran. It matters more here than in
        /// either predecessor: the incumbent Metal leg is the one the fleet's reference images are baked on, so a
        /// measurement attributed to the wrong Metal is a measurement against the reference itself.
        /// </summary>
        [Fact]
        public void TheNativeKind_IsDistinctFromTheVeldridOne()
            => Assert.NotEqual(GpuBackendKind.Metal, GpuBackendKind.MetalNative);

        /// <summary>
        /// Both implementations answer the Metal family predicate, and nothing else does (decision M-I5). It
        /// landed with two readers waiting, the frame-cap pair below, and gate 5's measurement then took both of
        /// them back to an equality against the incumbent, so the predicate has none today. It is still pinned
        /// here: it is public API, and the question it asks is the right one for the next site that reasons about
        /// the Metal API rather than about Veldrid's implementation of it.
        /// </summary>
        [Fact]
        public void IsMetal_CoversBothImplementations_AndNothingElse()
        {
            Assert.True(GpuBackendKind.Metal.IsMetal());
            Assert.True(GpuBackendKind.MetalNative.IsMetal());
            Assert.False(GpuBackendKind.Vulkan.IsMetal());
            Assert.False(GpuBackendKind.VulkanNative.IsMetal());
            Assert.False(GpuBackendKind.Direct3D11.IsMetal());
            Assert.False(GpuBackendKind.Direct3D11Native.IsMetal());
            Assert.False(GpuBackendKind.OpenGL.IsMetal());
        }

        // --- rows 1 and 2: GpuDeviceContext.LogThreadingCaps and D3D11ThreadingProbe.IsApplicable. NO CHANGE,
        // for the reason the Vulkan append records: both gate on IsDirect3D11, which correctly excludes both
        // Metal implementations, and there is no D3D11_FEATURE_DATA_THREADING analogue to log. ThreadingCaps and
        // ThreadingProbeFailure are both null on this backend, which the record documents as "there was nothing
        // to ask". Pinned by the row added to
        // GpuBackendKindAppendAuditTests.ThreadingProbe_AppliesToBothDirect3D11Implementations, which is one
        // theory over one probe and gains an InlineData rather than a copy. ---

        // --- row 3: the CreateForWindow / CreateHeadless Veldrid switches. In the registry class below. ---

        /// <summary>
        /// The fourteenth site, stated so it needs no edit. It was "everything the built-in path does not build"
        /// over a positive membership test across the four Veldrid kinds, which made an APPENDED member
        /// provider-backed with no edit at all. 18.0.0 deleted the built-in path, so
        /// <see cref="GpuBackendProviders.RequiresProvider"/> is now unconditionally true and the site stopped
        /// being an append site entirely. Forgetting a registration still throws a message naming it.
        /// </summary>
        [Fact]
        public void TheNativeKind_IsProviderBacked_WithNoEditToTheRegistry()
        {
            Assert.True(GpuBackendProviders.RequiresProvider(GpuBackendKind.MetalNative));
            Assert.True(GpuBackendProviders.RequiresProvider(GpuBackendKind.Metal));
        }

        // --- row 4: GpuBackendSelector.ToVeldrid, one more explicit throwing arm ---

        /// <summary>
        /// There WAS a <c>GraphicsBackend.Metal</c>, so this arm mattered for the reason the Vulkan one did and
        /// then some. Mapping the native kind onto it would not have failed: it would have built the INCUMBENT
        /// Veldrid Metal device, on the one platform where that device was what the OS probe answered anyway, and
        /// attributed a whole soak session to the implementation that did not run. The discard arm it replaced
        /// answered <c>Metal</c> for every unlisted member, so of the three appends this was the one where the
        /// old bug's wrong answer would have looked entirely correct. The map went with the backend in 18.0.0,
        /// and the retired member cannot build anything at all now.
        /// </summary>
        [Fact]
        public void TheRetiredIncumbentKind_CannotBuildADevice_RatherThanQuietlyBuildingOne()
        {
            var ex = Assert.Throws<GpuBackendRetiredException>(
                () => GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(GpuBackendKind.Metal, GpuBackendSource.OsProbe, null),
                    allowFallback: true, out _));

            Assert.Equal(GpuBackendKind.MetalNative, ex.Replacement);
        }

        // --- row 5: GpuBackendSelector.TryParseBackend, the two new tokens (M-I1) ---

        [Theory]
        [InlineData("metal-native")]
        [InlineData("mtl-native")]
        [InlineData("Metal-Native")]
        [InlineData("  MTL-NATIVE\t")]
        public void TryParseBackend_RecognizesBothNativeTokens(string value)
        {
            Assert.True(GpuBackendSelector.TryParseBackend(value, out GpuBackendKind backend));
            Assert.Equal(GpuBackendKind.MetalNative, backend);
        }

        /// <summary>
        /// The suffix must not bleed either way, and the incumbent token keeps pointing at the incumbent until
        /// the program's closing act (decision M-RO2), which since the 17.40.0 flip is ONE release away. That is
        /// the kill switch this design leans on hardest, because it is the only way to A/B a suspected
        /// difference on a Mac against the very references the fleet's goldens are baked from, on one build,
        /// with no re-bake. It is also how a Mac BAKES now, since the default is a guest of the family it would
        /// overwrite.
        /// </summary>
        [Fact]
        public void TryParseBackend_KeepsTheIncumbentTokenPointingAtTheIncumbent()
        {
            Assert.True(GpuBackendSelector.TryParseBackend("metal", out GpuBackendKind backend));
            Assert.Equal(GpuBackendKind.Metal, backend);
        }

        [Theory]
        [InlineData("metalnative")]
        [InlineData("metal_native")]
        [InlineData("mtlnative")]
        [InlineData("metal-nativ")]
        [InlineData("mtl")]
        public void TryParseBackend_RejectsANearMissRatherThanGuessing(string value)
        {
            Assert.False(GpuBackendSelector.TryParseBackend(value, out _));
            // And an unrecognized value keeps its raw text for the diagnostic, so the tester is told their
            // variable did nothing instead of reading the OS default as the backend they asked for.
            GpuBackendSelection selection = GpuBackendSelector.Resolve(value, OSPlatformKind.MacOS);
            Assert.Equal(GpuBackendSource.UnrecognizedOverride, selection.Source);
            Assert.Equal(value, selection.RequestedOverride);
        }

        /// <summary>
        /// The whole point of the token: a field soak sets it, so it has to reach the selection. Driven against
        /// macOS, where the probe it has to beat answers <see cref="GpuBackendKind.Metal"/>, the SAME API, so an
        /// override that silently failed to parse would land on the incumbent implementation of the very backend
        /// the soak is measuring and the session would look entirely correct. Identical in shape to the Vulkan
        /// row driven against Linux, and worse in consequence, because gate 4 has to TAKE its own incumbent
        /// baseline first and a mis-parsed token would have it measure the incumbent twice.
        /// </summary>
        [Theory]
        [InlineData("metal-native")]
        [InlineData("mtl-native")]
        public void ANativeTokenOverride_WinsOverTheMacOsProbe_AndReportsItself(string token)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(token, OSPlatformKind.MacOS);

            Assert.Equal(GpuBackendKind.MetalNative, selection.Backend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.Equal(token, selection.RequestedOverride);
        }

        /// <summary>
        /// The token list the unrecognized-override WARN prints gains ONE of the pair, not both, and this is the
        /// row that says which and why. <c>GpuBackendSelector.RecognizedTokens</c> is what a reader is asked to
        /// TYPE, so it carries one canonical token per backend and the aliases stay out of it, exactly as
        /// <c>vk-native</c>, <c>direct3d11</c> and <c>opengl</c> already do. That is not a style preference: the
        /// every-member rows in <see cref="GpuBackendKindAppendAuditTests"/> require each listed token to parse
        /// to a DISTINCT backend, so listing both would make the warning claim seven choices where six exist.
        /// </summary>
        [Fact]
        public void TheWarningNamesTheCanonicalToken_AndLeavesTheAliasOut()
        {
            string warning = GpuDeviceContext.UnrecognizedOverrideWarning("metel", GpuBackendKind.Metal);

            Assert.Contains("metal-native", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("mtl-native", warning, StringComparison.Ordinal);
            // And the alias still parses, which is the half that would otherwise read as an oversight.
            Assert.True(GpuBackendSelector.TryParseBackend("mtl-native", out GpuBackendKind alias));
            Assert.Equal(GpuBackendKind.MetalNative, alias);
        }

        // --- row 6: GpuBackendSelector.IsBackendSupported. In the registry class below. ---

        // --- row 7: GpuBackendSelector.ProbeOS, FLIPPED at 17.40.0, and the arm with the largest blast radius
        // of the three ---

        /// <summary>
        /// macOS probes to the native backend since 17.40.0, and macOS is the operating system this member's
        /// flip changed hardest. That is the difference from both earlier versions of this row: Windows and
        /// Linux are player platforms, macOS is the fleet's DEVELOPMENT platform, so this arm moved every
        /// windowed playtest, every capture, every editor session and every local golden bake onto the native
        /// backend on the day it landed. A local bake is the sharp one: the native kind is a GUEST in the
        /// <c>metal</c> golden family, so a bare local <c>KE_UPDATE_GOLDENS=1</c> is now REFUSED and a bake has
        /// to name the incumbent.
        /// <para>
        /// Only the macOS row is here. The full four-OS mapping is the same assertion for all three appends, so
        /// it is pinned once in <c>GpuBackendKindAppendAuditTests.ProbeOS_AnswersTheNativeBackend_OnEveryOs</c>,
        /// and "this kind is never what the fallback answers" is walked over every OS by
        /// <see cref="ANativeRequest_IsItsOwnFallback_OnExactlyItsOwnPlatform"/> below.
        /// </para>
        /// </summary>
        [Fact]
        public void ProbeOS_AnswersTheNativeBackend_OnMacOs()
        {
            Assert.Equal(GpuBackendKind.MetalNative, GpuBackendSelector.ProbeOS(OSPlatformKind.MacOS));
            // And it is what a failed creation falls back to as well since 18.0.0, the incumbent map having gone
            // with the incumbent. There is one macOS answer now rather than two.
            Assert.False(GpuBackendSelector.IsRetired(GpuBackendSelector.ProbeOS(OSPlatformKind.MacOS)));
        }

        // --- row 8: GpuBackendSelector._windowCandidates. In the registry class below. ---

        // --- rows 9 and 10: FrameCap.Resolve and DisplaySettings.RequiresFrameCapWarning. THE FIRST TWO OF THE
        // THREE SILENT SITES, and the first append for which these rows are not correct by default. Both apply a
        // real software cap only on Metal, so an appended Metal member left in the uncapped arm would take the
        // cap away from a native Mac client with nothing failing anywhere. Both routed through IsMetal() at the
        // append because decision M-W3 rules that the ARM is a gate-5 MEASUREMENT rather than an assumption, and
        // the capped arm was the conservative default until it was read.
        //
        // GATE 5 READ IT ON 2026-08-11 AND THE ARM FLIPPED FOR MetalNative. Three legs: an uncapped 8000-frame
        // field capture with vsync on blocked in the drawable acquire exactly 1.000 times per frame for 15.175 ms
        // against a 16.669 ms median frame, a human windowed pass on a display pinned to 120 Hz sat at 120 fps,
        // and toggling vsync OFF mid-session jumped past 700 fps with visible tearing, which is what rules out
        // any other bottleneck as the source of the pacing. The native present throttles the CPU from vsync
        // alone, exactly as Direct3D11Native's and VulkanNative's do, so a software cap at the refresh cannot
        // bind and the warning would be noise. The capped arm is the incumbent's alone again. ---

        /// <summary>
        /// The native kind is UNCAPPED where the incumbent caps, which is gate 5's measured answer rather than a
        /// default. Both halves are asserted in one row deliberately: the interesting failure is not "the native
        /// kind reads 0", it is the two kinds agreeing, which is what an over-wide edit in either direction would
        /// produce and what the conservative arm this replaced actually did.
        /// </summary>
        [Fact]
        public void FrameCapAuto_LeavesTheNativeKindUncapped()
        {
            Assert.Equal(0, FrameCap.Auto.Resolve(GpuBackendKind.MetalNative, PresentMode.Vsync, 144));

            // The no-live-refresh arm too, since a windowed Mac session with no refresh rate to read is the case
            // FrameCap.DefaultMetalAutoCapHz existed for, and it is the arm that would silently reintroduce a
            // 120 Hz cap on a backend measured not to need one.
            Assert.Equal(0, FrameCap.Auto.Resolve(GpuBackendKind.MetalNative, PresentMode.Vsync, displayRefreshHz: 0));
        }

        /// <summary>
        /// An Immediate present is still an intentional free-run on the native kind. Implied by the row above now
        /// that the whole backend is uncapped, and kept because it is the pin that survives the arm moving again:
        /// #380's present-pacing work is stated against exactly this site.
        /// </summary>
        [Fact]
        public void FrameCapAuto_LeavesAnImmediatePresentUncapped_OnTheNativeKind()
            => Assert.Equal(0, FrameCap.Auto.Resolve(GpuBackendKind.MetalNative, PresentMode.Immediate, 144));

        /// <summary>
        /// An explicit consumer choice still outranks the backend-aware default on the native kind, both ways.
        /// This is what the flip did NOT change and the half most easily lost with it: a consumer that asks for
        /// 30 Hz on a Mac still gets 30 Hz, whatever the present does on its own.
        /// </summary>
        [Fact]
        public void AnExplicitCap_StillOutranksTheDefault_OnTheNativeKind()
        {
            Assert.Equal(0, FrameCap.Uncapped.Resolve(GpuBackendKind.MetalNative, PresentMode.Vsync, 144));
            Assert.Equal(30, FrameCap.Hz(30).Resolve(GpuBackendKind.MetalNative, PresentMode.Vsync, 144));
        }

        /// <summary>
        /// And the warning is SILENT on the native kind, because the two sites take one decision and a session
        /// that capped without saying so (or said so without capping) would be the pair disagreeing. Vsync plus
        /// an uncapped frame rate is a healthy configuration there, so the warning telling a tester to set a cap
        /// would be advice against the measurement.
        /// </summary>
        [Theory]
        [InlineData(PresentMode.Vsync, 0)]
        [InlineData(PresentMode.Vsync, 60)]
        [InlineData(PresentMode.Immediate, 0)]
        public void FrameCapWarning_IsSilentOnTheNativeKind(PresentMode present, int frameCapHz)
            => Assert.False(DisplaySettings.RequiresFrameCapWarning(
                GpuBackendKind.MetalNative, present, frameCapHz));

        /// <summary>
        /// The two sites agree on every backend, which is the claim that actually matters and the one neither
        /// site can make alone: the warning tells a consumer to set a cap precisely when the default would not
        /// supply one. Walked over every member so a fourth append cannot move one site and not the other, and it
        /// is what would have caught gate 5's flip landing at one site only.
        /// </summary>
        [Fact]
        public void TheTwoFrameCapSites_AgreeOnEveryBackend()
        {
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
            {
                foreach (PresentMode present in Enum.GetValues<PresentMode>())
                {
                    bool capped = FrameCap.Auto.Resolve(kind, present, displayRefreshHz: 60) > 0;
                    bool warns = DisplaySettings.RequiresFrameCapWarning(kind, present, frameCapHz: 0);
                    Assert.Equal(capped, warns);
                }
            }
        }

        /// <summary>
        /// The capped arm is EMPTY since 18.0.0, walked over the whole enum. It was one member wide, the Veldrid
        /// Metal incumbent, and gate 5 measured that every native backend's present throttles the CPU from vsync
        /// (MetalNative by its own three legs, the other two by their incumbents'). Deleting the incumbent left
        /// nothing in the arm, and this is the row that goes red the day something is put back into it without
        /// the measurement to justify it.
        /// </summary>
        [Fact]
        public void NoKindIsInTheCappedArm()
        {
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
            {
                Assert.Equal(0, FrameCap.Auto.Resolve(kind, PresentMode.Vsync, 144));
                Assert.False(DisplaySettings.RequiresFrameCapWarning(kind, PresentMode.Vsync, 0));
            }
        }

        // --- row 11: GoldenCompare, at BOTH filename sites (decision M-I3, superseded at 17.41.0) ---

        /// <summary>
        /// OWNED FROM 17.41.0, and this is the pair the reversal costs the most. M-I3 made the native backend a
        /// guest, held to the incumbent's already-committed reference grids, unmodified, on the same real Apple
        /// hardware, at the same tolerance, and owning a <c>metal-native</c> family was rejected for the reason
        /// it was rejected twice before plus one this family alone carries: <c>metal</c> is the FLEET's
        /// cross-backend reference, so forking it in two would leave the fleet with two references and no way to
        /// say which is the one. Row 2 of the Veldrid removal
        /// (<c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c> section 3,
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/685">#685</see>) forked it anyway, because
        /// the incumbent that owns <c>metal</c> is being deleted and the alternative is a fleet reference nothing
        /// may ever re-bake. The fork is not a second opinion: <c>metal-native</c> was seeded as a byte-identical
        /// COPY of <c>metal</c> (<c>GoldenFamilyCopyGoldenTests</c>), so it INHERITS the standing rather than
        /// competing with it, and when row 4 deletes <c>metal</c> the fleet is back to one reference holding the
        /// same bytes it holds today.
        /// </summary>
        [Fact]
        public void EachMetalImplementation_OwnsItsOwnGoldenFamily()
        {
            Assert.Equal("metal", GoldenCompare.GoldenBackendToken(GpuBackendKind.Metal));
            Assert.Equal("metal-native", GoldenCompare.GoldenBackendToken(GpuBackendKind.MetalNative));
        }

        /// <summary>
        /// And it does not disturb the other two native families. Asserted because all three native mappings are
        /// arms of one switch, so the cheapest way to get this wrong is an edit that points the new arm at the
        /// wrong string and moves nothing else, which no golden run on any backend would notice until a filename
        /// is built.
        /// </summary>
        [Fact]
        public void TheThreeNativeMappings_DoNotCrossOver()
        {
            Assert.Equal("metal-native", GoldenCompare.GoldenBackendToken(GpuBackendKind.MetalNative));
            Assert.Equal("vulkan-native", GoldenCompare.GoldenBackendToken(GpuBackendKind.VulkanNative));
            Assert.Equal("direct3d11-native", GoldenCompare.GoldenBackendToken(GpuBackendKind.Direct3D11Native));
        }

        /// <summary>
        /// The bake, which needed no edit to the refusal again because ownership is derived GENERICALLY: the
        /// token matches the kind's own name under the <c>OrdinalIgnoreCase</c> compare <c>BakeRefusal</c>
        /// already used, hyphen removed. Asserted precisely because "no edit needed" is indistinguishable from
        /// "nobody checked", and on THIS family the cost of being wrong is still the highest in the repo: a bake
        /// into <c>metal</c> overwrites the references every other family is read in relation to, and a bake into
        /// <c>metal-native</c> ends the copy invariant that carries the guest-era agreement forward.
        /// </summary>
        [Fact]
        public void Baking_IsAllowedOnTheNativeKind_BecauseItOwnsItsFamily()
        {
            Assert.Null(GoldenCompare.BakeRefusal(GpuBackendKind.MetalNative, familyOverride: false));

            // The rule that used to fire here still fires, against a token this kind does not own. The owning
            // backend is nameable from the message alone, which is the action the reader has to take.
            string? refusal = GoldenCompare.BakeRefusal(
                GpuBackendKind.MetalNative, "metal", familyOverride: false);
            Assert.NotNull(refusal);
            Assert.Contains("metal", refusal);
            Assert.Contains(GoldenCompare.FamilyOverrideEnvVar, refusal);
        }

        // That the incumbent still bakes as it always did is the other half of this row, and it is not asserted
        // here: GpuBackendKindAppendAuditTests.Baking_IsAllowedOnEveryBackendThatOwnsItsFamily already walks all
        // SEVEN owning backends since 17.41.0, Metal and MetalNative among them, which is the stronger form of
        // the same claim.

        // --- row 12: VeldridMap.SupportsCompletionFences, and the VeldridGpuDevice frame-capture gate, which is
        // THE THIRD SILENT SITE. Both were deleted in 18.0.0. The first was not an append site at all: it
        // switched on Veldrid's own GraphicsBackend and already answered true for GraphicsBackend.Metal, which is
        // why M-F4 is parity here rather than the upgrade it was on Direct3D 11. ---

        /// <summary>
        /// The frame-capture gate, which had a second arm until 18.0.0. It was the Veldrid Metal kind ALONE, and
        /// widening it to <see cref="GpuBackendKinds.IsMetal"/> read like the fix and fixed nothing, because the
        /// check lived inside the Veldrid device wrapper that a provider-built native device never becomes. The
        /// wrapper is gone, so the gate is gone with it: the native Metal device services an armed capture
        /// itself, with its own command-queue pointer in hand, which is what removed the reflection into
        /// Veldrid's private <c>_commandQueue</c> field on that path (decision M-G5).
        /// <para>
        /// What is left to pin is that the arm is BACKEND-BLIND now. Arming is a process-wide one-shot, and the
        /// device that consumes it is the one that knows whether it can, so nothing has to decide from a kind.
        /// </para>
        /// </summary>
        [Fact]
        public void TheFrameCaptureArm_IsBackendBlind_AndConsumedByTheDeviceThatCanServiceIt()
        {
            Assert.False(GpuFrameCapture.IsArmed);

            GpuFrameCapture.ArmNext("/tmp/khaozengine-append-audit.gputrace");
            try
            {
                Assert.True(GpuFrameCapture.IsArmed);
            }
            finally
            {
                Assert.True(GpuFrameCapture.TryConsume(out _));
            }

            Assert.False(GpuFrameCapture.IsArmed);
            // Stated as the thing a later reader is most likely to reintroduce: the family predicate still
            // exists, and there is no longer any capture site for it to gate.
            Assert.True(GpuBackendKind.MetalNative.IsMetal());
        }

        // --- row 13: GpuDeviceContext.CreateOrFallBack's requested-versus-fallback comparison ---

        /// <summary>
        /// A value comparison rather than a switch, so it is invisible to any arm sweep, and correct by default
        /// for a reason worth pinning: the native kind is never EQUAL to what
        /// <see cref="GpuBackendSelector.ProbeOS"/> returns on any OS, so a native request never short-circuits
        /// the "nothing to fall back TO" guard and always routes through the functional probe.
        /// <para>
        /// THE ANSWER INVERTED IN 18.0.0. Until then the fallback target was the Veldrid incumbent, so a macOS
        /// <see cref="GpuBackendKind.MetalNative"/> request whose creation failed fell back to the incumbent
        /// Metal backend. With the incumbent deleted the target is the probe itself, so on macOS the request is
        /// its own fallback and correctly gets a hard failure rather than a retry onto the backend that just
        /// refused. What decision M-I4 is about survives unchanged: a machine failure reports
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/> and a missing REGISTRATION throws.
        /// </para>
        /// </summary>
        [Fact]
        public void ANativeRequest_IsItsOwnFallback_OnExactlyItsOwnPlatform()
        {
            foreach (OSPlatformKind os in Enum.GetValues<OSPlatformKind>())
            {
                bool isOwnFallback = GpuBackendSelector.ProbeOS(os) == GpuBackendKind.MetalNative;
                Assert.Equal(isOwnFallback, os == OSPlatformKind.MacOS);
            }
        }

        // --- decision M-I1's other half: no new telemetry field ---

        /// <summary>
        /// The session header writes the enum NAME, so the attribution gate 4 depends on is carried with no field
        /// added and no code changed. Under the rejected shape, where both implementations reused
        /// <see cref="GpuBackendKind.Metal"/>, every existing reader would report <c>"Metal"</c> and ignore
        /// whatever secondary field said which one ran.
        /// </summary>
        [Fact]
        public void TheSessionHeader_NamesTheNativeBackend_WithNoNewField()
        {
            var selection = new GpuBackendSelection(
                GpuBackendKind.MetalNative, GpuBackendSource.EnvironmentOverride, "metal-native");

            var info = new TelemetrySessionInfo().WithGpu(selection, "Apple M2 Max", null, null);

            Assert.Equal("MetalNative", info.GpuBackend);
            Assert.Equal("EnvironmentOverride", info.GpuBackendSource);
            Assert.Equal("metal-native", info.GpuRequestedOverride);
            // And the two driver-threading fields stay ABSENT, which is rows 1 and 2's answer showing up in the
            // artifact they feed: there is no D3D11_FEATURE_DATA_THREADING analogue on this backend, so "there
            // was nothing to ask" has to read as null rather than as a default.
            Assert.Null(info.DriverCommandLists);
            Assert.Null(info.DriverConcurrentCreates);
        }
    }

    /// <summary>
    /// Rows 3, 6 and 8 of the Metal append audit, split out of
    /// <see cref="GpuBackendKindMetalAppendAuditTests"/> because they touch the process-wide provider registry
    /// under the real <see cref="GpuBackendKind.MetalNative"/> kind. The full reasoning for the split, and for
    /// the collection, is on <see cref="GpuBackendKindAppendAuditRegistryTests"/> and is not repeated.
    /// <para>
    /// Every row here that means "nothing is registered" says so explicitly with a scope rather than leaning on
    /// the ambient state, which is the same shape the Vulkan rows use and is the stronger form either way: it
    /// asserts what the code does with no provider present rather than what it happens to do given whatever
    /// <c>KhaozEngine.TestSupport.Gpu</c> registers at discovery on the day the test runs.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class GpuBackendKindMetalAppendAuditRegistryTests
    {
        // --- row 3: the CreateForWindow / CreateHeadless Veldrid switches. RIDES the arm phase 2 made explicit,
        // and the obligation is a VERIFICATION rather than an edit: the message has to name the provider registry
        // generically, or a Metal wiring fault reads as somebody else's. ---

        /// <summary>
        /// The native kind never reaches the Veldrid creation switch, because every entry into that path branches
        /// on <see cref="GpuBackendProviders.RequiresProvider"/> first. With nothing registered the observable
        /// outcome is the provider-missing exception stating the naming CONVENTION, which is the phase-3 fix
        /// paying out: written as a switch it would need an arm here, and written as one package's entry point it
        /// would be telling a Metal tester to register Direct3D 11.
        /// <para>
        /// Note what this row canNOT assert, and where it differs from both predecessors': they check the message
        /// does not contain "Metal", because the pre-phase-2 discard arm asked Veldrid for a Metal device. Here
        /// the kind IS Metal, so that check would be asserting the message never names the backend it is about.
        /// </para>
        /// </summary>
        [Fact]
        public void CreateForWindow_OnTheNativeKind_NeverAsksVeldridForADevice()
        {
            using (Unregistered(GpuBackendKind.MetalNative))
            {
                GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                    () => GpuDeviceContext.CreateForWindow(default, 640, 480, true, GpuBackendKind.MetalNative));

                Assert.Equal(GpuBackendKind.MetalNative, ex.Backend);
                Assert.Contains(nameof(GpuBackendKind.MetalNative), ex.Message, StringComparison.Ordinal);
                // The actionable line is the convention, which is what degrades correctly for a backend whose
                // package the message predates.
                Assert.Contains("KhaozEngine<Backend>.Register()", ex.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The headless twin, through the public named-backend overload, which is the entry a backend-parity
        /// harness uses. Same exception, same convention in the message.
        /// </summary>
        [Fact]
        public void CreateHeadless_OnTheNativeKind_ThrowsNamingTheMissingRegistration()
        {
            using (Unregistered(GpuBackendKind.MetalNative))
            {
                GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                    () => GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative));

                Assert.Equal(GpuBackendKind.MetalNative, ex.Backend);
                Assert.Contains("KhaozEngine<Backend>.Register()", ex.Message, StringComparison.Ordinal);
            }
        }

        // --- row 6: GpuBackendSelector.IsBackendSupported ---

        /// <summary>
        /// Veldrid cannot answer for a backend it does not implement, and here it would answer WRONGLY rather
        /// than not at all: <c>GraphicsDevice.IsBackendSupported(GraphicsBackend.Metal)</c> is a perfectly good
        /// answer about a different implementation, and on a Mac it is a true one. So the native kind routes to
        /// its own provider's functional probe. With no provider the answer is false and is not cached as false,
        /// so registering later still gets to answer for real.
        /// </summary>
        [Fact]
        public void IsBackendSupported_AsksTheNativeProvider_NotVeldrid()
        {
            using (Unregistered(GpuBackendKind.MetalNative))
            {
                Assert.False(GpuBackendSelector.IsBackendSupported(GpuBackendKind.MetalNative));

                var provider = new FakeBackendProvider(GpuBackendKind.MetalNative) { Supported = true };
                using (Registered(GpuBackendKind.MetalNative, provider))
                {
                    Assert.True(GpuBackendSelector.IsBackendSupported(GpuBackendKind.MetalNative));
                    Assert.Equal(1, provider.SupportProbes);
                }

                Assert.False(GpuBackendSelector.IsBackendSupported(GpuBackendKind.MetalNative));
            }
        }

        // --- row 8: GpuBackendSelector._windowCandidates, FLIPPED at 17.40.0 with the default ---

        /// <summary>
        /// The native kind is OFFERED now, because it is what "Metal" means on macOS from this release.
        /// Asserted with the provider registered and reporting SUPPORTED, since the offered list is probed and
        /// an unregistered kind answers no on any machine.
        /// </summary>
        [Fact]
        public void SupportedBackends_OffersTheNativeKind_ToAPlayer()
        {
            using (Registered(GpuBackendKind.MetalNative,
                new FakeBackendProvider(GpuBackendKind.MetalNative) { Supported = true }))
            {
                Assert.Contains(GpuBackendKind.MetalNative, GpuBackendSelector.SupportedBackends());
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
            var device = new FakeGpuDevice(GpuBackendKind.MetalNative);
            var provider = new FakeBackendProvider(GpuBackendKind.MetalNative) { Device = device };

            using (new EnvScope(GpuBackendSelector.EnvVarName, "metal-native"))
            using (new BackendProviderScope(GpuBackendKind.MetalNative, provider))
            {
                using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();

                Assert.Same(device, ctx.GpuDevice);
                Assert.Equal(GpuBackendKind.MetalNative, ctx.Backend);
                Assert.Equal(GpuBackendSource.EnvironmentOverride, ctx.Selection.Source);
                Assert.Equal(1, provider.HeadlessCreations);
                Assert.Equal(0, provider.WindowedCreations);
                // Headless never probes and never falls back, on any backend: it propagates its failure, so a
                // headless run cannot quietly change backend and file its golden images under one that never
                // rendered them. On THIS family that guarantee is worth the most, because the images in question
                // are the fleet's cross-backend reference.
                Assert.Equal(0, provider.SupportProbes);
            }
        }

        static BackendProviderScope Registered(GpuBackendKind backend, IGpuBackendProvider provider)
            => new(backend, provider);

        static BackendProviderScope Unregistered(GpuBackendKind backend) => new(backend, provider: null);
    }
}
