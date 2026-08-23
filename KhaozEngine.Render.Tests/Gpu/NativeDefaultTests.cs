using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The native default, as the three things a reader can check without a GPU: what the boot line SAYS when a
    /// native backend was defaulted to rather than named, what happens to a game whose process has no provider
    /// registered, and which backend a bare local GPU run ends up on.
    /// <para>
    /// The per-OS mapping itself is pinned in <c>GpuBackendSelectorTests</c> and in the three append audits, and
    /// is deliberately not repeated here. What is here is the consequences of that mapping, which is where a
    /// default goes wrong quietly rather than loudly.
    /// </para>
    /// <para>
    /// THE 17.40.0 FALLBACK IS GONE FROM THIS FILE, and its absence is the 18.0.0 change. A defaulted backend
    /// with no registered provider used to fall back to the platform's Veldrid incumbent and report
    /// <see cref="GpuBackendSource.DefaultProviderMissing"/>. There is no incumbent to fall back to, so the same
    /// case is a hard <see cref="GpuBackendProviderMissingException"/> naming the package and the call. In
    /// practice a game never reaches it: the three native packages ship in the <c>KhaozEngine.Game2D</c> and
    /// <c>KhaozEngine.Game3D</c> umbrellas, and <c>AppWindow</c> registers the platform's own at boot.
    /// </para>
    /// <para>
    /// A DEFAULT IS NOT A STORED PREFERENCE, and that distinction is the fix on top of the 18.0.0 change. A
    /// settings file naming a provider-less kind falls back and reports
    /// <see cref="GpuBackendSource.FallbackAfterFailure"/>, because a player cannot reach the setting that caused
    /// it. <c>StoredPreferenceSelfHealTests</c>, at the foot of this file, is that half.
    /// </para>
    /// </summary>
    public sealed class NativeDefaultTests
    {
        // A value no GpuBackendKind member will plausibly take, distinct from the 9001 the registry tests use so
        // the two files cannot collide over a registry entry. It behaves exactly like an appended provider-backed
        // kind, because the registry is keyed by value and knows nothing else about it.
        const GpuBackendKind SentinelKind = (GpuBackendKind)9002;

        // --- the boot header, which is what a tester and an F1 overlay both read ---

        /// <summary>
        /// A native backend nobody asked for prints as the DEFAULT. Before 17.40.0 a native backend could only be
        /// reached by naming it, so every native session in every log said <c>(KE_GRAPHICS_BACKEND override)</c>,
        /// and a reader could take "native" and "somebody chose it" as the same fact. They are different facts and
        /// the line has to separate them.
        /// </summary>
        [Theory]
        [InlineData(GpuBackendKind.MetalNative)]
        [InlineData(GpuBackendKind.Direct3D11Native)]
        [InlineData(GpuBackendKind.VulkanNative)]
        public void TheBootHeader_NamesADefaultedNativeBackend_AsDefault(GpuBackendKind backend)
        {
            string line = GpuDeviceContext.SelectionLine(
                new GpuBackendSelection(backend, GpuBackendSource.OsProbe, null));

            Assert.Equal($"GPU backend: {backend} (default)", line);
            Assert.DoesNotContain("override", line, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// And the other half, which is what makes the first half worth anything: a backend that WAS named still
        /// says so, naming the variable. A line that called both cases "default" would lose the attribution the
        /// whole native-backend program is measured through.
        /// </summary>
        [Fact]
        public void TheBootHeader_StillNamesAnOverride_AsAnOverride()
        {
            string line = GpuDeviceContext.SelectionLine(new GpuBackendSelection(
                GpuBackendKind.MetalNative, GpuBackendSource.EnvironmentOverride, "metal-native"));

            Assert.Equal($"GPU backend: MetalNative ({GpuBackendSelector.EnvVarName} override)", line);
        }

        /// <summary>
        /// An override that did not parse decided nothing, so the backend came from the default and the line
        /// says both: the default chose, and what you typed was not recognized.
        /// </summary>
        [Fact]
        public void TheBootHeader_NamesAnUnparseableOverride_AgainstTheDefault()
        {
            string line = GpuDeviceContext.SelectionLine(new GpuBackendSelection(
                GpuBackendKind.MetalNative, GpuBackendSource.UnrecognizedOverride, "metel"));

            Assert.Equal("GPU backend: MetalNative (default, override not recognized)", line);
        }

        /// <summary>
        /// The end-to-end shape on every OS: resolve with no override, and the line names that platform's native
        /// backend as the default. This is the row that goes red if an arm of the probe is ever put back.
        /// </summary>
        [Theory]
        [InlineData(OSPlatformKind.MacOS)]
        [InlineData(OSPlatformKind.Windows)]
        [InlineData(OSPlatformKind.Linux)]
        [InlineData(OSPlatformKind.Unknown)]
        public void TheBootHeader_NamesEveryPlatformDefault_AsANativeBackend(OSPlatformKind os)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(null, os);

            Assert.True(GpuBackendProviders.RequiresProvider(selection.Backend));
            Assert.False(GpuBackendSelector.IsRetired(selection.Backend));
            Assert.Equal($"GPU backend: {selection.Backend} (default)",
                GpuDeviceContext.SelectionLine(selection));
        }

        // --- a process with no provider registered for the backend it resolved to ---

        /// <summary>
        /// THE 18.0.0 CHANGE, at the one place that decides it. A DEFAULTED backend with no registered provider
        /// throws, exactly as a named one does, because there is no second implementation left to route onto.
        /// 17.40.0 fell back here and reported <see cref="GpuBackendSource.DefaultProviderMissing"/>, on the
        /// reasoning that a game which never referenced a native package had made no wiring mistake. That
        /// reasoning depended entirely on the Veldrid incumbent being there to run instead.
        /// <para>
        /// A DEFAULT is what this row is about, and a stored preference is the case it is NOT about. The
        /// provenance carried on the selection is what tells the two apart, so the row states the default's own
        /// (<see cref="GpuBackendSource.OsProbe"/>) rather than leaving it to whatever a bare kind implied.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Preflight_ThrowsForAnUnregisteredProvider_WhateverTheFallbackAllowance(bool allowFallback)
        {
            var ex = Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(SentinelKind, GpuBackendSource.OsProbe, null), allowFallback, out _));

            // The message has to say whose line is missing, because the fix is in the consuming app and not in
            // the engine. It states the naming CONVENTION plus two worked examples rather than switching on the
            // kind, which is what keeps it correct for a backend appended later.
            Assert.Contains("No graphics backend provider is registered", ex.Message, StringComparison.Ordinal);
            Assert.Contains("KhaozEngineD3D11.Register()", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The half that has to stay true for a PLAYER: what a bare DEFAULT resolves to is always one of the three
        /// kinds the engine ships a package for, and <c>AppWindow</c> registers that kind at boot, so the throw
        /// above is not something an ordinary windowed game can reach by doing nothing.
        /// <para>
        /// WHAT IT DOES NOT SAY, and used to. This doc claimed nothing a player can store reaches the throw at
        /// all, on the reasoning that <see cref="GpuBackendSelector.SupportedBackends"/> only ever offered
        /// registered kinds. That is a claim about ONE process. A settings file outlives the build that wrote it:
        /// a Windows player who picked <see cref="GpuBackendKind.VulkanNative"/> while the game registered all
        /// three, and a profile synced off another machine, both hand this build a kind it has no provider for.
        /// The preflight now reports that as a fallback for a stored preference rather than throwing, and
        /// <c>StoredPreferenceSelfHealTests</c> is where that path is pinned.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(OSPlatformKind.MacOS)]
        [InlineData(OSPlatformKind.Windows)]
        [InlineData(OSPlatformKind.Linux)]
        [InlineData(OSPlatformKind.Unknown)]
        public void TheResolvedDefault_IsAlwaysAKindTheEngineShipsAProviderFor(OSPlatformKind os)
        {
            GpuBackendKind resolved = GpuBackendSelector.Resolve(null, os).Backend;

            Assert.Contains(resolved, new[]
            {
                GpuBackendKind.MetalNative, GpuBackendKind.Direct3D11Native, GpuBackendKind.VulkanNative,
            });
        }

        /// <summary>
        /// Which selections are PINNED, stated once, because it is the input to how a failure is REPORTED and
        /// getting it wrong in either direction is silent: too broad and a repinned game stops booting, too
        /// narrow and a deliberate A/B answers with the other implementation.
        /// <para>
        /// <see cref="GpuBackendSource.UserPreference"/> is the row that moved in 17.40.0, and it is the whole
        /// reason this property is named for the ENVIRONMENT rather than for anyone who asked. It read true
        /// until then, on the reasoning that a stored preference is a request like an override is. A hard
        /// refusal is right for the soak session that pins a variable and would otherwise measure the wrong
        /// implementation. It is never right for a player.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(GpuBackendSource.EnvironmentOverride, true)]
        [InlineData(GpuBackendSource.UserPreference, false)]
        [InlineData(GpuBackendSource.OsProbe, false)]
        [InlineData(GpuBackendSource.UnrecognizedOverride, false)]
        [InlineData(GpuBackendSource.FallbackAfterFailure, false)]
        [InlineData(GpuBackendSource.DefaultProviderMissing, false)]
        public void WasPinnedByEnvironment_IsTrueOnlyForTheEnvironmentOverride(
            GpuBackendSource source, bool expected)
            => Assert.Equal(expected,
                new GpuBackendSelection(GpuBackendKind.MetalNative, source, null).WasPinnedByEnvironment);

        /// <summary>
        /// <see cref="GpuBackendSource.DefaultProviderMissing"/> keeps its NUMBER even though the engine never
        /// produces it any more. The enum is append-only because captured traces are read back against these
        /// values, so a 17.40.0 capture that recorded a 5 has to keep reading as what it meant.
        /// </summary>
        [Fact]
        public void TheRetiredSource_KeepsItsPublishedNumber()
            => Assert.Equal(5, (int)GpuBackendSource.DefaultProviderMissing);
    }

    /// <summary>
    /// What a bare local GPU run resolves to, and what that does to the goldens. It reads and clears
    /// <c>KE_GRAPHICS_BACKEND</c>, which <c>GoldenCompare</c> also reads to pick a golden family, so it belongs
    /// off the parallel pool with the rest of that state.
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class BareLocalGpuRunTests
    {
        /// <summary>
        /// THE DECISION, PINNED: a local <c>KE_GPU_TESTS=1 dotnet test</c> with no backend variable runs on the
        /// platform's NATIVE backend, because the harness resolves through the same
        /// <c>GpuBackendSelector.Select()</c> every consumer does. That is deliberate rather than incidental: the
        /// engine's own suite should exercise what ships by default, and each cross-platform GPU leg is
        /// unaffected because it names its backend in <c>KE_GRAPHICS_BACKEND</c>.
        /// </summary>
        [Fact]
        public void WithNoBackendVariable_TheHarnessResolvesToThePlatformNativeBackend()
        {
            using (new EnvScope(GpuBackendSelector.EnvVarName, null))
            {
                GpuBackendKind resolved = GpuBackendSelector.Select();

                Assert.Equal(GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS()), resolved);
                Assert.True(GpuBackendProviders.RequiresProvider(resolved));
                Assert.False(GpuBackendSelector.IsRetired(resolved));
            }
        }

        /// <summary>
        /// And the golden FAMILY that goes with it: a bare local run reads the family named after the RESOLVED
        /// kind (<c>metal-native</c> on the fleet's development platform), and that family is one its own backend
        /// OWNS. Row 2 of the Veldrid removal gave the native kinds their own families as byte-identical copies
        /// of the incumbents', and row 4 deleted the incumbent families, so the ownership property is now the
        /// only thing standing between a local run and a family nobody has baked.
        /// </summary>
        [Fact]
        public void TheGoldenFamily_IsOneTheResolvedKindOwns()
        {
            using (new EnvScope(GpuBackendSelector.EnvVarName, null))
            {
                GpuBackendKind resolved = GpuBackendSelector.Select();
                string token = GoldenCompare.GoldenBackendToken(resolved);

                Assert.True(GoldenCompare.OwnsFamily(resolved, token));
                Assert.EndsWith("-native", token, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// A bare local <c>KE_UPDATE_GOLDENS=1</c> is allowed, because the default owns the family it would
        /// write. 17.40.0 refused it for one release: the run was on a guest of the family it would have
        /// overwritten, so baking on a Mac meant naming <c>KE_GRAPHICS_BACKEND=metal</c>. The row is kept rather
        /// than deleted because that cost was documented out loud and a reader who met it needs to find where it
        /// ended.
        /// </summary>
        [Fact]
        public void ABareLocalBake_IsAllowed_BecauseTheDefaultOwnsItsFamily()
        {
            using (new EnvScope(GpuBackendSelector.EnvVarName, null))
            {
                Assert.Null(GoldenCompare.BakeRefusal(GpuBackendSelector.Select(), familyOverride: false));
            }
        }
    }

    /// <summary>
    /// A STORED PREFERENCE this build has no provider for, driven through the public windowed entry a game calls,
    /// and still device-free: the platform's own kind is stood up with a fake provider for the duration, so the
    /// fallback lands somewhere real without a driver.
    /// <para>
    /// The case is not hypothetical, and it is what 18.0.0 broke and this restores. A Windows player picks
    /// <c>VulkanNative</c> from a settings screen while the game still registers all three natives, the game later
    /// drops its explicit registrations and takes only what <c>AppWindow</c> registers, and the saved file now
    /// names a kind with no provider. A synced profile from another machine does the same thing on the first
    /// launch. Either way the player cannot reach the setting that caused it, so the engine has to self-heal and
    /// hand the game the one signal it already clears the preference on.
    /// </para>
    /// <para>
    /// It registers under a REAL kind, so it belongs in the same collection as the rest of the process-wide
    /// graphics state, and it puts back exactly what it found rather than unregistering: the assembly's own
    /// registration for the platform kind outlives this test and every <c>GpuFact</c> after it needs it.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class StoredPreferenceSelfHealTests
    {
        // A kind no member takes, standing in for a backend this build ships no package for. Distinct from the
        // 9001/9002 the other two files use so no registry entry can be shared across them.
        const GpuBackendKind StoredKind = (GpuBackendKind)9004;

        /// <summary>
        /// The whole self-heal in one call: the preference is asked for, has no provider, and the context that
        /// comes back runs the platform's native backend and reports
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/> with the stored kind preserved on
        /// <see cref="GpuBackendSelection.RequestedBackend"/>. That pair is what a game clears the setting on.
        /// </summary>
        [Fact]
        public void AStoredPreferenceWithNoProvider_SelfHealsToThePlatformNative()
        {
            GpuBackendKind platform = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());
            GpuBackendProviders.TryGet(platform, out IGpuBackendProvider? prior);
            GpuBackendProviders.Register(platform, new FakeBackendProvider(platform));

            try
            {
                using var env = new EnvScope(GpuBackendSelector.EnvVarName, null);
                using GpuDeviceContext ctx =
                    GpuDeviceContext.CreateForWindow(default, 8, 8, true, (GpuBackendKind?)StoredKind);

                Assert.Equal(platform, ctx.Backend);
                Assert.Equal(GpuBackendSource.FallbackAfterFailure, ctx.Selection.Source);
                Assert.Equal(StoredKind, ctx.Selection.RequestedBackend);
            }
            finally
            {
                if (prior is null) GpuBackendProviders.Unregister(platform);
                else GpuBackendProviders.Register(platform, prior);
            }
        }

        /// <summary>
        /// And the boundary, through the same public entry: the SAME provider-less kind NAMED outright still
        /// throws rather than self-healing. The named overload reports the same
        /// <see cref="GpuBackendSource.UserPreference"/> provenance, so what tells the two apart is the fallback
        /// allowance, and a caller that named one backend is not asking to be given another.
        /// </summary>
        [Fact]
        public void TheSameKindNamedOutright_StillThrows()
        {
            GpuBackendProviderMissingException ex = Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuDeviceContext.CreateForWindow(default, 8, 8, true, StoredKind));

            Assert.Equal(StoredKind, ex.Backend);
        }
    }
}
