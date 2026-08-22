using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The 17.40.0 default flip, as the three things a reader can check without a GPU: what the boot line SAYS
    /// when a native backend was defaulted to rather than named, what happens to a game that repins the engine
    /// without taking a native backend package, and which backend a bare local GPU run ends up on.
    /// <para>
    /// The per-OS mapping itself is pinned in <c>GpuBackendSelectorTests</c> and in the three append audits, and
    /// is deliberately not repeated here. What is here is the consequences of that mapping, which is where a
    /// flip goes wrong quietly rather than loudly.
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
        /// A native backend nobody asked for prints as the DEFAULT. Before the flip a native backend could only
        /// be reached by naming it, so every native session in every log said
        /// <c>(KE_GRAPHICS_BACKEND override)</c>, and a reader could take "native" and "somebody chose it" as
        /// the same fact. After the flip they are different facts and the line has to separate them.
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
                GpuBackendKind.Metal, GpuBackendSource.EnvironmentOverride, "metal"));

            Assert.Equal($"GPU backend: Metal ({GpuBackendSelector.EnvVarName} override)", line);
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
            Assert.Equal($"GPU backend: {selection.Backend} (default)",
                GpuDeviceContext.SelectionLine(selection));
        }

        // --- a game that repins without taking a native backend package ---

        /// <summary>
        /// The case the flip creates and the one that decides whether repinning the engine is safe. The native
        /// packages are in no umbrella and the engine cannot reference them, so a game gets one by adding a
        /// package reference and a <c>Register()</c> call. Now that the OS probe answers a provider-backed kind
        /// everywhere, a game that did neither has a default its own process cannot build, and the preflight
        /// falls back with a reason instead of throwing at a request nobody made.
        /// </summary>
        [Fact]
        public void Preflight_FallsBackForADefaultedBackend_WhenNoProviderIsRegistered()
        {
            string? reason = GpuDeviceContext.PreflightProvider(
                SentinelKind, allowFallback: true, pinnedByEnvironment: false, out IGpuBackendProvider? provider, out _);

            Assert.Null(provider);
            Assert.NotNull(reason);
            // The message has to say whose line is missing, because the fix is in the game and not the engine.
            Assert.Contains("no provider is registered", reason);
            Assert.Contains("KhaozEngine.Gpu.Metal", reason);
        }

        /// <summary>
        /// Decision I2, narrowed rather than dropped. A backend the caller NAMED still throws for a missing
        /// provider, which is what stops a soak session that asked for the native backend from quietly measuring
        /// the incumbent and filing the number under the native name.
        /// </summary>
        [Fact]
        public void Preflight_StillThrowsForANamedBackend_WhenNoProviderIsRegistered()
            => Assert.Throws<GpuBackendProviderMissingException>(
                () => GpuDeviceContext.PreflightProvider(
                    SentinelKind, allowFallback: true, pinnedByEnvironment: true, out _, out _));

        /// <summary>
        /// Which selections are PINNED, stated once, because it is the input to the rule above and getting it
        /// wrong in either direction is silent: too broad and a repinned game stops booting, too narrow and a
        /// deliberate A/B answers with the other implementation.
        /// <para>
        /// <see cref="GpuBackendSource.UserPreference"/> is the row that moved, and it is the whole reason this
        /// property is named for the ENVIRONMENT rather than for anyone who asked. It read true until 17.40.0,
        /// on the reasoning that a stored preference is a request like an override is. But
        /// <see cref="GpuBackendSelector.SupportedBackends"/> offers native rows now, so a player can store
        /// <see cref="GpuBackendKind.MetalNative"/>, and a later build that dropped the package or the
        /// <c>Register()</c> line would throw at boot with the setting that caused it reachable only by editing
        /// a file. A hard throw is right for the soak session that pins a variable and would otherwise measure
        /// the wrong implementation. It is never right for a player.
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

        // --- how that fallback is REPORTED, which is the half a consuming game acts on ---

        /// <summary>
        /// The report split, read at the one place it is decided. Every row is the same missing registration,
        /// and what changes is who asked for the backend. A stored preference is a choice the player made and
        /// can un-make, so it reports <see cref="GpuBackendSource.FallbackAfterFailure"/>, which is the signal a
        /// game clears a stored preference on. Everything else was a DEFAULT nobody chose, and reports
        /// <see cref="GpuBackendSource.DefaultProviderMissing"/>, which asks a game to clear nothing.
        /// <para>
        /// Reporting the defaulted case as <see cref="GpuBackendSource.FallbackAfterFailure"/> is what this row
        /// exists to stop, and it would be wrong in two directions at once: every repinned game that has not
        /// taken a native package would read as a device-creation failure in telemetry, and a player who chose
        /// nothing would be shown a notice saying their graphics choice failed.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(GpuBackendSource.OsProbe, GpuBackendSource.DefaultProviderMissing)]
        [InlineData(GpuBackendSource.UnrecognizedOverride, GpuBackendSource.DefaultProviderMissing)]
        [InlineData(GpuBackendSource.UserPreference, GpuBackendSource.FallbackAfterFailure)]
        public void Report_SplitsTheFallbackByWhoAskedForTheBackend(
            GpuBackendSource asked, GpuBackendSource expected)
        {
            var selection = new GpuBackendSelection(GpuBackendKind.MetalNative, asked, null);

            GpuBackendSelection fell = UnregisteredNativeDefault.Report(selection, GpuBackendKind.Metal);

            Assert.Equal(expected, fell.Source);
            Assert.Equal(GpuBackendKind.Metal, fell.Backend);
            // What could not be built is preserved on both arms, because a reader of either one has to know
            // which backend the process was supposed to be on.
            Assert.Equal(GpuBackendKind.MetalNative, fell.RequestedBackend);
        }

        /// <summary>
        /// The negative half stated on its own, because it is the assertion that trips if the two members are
        /// ever collapsed back into one. A DEFAULT whose provider was never registered must not read as a
        /// device-creation failure, on any of the provenances that count as a default.
        /// </summary>
        [Theory]
        [InlineData(GpuBackendSource.OsProbe)]
        [InlineData(GpuBackendSource.UnrecognizedOverride)]
        public void Report_DoesNotSayFallbackAfterFailure_ForADefaultNobodyChose(GpuBackendSource asked)
        {
            GpuBackendSelection fell = UnregisteredNativeDefault.Report(
                new GpuBackendSelection(GpuBackendKind.VulkanNative, asked, null), GpuBackendKind.Vulkan);

            Assert.NotEqual(GpuBackendSource.FallbackAfterFailure, fell.Source);
        }

        /// <summary>
        /// The boot line for the new source, which has to read nothing like the failure line beside it. A reader
        /// must come away with "this build has no provider for its default backend", not "a device failed to
        /// create", because only the first one is true and only the first one names a fix.
        /// </summary>
        [Fact]
        public void TheBootHeader_NamesAMissingDefaultProvider_AsAMissingRegistration()
        {
            string line = GpuDeviceContext.SelectionLine(GpuBackendSelector.AfterMissingDefaultProvider(
                new GpuBackendSelection(GpuBackendKind.VulkanNative, GpuBackendSource.OsProbe, null),
                GpuBackendKind.Vulkan));

            Assert.Equal("GPU backend: Vulkan (default, VulkanNative has no registered provider)", line);
            Assert.DoesNotContain("failed", line, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The WARN beside it, aimed at the DEVELOPER: it names the packages, the one call that fixes it, and
        /// says outright that there is nothing stored to clear. The player-facing fallback WARN says the
        /// opposite of that last clause, which is the whole reason the two are separate strings rather than one
        /// with a substitution in it.
        /// </summary>
        [Fact]
        public void TheWarning_ForAMissingDefaultProvider_NamesTheRegistrationRatherThanAStoredChoice()
        {
            string warning = UnregisteredNativeDefault.Warning(
                GpuBackendKind.MetalNative, GpuBackendKind.Metal);

            Assert.Contains("MetalNative", warning);
            Assert.Contains("KhaozEngine.Gpu.Metal", warning);
            Assert.Contains("KhaozEngineMetal.Register()", warning);
            Assert.Contains("nothing to clear", warning);
            Assert.DoesNotContain("should be cleared", warning);

            // And the contrast that makes it worth having: the player-facing line really does say the thing
            // this one must not.
            Assert.Contains("should be cleared", GpuDeviceContext.FallbackWarning(
                GpuBackendKind.MetalNative, "boom", GpuBackendKind.Metal));
        }

        /// <summary>
        /// The header a triage reader opens, which is where the split has to survive: the source reaches the
        /// capture by NAME, and the default that could not be built rides along on <c>requestedBackend</c>. A
        /// header that said <c>FallbackAfterFailure</c> here would misattribute every un-wired session in the
        /// fleet at once.
        /// </summary>
        [Fact]
        public void TheTelemetryHeader_CarriesTheMissingDefaultProvider_ByName()
        {
            GpuBackendSelection fell = GpuBackendSelector.AfterMissingDefaultProvider(
                new GpuBackendSelection(GpuBackendKind.Direct3D11Native, GpuBackendSource.OsProbe, null),
                GpuBackendKind.Direct3D11);

            var info = new TelemetrySessionInfo().WithGpu(fell, "Microsoft Basic Render Driver", null, null);

            Assert.Equal("Direct3D11", info.GpuBackend);
            Assert.Equal("DefaultProviderMissing", info.GpuBackendSource);
            Assert.Equal("Direct3D11Native", info.GpuRequestedBackend);
        }
    }

    /// <summary>
    /// What a bare local GPU run resolves to after the flip, and what that does to the goldens. It reads and
    /// clears <c>KE_GRAPHICS_BACKEND</c>, which <c>GoldenCompare</c> also reads to pick a golden family, so it
    /// belongs off the parallel pool with the rest of that state.
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class BareLocalGpuRunTests
    {
        /// <summary>
        /// THE DECISION, PINNED: a local <c>KE_GPU_TESTS=1 dotnet test</c> with no backend variable runs on the
        /// platform's NATIVE backend from 17.40.0, because the harness resolves through the same
        /// <c>GpuBackendSelector.Select()</c> every consumer does and the flip moved it. That is deliberate
        /// rather than incidental: the engine's own suite should exercise what ships by default, and the five CI
        /// legs are unaffected because each names its backend in <c>KE_GRAPHICS_BACKEND</c>. Naming the
        /// incumbent still pins it, which is how a local A/B is run.
        /// </summary>
        [Fact]
        public void WithNoBackendVariable_TheHarnessResolvesToThePlatformNativeBackend()
        {
            using (new EnvScope(GpuBackendSelector.EnvVarName, null))
            {
                GpuBackendKind resolved = GpuBackendSelector.Select();

                Assert.Equal(GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS()), resolved);
                Assert.True(GpuBackendProviders.RequiresProvider(resolved));
            }
        }

        /// <summary>
        /// And the property that makes the line above safe to take: the golden FAMILY does not move with it. A
        /// native kind is a guest in its incumbent's family, so a bare local run compares against exactly the
        /// same committed grids it compared against before the flip. If this ever stopped holding, every local
        /// golden run would silently be reading a family nobody has baked.
        /// </summary>
        [Fact]
        public void TheGoldenFamily_IsUnchangedByTheFlip()
        {
            using (new EnvScope(GpuBackendSelector.EnvVarName, null))
            {
                OSPlatformKind os = GpuBackendSelector.DetectOS();

                Assert.Equal(
                    GoldenCompare.GoldenBackendToken(GpuBackendSelector.IncumbentFor(os)),
                    GoldenCompare.GoldenBackendToken(GpuBackendSelector.Select()));
            }
        }

        /// <summary>
        /// The one ergonomic cost of the decision, pinned so it is a documented consequence rather than a
        /// surprise: a bare local <c>KE_UPDATE_GOLDENS=1</c> is now REFUSED, because the run is on a guest of
        /// the family it would overwrite. Baking locally means naming the incumbent, and the refusal says so.
        /// </summary>
        [Fact]
        public void ABareLocalBake_IsRefused_BecauseTheDefaultIsNowAGuestOfItsFamily()
        {
            using (new EnvScope(GpuBackendSelector.EnvVarName, null))
            {
                string? refusal = GoldenCompare.BakeRefusal(GpuBackendSelector.Select(), familyOverride: false);

                Assert.NotNull(refusal);
                Assert.Contains("Re-bake on the backend that owns", refusal);
            }
        }

        /// <summary>Naming the incumbent still pins it, which is both the documented opt-out and the way a local
        /// bake is taken.</summary>
        [Fact]
        public void NamingTheIncumbent_StillPinsItAndCanStillBake()
        {
            OSPlatformKind os = GpuBackendSelector.DetectOS();
            GpuBackendKind incumbent = GpuBackendSelector.IncumbentFor(os);

            using (new EnvScope(GpuBackendSelector.EnvVarName, incumbent.ToString()))
            {
                Assert.Equal(incumbent, GpuBackendSelector.Select());
                Assert.Null(GoldenCompare.BakeRefusal(GpuBackendSelector.Select(), familyOverride: false));
            }
        }
    }
}
