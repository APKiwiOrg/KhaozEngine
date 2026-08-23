using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Headless tests for <see cref="GpuBackendSelector"/>'s pure logic: the env override parsing and the
    /// per-OS default probe, driven through the injectable <see cref="GpuBackendSelector.Select(string?, OSPlatformKind)"/>
    /// overload so no real environment or GPU is touched.
    /// </summary>
    public sealed class GpuBackendSelectorTests
    {
        // --- env override wins, case-insensitive. The three native tokens name a live backend; the four
        // retired tokens still name an API a tester means, so they run that API's native backend (18.0.0). ---

        [Theory]
        [InlineData("metal-native", GpuBackendKind.MetalNative)]
        [InlineData("vulkan-native", GpuBackendKind.VulkanNative)]
        [InlineData("d3d11-native", GpuBackendKind.Direct3D11Native)]
        public void Select_EnvOverride_Wins(string env, GpuBackendKind expected)
        {
            // OS would otherwise pick Linux->VulkanNative; the override must beat it (except where they
            // coincide).
            Assert.Equal(expected, GpuBackendSelector.Select(env, OSPlatformKind.Linux));
        }

        [Theory]
        [InlineData("METAL-NATIVE", GpuBackendKind.MetalNative)]
        [InlineData("  Vulkan-Native  ", GpuBackendKind.VulkanNative)]
        [InlineData("D3D11-Native", GpuBackendKind.Direct3D11Native)]
        public void Select_EnvOverride_IsCaseInsensitiveAndTrimmed(string env, GpuBackendKind expected)
        {
            // Windows would otherwise pick Direct3D11Native; override must beat it.
            Assert.Equal(expected, GpuBackendSelector.Select(env, OSPlatformKind.Windows));
        }

        /// <summary>
        /// A RETIRED token runs that API's native backend instead of refusing the boot. Refusing would turn every
        /// soak script, CI leg and shell alias in the fleet that still says <c>KE_GRAPHICS_BACKEND=metal</c> into
        /// a crash, for a variable whose whole purpose is to get a run going. <c>gl</c> has no native successor
        /// (the engine never had an OpenGL backend), so it lands on the platform default.
        /// </summary>
        [Theory]
        [InlineData("metal", OSPlatformKind.Linux, GpuBackendKind.MetalNative)]
        [InlineData("METAL", OSPlatformKind.Windows, GpuBackendKind.MetalNative)]
        [InlineData("vulkan", OSPlatformKind.MacOS, GpuBackendKind.VulkanNative)]
        [InlineData("  Vulkan  ", OSPlatformKind.Windows, GpuBackendKind.VulkanNative)]
        [InlineData("d3d11", OSPlatformKind.Linux, GpuBackendKind.Direct3D11Native)]
        [InlineData("direct3d11", OSPlatformKind.MacOS, GpuBackendKind.Direct3D11Native)]
        [InlineData("gl", OSPlatformKind.Linux, GpuBackendKind.VulkanNative)]
        [InlineData("opengl", OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        [InlineData("Gl", OSPlatformKind.Windows, GpuBackendKind.Direct3D11Native)]
        public void Select_RetiredEnvOverride_RunsTheNativeSuccessor(
            string env, OSPlatformKind os, GpuBackendKind expected)
        {
            Assert.Equal(expected, GpuBackendSelector.Select(env, os));
        }

        /// <summary>
        /// And it does NOT do it silently: the retired member is carried on <c>RequestedBackend</c> so the boot
        /// line can name what was asked for and what ran instead.
        /// </summary>
        [Theory]
        [InlineData("metal", GpuBackendKind.Metal, GpuBackendKind.MetalNative)]
        [InlineData("vulkan", GpuBackendKind.Vulkan, GpuBackendKind.VulkanNative)]
        [InlineData("direct3d11", GpuBackendKind.Direct3D11, GpuBackendKind.Direct3D11Native)]
        [InlineData("gl", GpuBackendKind.OpenGL, GpuBackendKind.VulkanNative)]
        public void Resolve_RetiredEnvOverride_RecordsTheRedirect(
            string env, GpuBackendKind retired, GpuBackendKind ran)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(env, OSPlatformKind.Linux);

            Assert.Equal(ran, selection.Backend);
            Assert.Equal(retired, selection.RequestedBackend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.Equal(env, selection.RequestedOverride);
            Assert.True(GpuBackendSelector.IsRetired(retired));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("nonsense")]
        [InlineData("directx")]
        public void Select_BadOrMissingEnv_FallsThroughToProbe(string? env)
        {
            // Bad/empty/null override is ignored; the OS probe decides (Windows -> Direct3D11Native since
            // 17.40.0).
            Assert.Equal(GpuBackendKind.Direct3D11Native, GpuBackendSelector.Select(env, OSPlatformKind.Windows));
        }

        // --- OS probe mapping. Every arm answers the ENGINE'S OWN backend since 17.40.0; each used to answer
        // that API's Veldrid incumbent, which 18.0.0 deleted. ---

        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11Native)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.VulkanNative)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.VulkanNative)]
        public void Probe_MapsOsToDefaultBackend(OSPlatformKind os, GpuBackendKind expected)
        {
            Assert.Equal(expected, GpuBackendSelector.ProbeOS(os));
            // Same result via Select with no override.
            Assert.Equal(expected, GpuBackendSelector.Select(null, os));
        }

        /// <summary>
        /// ONE DEFAULT PER PLATFORM SINCE 18.0.0, which is what makes the fallback guard in
        /// <c>GpuDeviceContext</c> a complete statement. There used to be a second map beside the probe,
        /// <c>IncumbentFor</c>, holding what the probe answered before 17.40.0 and what a failed device creation
        /// fell back TO. It was deleted with the backend it named, so a fallback lands on the probe's own answer
        /// and the "nothing to fall back TO when the request already IS the default" arm is the only case left.
        /// </summary>
        [Theory]
        [InlineData(OSPlatformKind.MacOS)]
        [InlineData(OSPlatformKind.Windows)]
        [InlineData(OSPlatformKind.Linux)]
        [InlineData(OSPlatformKind.Unknown)]
        public void EveryPlatformDefault_IsALiveBackend(OSPlatformKind os)
            => Assert.False(GpuBackendSelector.IsRetired(GpuBackendSelector.ProbeOS(os)));

        [Theory]
        [InlineData("metal", true, GpuBackendKind.Metal)]
        [InlineData("vulkan", true, GpuBackendKind.Vulkan)]
        [InlineData("d3d11", true, GpuBackendKind.Direct3D11)]
        [InlineData("direct3d11", true, GpuBackendKind.Direct3D11)]   // alias matching GpuBackendKind.ToString()
        [InlineData("d3d11-native", true, GpuBackendKind.Direct3D11Native)]
        [InlineData("direct3d11-native", true, GpuBackendKind.Direct3D11Native)]
        [InlineData("vulkan-native", true, GpuBackendKind.VulkanNative)]
        [InlineData("vk-native", true, GpuBackendKind.VulkanNative)]   // the short form, no unsuffixed twin
        [InlineData("gl", true, GpuBackendKind.OpenGL)]
        [InlineData("opengl", true, GpuBackendKind.OpenGL)]           // alias matching GpuBackendKind.ToString()
        [InlineData("nonsense", false, default(GpuBackendKind))]
        [InlineData(null, false, default(GpuBackendKind))]
        public void TryParseBackend_RecognizesKnownValues(string? value, bool ok, GpuBackendKind expected)
        {
            bool parsed = GpuBackendSelector.TryParseBackend(value, out GpuBackendKind backend);
            Assert.Equal(ok, parsed);
            if (ok) Assert.Equal(expected, backend);
        }

        // --- the exact KE_GRAPHICS_BACKEND values the cross-platform-gpu CI matrix sets per leg, asserted
        // regardless of the host OS so the override drives the backend (and thus the per-backend golden path).
        // Each string below is the matrix's `backend` key verbatim. The three incumbent legs went with the
        // Veldrid backend in 18.0.0, so the matrix is three native legs and every key here ends in -native. ---

        [Theory]
        [InlineData("metal-native", GpuBackendKind.MetalNative)]           // macos-26 leg
        [InlineData("direct3d11-native", GpuBackendKind.Direct3D11Native)] // windows-latest leg
        [InlineData("vulkan-native", GpuBackendKind.VulkanNative)]         // ubuntu-latest leg
        public void Select_CiMatrixBackendOverride_HonoredOnEveryOs(string env, GpuBackendKind expected)
        {
            foreach (OSPlatformKind os in new[]
                { OSPlatformKind.MacOS, OSPlatformKind.Windows, OSPlatformKind.Linux, OSPlatformKind.Unknown })
            {
                Assert.Equal(expected, GpuBackendSelector.Select(env, os));
            }
        }

        // --- Resolve: the same decision, reported WITH its provenance. This is what makes a misconfigured
        // KE_GRAPHICS_BACKEND visible: without it, a typo'd override and the OS default are indistinguishable,
        // so a backend A/B looks like "the requested backend did not help" when it never ran. ---

        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11Native)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.VulkanNative)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.VulkanNative)]
        public void Resolve_NoOverride_IsOsProbeWithNoRawValue(OSPlatformKind os, GpuBackendKind expected)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(null, os);

            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.OsProbe, selection.Source);
            Assert.Null(selection.RequestedOverride);
            // The environment pinned nothing, which is what decides whether a missing native provider throws
            // or falls back.
            Assert.False(selection.WasPinnedByEnvironment);
        }

        [Theory]
        [InlineData("vulkan-native", GpuBackendKind.VulkanNative)]
        [InlineData("vk-native", GpuBackendKind.VulkanNative)]              // short form
        [InlineData("direct3d11-native", GpuBackendKind.Direct3D11Native)]  // alias
        [InlineData("metal-native", GpuBackendKind.MetalNative)]
        public void Resolve_ValidOverride_IsHonoredAndKeepsTheRawValue(string env, GpuBackendKind expected)
        {
            // Windows would otherwise probe to Direct3D11Native, so the override is doing the work here.
            GpuBackendSelection selection = GpuBackendSelector.Resolve(env, OSPlatformKind.Windows);

            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.Equal(env, selection.RequestedOverride);
        }

        [Theory]
        [InlineData(" Vulkan-Native ", GpuBackendKind.VulkanNative)]
        [InlineData("METAL-NATIVE", GpuBackendKind.MetalNative)]
        [InlineData("\tD3D11-Native\n", GpuBackendKind.Direct3D11Native)]
        public void Resolve_ValidOverride_PreservesOriginalCaseAndWhitespace(string env, GpuBackendKind expected)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(env, OSPlatformKind.Linux);

            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            // The RAW value survives untrimmed and un-lowercased: normalizing it away would hide exactly the
            // stray quoting / stray whitespace a reader needs to see in the log.
            Assert.Equal(env, selection.RequestedOverride);
        }

        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11Native)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.VulkanNative)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.VulkanNative)]
        public void Resolve_UnparseableOverride_FallsBackToProbeButKeepsWhatWasAsked(OSPlatformKind os, GpuBackendKind expected)
        {
            // "vulcan" is the realistic typo: close enough to look right in a launcher, silently not a backend.
            GpuBackendSelection selection = GpuBackendSelector.Resolve("vulcan", os);

            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.UnrecognizedOverride, selection.Source);
            Assert.Equal("vulcan", selection.RequestedOverride);
            // A value that decided nothing pinned nothing: the probe picked, so this is a default.
            Assert.False(selection.WasPinnedByEnvironment);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void Resolve_BlankOverride_CountsAsNoOverrideAtAll(string env)
        {
            // A launcher that exports the var empty has not asked for anything, so it is not a misconfiguration.
            GpuBackendSelection selection = GpuBackendSelector.Resolve(env, OSPlatformKind.Windows);

            Assert.Equal(GpuBackendKind.Direct3D11Native, selection.Backend);
            Assert.Equal(GpuBackendSource.OsProbe, selection.Source);
            Assert.Null(selection.RequestedOverride);
        }

        // Anti-drift guard for keeping both APIs: Select is implemented on top of Resolve, and this fails if a
        // future edit ever gives them two decision paths that can disagree.
        [Fact]
        public void Select_AndResolve_AgreeOnEveryInput()
        {
            string?[] overrides =
            {
                null, "", "   ", "\t", "vulcan", "directx", "nonsense", "d3d11native",
                "metal", "vulkan", "d3d11", "direct3d11", "d3d11-native", "direct3d11-native", "gl", "opengl",
                " Vulkan ", "METAL", "\tD3D11\n", " D3D11-Native ",
            };

            foreach (OSPlatformKind os in new[]
                { OSPlatformKind.MacOS, OSPlatformKind.Windows, OSPlatformKind.Linux, OSPlatformKind.Unknown })
            {
                foreach (string? env in overrides)
                {
                    Assert.Equal(GpuBackendSelector.Select(env, os), GpuBackendSelector.Resolve(env, os).Backend);
                }
            }
        }

        // --- The stored user preference (17.23.0): env override > preference > OS probe. The preference is the
        // player's in-game graphics setting, handed in as data so KhaozEngine.Gpu does no file IO. ---

        [Theory]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.VulkanNative)]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.VulkanNative)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Direct3D11Native)]
        public void Resolve_PreferenceWithNoOverride_BeatsTheOsProbe(OSPlatformKind os, GpuBackendKind preference)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(null, os, preference);

            Assert.Equal(preference, selection.Backend);
            Assert.Equal(GpuBackendSource.UserPreference, selection.Source);
            Assert.Null(selection.RequestedOverride);
            Assert.Null(selection.RequestedBackend);
        }

        /// <summary>
        /// A STORED preference for a RETIRED member (18.0.0). It reports <c>FallbackAfterFailure</c> with the
        /// retired member on <c>RequestedBackend</c>, which is the signal a consuming game already acts on, and
        /// acting on it CLEARS the setting. That is the only thing that gets the player off a dead choice
        /// permanently. It is rejected here, ahead of <c>GpuBackendProviders.Require</c>, because Require throws
        /// by contract and a saved settings file must never be able to make the engine throw at boot.
        /// </summary>
        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.Metal, GpuBackendKind.MetalNative)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11, GpuBackendKind.Direct3D11Native)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Vulkan, GpuBackendKind.VulkanNative)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Metal, GpuBackendKind.Direct3D11Native)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.OpenGL, GpuBackendKind.VulkanNative)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.Vulkan, GpuBackendKind.VulkanNative)]
        public void Resolve_RetiredPreference_SelfHealsToThePlatformNativeAndReportsFallback(
            OSPlatformKind os, GpuBackendKind preference, GpuBackendKind expected)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(null, os, preference);

            // The platform's OWN default, not the retired member's API: a stored Metal on Windows must not send
            // the player to a Metal device that cannot exist there.
            Assert.Equal(GpuBackendSelector.ProbeOS(os), selection.Backend);
            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.FallbackAfterFailure, selection.Source);
            Assert.Equal(preference, selection.RequestedBackend);
            Assert.Null(selection.RequestedOverride);
        }

        [Fact]
        public void Resolve_EnvironmentOverride_OutranksThePreference()
        {
            // The whole point of keeping the env var on top: a developer must be able to force a backend for a
            // repro no matter what the player picked in the settings screen.
            GpuBackendSelection selection =
                GpuBackendSelector.Resolve("metal-native", OSPlatformKind.Windows, GpuBackendKind.VulkanNative);

            Assert.Equal(GpuBackendKind.MetalNative, selection.Backend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.Equal("metal-native", selection.RequestedOverride);
        }

        [Fact]
        public void Resolve_UnparseableOverride_FallsThroughToThePreferenceAndStillReportsTheBadValue()
        {
            // An override that does not parse is not an override, so it falls to the next rung rather than
            // skipping the player's choice and landing on the OS probe. The raw text is still carried so the
            // "your env var did nothing" warning survives.
            GpuBackendSelection selection =
                GpuBackendSelector.Resolve("vulcan", OSPlatformKind.Windows, GpuBackendKind.VulkanNative);

            Assert.Equal(GpuBackendKind.VulkanNative, selection.Backend);
            Assert.Equal(GpuBackendSource.UserPreference, selection.Source);
            Assert.Equal("vulcan", selection.RequestedOverride);
        }

        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11Native)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.VulkanNative)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.VulkanNative)]
        public void Resolve_NullPreference_IsIdenticalToTheTwoArgumentOverload(OSPlatformKind os, GpuBackendKind expected)
        {
            // The compatibility guarantee that keeps every pre-17.23.0 call site behaving exactly as it did.
            foreach (string? env in new string?[] { null, "", "vulcan", "metal" })
            {
                Assert.Equal(GpuBackendSelector.Resolve(env, os), GpuBackendSelector.Resolve(env, os, null));
            }
            Assert.Equal(expected, GpuBackendSelector.Resolve(null, os, null).Backend);
        }

        [Fact]
        public void Select_AndResolve_AgreeOnEveryInput_WithAPreferenceToo()
        {
            GpuBackendKind?[] preferences = { null, GpuBackendKind.Vulkan, GpuBackendKind.Metal, GpuBackendKind.Direct3D11 };
            string?[] overrides = { null, "", "vulcan", "metal", "d3d11" };

            foreach (OSPlatformKind os in new[]
                { OSPlatformKind.MacOS, OSPlatformKind.Windows, OSPlatformKind.Linux, OSPlatformKind.Unknown })
            {
                foreach (string? env in overrides)
                {
                    foreach (GpuBackendKind? pref in preferences)
                    {
                        Assert.Equal(
                            GpuBackendSelector.Select(env, os, pref),
                            GpuBackendSelector.Resolve(env, os, pref).Backend);
                    }
                }
            }
        }

        // --- Support probe + fallback reporting (17.23.0). The probe decides what a settings UI may OFFER; the
        // fallback decides what happens when a driver lies. Both are needed: a partial ICD can pass the probe and
        // still fail at device creation. ---

        [Theory]
        [InlineData(GpuBackendKind.OpenGL)]
        [InlineData(GpuBackendKind.Metal)]
        [InlineData(GpuBackendKind.Vulkan)]
        [InlineData(GpuBackendKind.Direct3D11)]
        public void IsBackendSupported_IsAlwaysFalse_ForARetiredMember(GpuBackendKind retired)
        {
            // The engine never had an OpenGL implementation, and the other three lost theirs with the Veldrid
            // incumbent in 18.0.0. Offering any of them to a player would be offering a choice that cannot boot.
            Assert.True(GpuBackendSelector.IsRetired(retired));
            Assert.False(GpuBackendSelector.IsBackendSupported(retired));
        }

        [Fact]
        public void IsBackendSupported_NeverThrows_ForAnyBackend()
        {
            // The probe loads native libraries. On a machine without them it must answer "no", not blow up the
            // settings screen that asked.
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>())
            {
                GpuBackendSelector.IsBackendSupported(kind);
            }
        }

        [Fact]
        public void SupportedBackends_OffersOnlyRealWindowedBackends_InAStableOrder()
        {
            IReadOnlyList<GpuBackendKind> supported = GpuBackendSelector.SupportedBackends();

            Assert.DoesNotContain(GpuBackendKind.OpenGL, supported);
            Assert.All(supported, k => Assert.True(GpuBackendSelector.IsBackendSupported(k)));
            Assert.Equal(supported.Distinct().Count(), supported.Count);

            // NO RETIRED MEMBER IS EVER OFFERED (18.0.0). A settings dropdown built from this list is the one
            // place a player can newly ACQUIRE a stored preference, so a retired member leaking in here would
            // recreate the dead saved choice the retirement exists to drain.
            Assert.All(supported, k => Assert.False(GpuBackendSelector.IsRetired(k)));

            // Stable presentation order: a settings dropdown must not reshuffle itself between openings.
            var order = new[]
            {
                GpuBackendKind.MetalNative,
                GpuBackendKind.VulkanNative,
                GpuBackendKind.Direct3D11Native,
            };
            Assert.Equal(supported.OrderBy(k => Array.IndexOf(order, k)).ToArray(), supported.ToArray());
            Assert.Equal(supported, GpuBackendSelector.SupportedBackends());
        }

        [Fact]
        public void AfterFallback_ReportsWhatRanAndWhatWasAskedFor()
        {
            // The exact contract a consuming game reads to decide "clear the stored preference".
            var requested = new GpuBackendSelection(
                GpuBackendKind.VulkanNative, GpuBackendSource.UserPreference, null);

            GpuBackendSelection fell = GpuBackendSelector.AfterFallback(requested, GpuBackendKind.Direct3D11Native);

            Assert.Equal(GpuBackendKind.Direct3D11Native, fell.Backend);      // what actually runs
            Assert.Equal(GpuBackendKind.VulkanNative, fell.RequestedBackend); // what the player picked and lost
            Assert.Equal(GpuBackendSource.FallbackAfterFailure, fell.Source);
        }

        [Fact]
        public void AfterFallback_KeepsTheRawOverrideTextForTheDiagnostic()
        {
            var requested = new GpuBackendSelection(
                GpuBackendKind.VulkanNative, GpuBackendSource.EnvironmentOverride, "vulkan-native");

            GpuBackendSelection fell = GpuBackendSelector.AfterFallback(requested, GpuBackendKind.MetalNative);

            Assert.Equal("vulkan-native", fell.RequestedOverride);
            Assert.Equal(GpuBackendKind.VulkanNative, fell.RequestedBackend);
        }

        // The numeric values are a published telemetry contract (consumers persist (int)GpuBackendSource and read
        // captured traces back against these numbers). This test is the thing that fails if anyone reorders them.
        [Fact]
        public void GpuBackendSource_NumericValues_ArePinned()
        {
            Assert.Equal(0, (int)GpuBackendSource.OsProbe);
            Assert.Equal(1, (int)GpuBackendSource.EnvironmentOverride);
            Assert.Equal(2, (int)GpuBackendSource.UnrecognizedOverride);
            Assert.Equal(3, (int)GpuBackendSource.UserPreference);
            Assert.Equal(4, (int)GpuBackendSource.FallbackAfterFailure);
            Assert.Equal(5, (int)GpuBackendSource.DefaultProviderMissing);
        }

    }
}
