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
        // --- env override wins, case-insensitive, all four values ---

        [Theory]
        [InlineData("metal", GpuBackendKind.Metal)]
        [InlineData("vulkan", GpuBackendKind.Vulkan)]
        [InlineData("d3d11", GpuBackendKind.Direct3D11)]
        [InlineData("gl", GpuBackendKind.OpenGL)]
        public void Select_EnvOverride_Wins(string env, GpuBackendKind expected)
        {
            // OS would otherwise pick Linux->Vulkan; the override must beat it (except where they coincide).
            Assert.Equal(expected, GpuBackendSelector.Select(env, OSPlatformKind.Linux));
        }

        [Theory]
        [InlineData("METAL", GpuBackendKind.Metal)]
        [InlineData("  Vulkan  ", GpuBackendKind.Vulkan)]
        [InlineData("D3D11", GpuBackendKind.Direct3D11)]
        [InlineData("Gl", GpuBackendKind.OpenGL)]
        public void Select_EnvOverride_IsCaseInsensitiveAndTrimmed(string env, GpuBackendKind expected)
        {
            // macOS would otherwise pick Metal; override must beat it.
            Assert.Equal(expected, GpuBackendSelector.Select(env, OSPlatformKind.Windows));
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
        // that API's Veldrid incumbent, which is now what IncumbentFor answers. ---

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
        /// The frozen incumbent map beside it, which is what the probe answered before 17.40.0 and what a failed
        /// device creation falls back TO now. Pinned as a pair with the probe above, because the two arms have to
        /// stay DIFFERENT on every OS: a fallback that equalled the default would aim the retry at the backend
        /// that just refused.
        /// </summary>
        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.Metal)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Vulkan)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.Vulkan)]
        public void IncumbentFor_MapsOsToTheVeldridBackend(OSPlatformKind os, GpuBackendKind expected)
        {
            Assert.Equal(expected, GpuBackendSelector.IncumbentFor(os));
            Assert.NotEqual(GpuBackendSelector.ProbeOS(os), GpuBackendSelector.IncumbentFor(os));
        }

        /// <summary>
        /// The opt-out, stated as the thing a game is told to set. Each incumbent token still pins its Veldrid
        /// backend on the OS whose default is now that API's native implementation, which is the whole content
        /// of "the incumbents remain selectable for one release".
        /// </summary>
        [Theory]
        [InlineData("metal", OSPlatformKind.MacOS, GpuBackendKind.Metal)]
        [InlineData("d3d11", OSPlatformKind.Windows, GpuBackendKind.Direct3D11)]
        [InlineData("direct3d11", OSPlatformKind.Windows, GpuBackendKind.Direct3D11)]
        [InlineData("vulkan", OSPlatformKind.Linux, GpuBackendKind.Vulkan)]
        public void AnIncumbentToken_StillPinsTheIncumbent_OverTheNativeDefault(
            string env, OSPlatformKind os, GpuBackendKind expected)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(env, os);

            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.True(selection.WasPinnedByEnvironment);
            Assert.NotEqual(GpuBackendSelector.ProbeOS(os), selection.Backend);
        }

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
        // Each string below is the matrix's `backend` key verbatim. It is one of the two keys the two
        // windows-latest legs differ by: the other is `d3d11Adapter`, which the native leg pins to warp and the
        // incumbent leg leaves empty. ---

        [Theory]
        [InlineData("metal", GpuBackendKind.Metal)]                        // macos-14 leg
        [InlineData("direct3d11", GpuBackendKind.Direct3D11)]              // windows-latest incumbent leg
        [InlineData("direct3d11-native", GpuBackendKind.Direct3D11Native)] // windows-latest native leg
        [InlineData("vulkan", GpuBackendKind.Vulkan)]                      // ubuntu-latest leg
        [InlineData("gl", GpuBackendKind.OpenGL)]                          // (out of CI scope, override still resolves)
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
        [InlineData("vulkan", GpuBackendKind.Vulkan)]
        [InlineData("direct3d11", GpuBackendKind.Direct3D11)]  // alias
        [InlineData("opengl", GpuBackendKind.OpenGL)]          // alias
        [InlineData("metal", GpuBackendKind.Metal)]
        public void Resolve_ValidOverride_IsHonoredAndKeepsTheRawValue(string env, GpuBackendKind expected)
        {
            // Windows would otherwise probe to Direct3D11, so the override is doing the work here.
            GpuBackendSelection selection = GpuBackendSelector.Resolve(env, OSPlatformKind.Windows);

            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.Equal(env, selection.RequestedOverride);
        }

        [Theory]
        [InlineData(" Vulkan ", GpuBackendKind.Vulkan)]
        [InlineData("METAL", GpuBackendKind.Metal)]
        [InlineData("\tD3D11\n", GpuBackendKind.Direct3D11)]
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
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Vulkan)]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.Vulkan)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Direct3D11)]
        public void Resolve_PreferenceWithNoOverride_BeatsTheOsProbe(OSPlatformKind os, GpuBackendKind preference)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(null, os, preference);

            Assert.Equal(preference, selection.Backend);
            Assert.Equal(GpuBackendSource.UserPreference, selection.Source);
            Assert.Null(selection.RequestedOverride);
            Assert.Null(selection.RequestedBackend);
        }

        [Fact]
        public void Resolve_EnvironmentOverride_OutranksThePreference()
        {
            // The whole point of keeping the env var on top: a developer must be able to force a backend for a
            // repro no matter what the player picked in the settings screen.
            GpuBackendSelection selection =
                GpuBackendSelector.Resolve("metal", OSPlatformKind.Windows, GpuBackendKind.Vulkan);

            Assert.Equal(GpuBackendKind.Metal, selection.Backend);
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.Equal("metal", selection.RequestedOverride);
        }

        [Fact]
        public void Resolve_UnparseableOverride_FallsThroughToThePreferenceAndStillReportsTheBadValue()
        {
            // An override that does not parse is not an override, so it falls to the next rung rather than
            // skipping the player's choice and landing on the OS probe. The raw text is still carried so the
            // "your env var did nothing" warning survives.
            GpuBackendSelection selection =
                GpuBackendSelector.Resolve("vulcan", OSPlatformKind.Windows, GpuBackendKind.Vulkan);

            Assert.Equal(GpuBackendKind.Vulkan, selection.Backend);
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

        [Fact]
        public void IsBackendSupported_OpenGL_IsAlwaysFalse()
        {
            // Veldrid may well support GL, but CreateForWindow has no windowed GL path, so offering it to a
            // player would be offering a choice that cannot boot.
            Assert.False(GpuBackendSelector.IsBackendSupported(GpuBackendKind.OpenGL));
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

            // Stable presentation order: a settings dropdown must not reshuffle itself between openings. Each
            // API's native implementation leads its incumbent since 17.40.0.
            var order = new[]
            {
                GpuBackendKind.MetalNative, GpuBackendKind.Metal,
                GpuBackendKind.VulkanNative, GpuBackendKind.Vulkan,
                GpuBackendKind.Direct3D11Native, GpuBackendKind.Direct3D11,
            };
            Assert.Equal(supported.OrderBy(k => Array.IndexOf(order, k)).ToArray(), supported.ToArray());
            Assert.Equal(supported, GpuBackendSelector.SupportedBackends());
        }

        [Fact]
        public void AfterFallback_ReportsWhatRanAndWhatWasAskedFor()
        {
            // The exact contract a consuming game reads to decide "clear the stored preference".
            var requested = new GpuBackendSelection(
                GpuBackendKind.Vulkan, GpuBackendSource.UserPreference, null);

            GpuBackendSelection fell = GpuBackendSelector.AfterFallback(requested, GpuBackendKind.Direct3D11);

            Assert.Equal(GpuBackendKind.Direct3D11, fell.Backend);          // what actually runs
            Assert.Equal(GpuBackendKind.Vulkan, fell.RequestedBackend);     // what the player picked and lost
            Assert.Equal(GpuBackendSource.FallbackAfterFailure, fell.Source);
        }

        [Fact]
        public void AfterFallback_KeepsTheRawOverrideTextForTheDiagnostic()
        {
            var requested = new GpuBackendSelection(
                GpuBackendKind.Vulkan, GpuBackendSource.EnvironmentOverride, "vulkan");

            GpuBackendSelection fell = GpuBackendSelector.AfterFallback(requested, GpuBackendKind.Metal);

            Assert.Equal("vulkan", fell.RequestedOverride);
            Assert.Equal(GpuBackendKind.Vulkan, fell.RequestedBackend);
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

        /// <summary>
        /// The 17.40.0 append's own pure helper, beside <see cref="AfterFallback_ReportsWhatRanAndWhatWasAskedFor"/>
        /// and deliberately shaped the same: what ran, what could not be built, and a source that says which of
        /// the two things went wrong. The difference is the source, and it is the whole content of the append.
        /// </summary>
        [Fact]
        public void AfterMissingDefaultProvider_ReportsTheIncumbentAndKeepsTheDefaultItCouldNotBuild()
        {
            var defaulted = new GpuBackendSelection(
                GpuBackendKind.VulkanNative, GpuBackendSource.OsProbe, null);

            GpuBackendSelection fell =
                GpuBackendSelector.AfterMissingDefaultProvider(defaulted, GpuBackendKind.Vulkan);

            Assert.Equal(GpuBackendKind.Vulkan, fell.Backend);
            Assert.Equal(GpuBackendKind.VulkanNative, fell.RequestedBackend);
            Assert.Equal(GpuBackendSource.DefaultProviderMissing, fell.Source);
            // Not the member a game clears a stored preference on: nothing is stored and nothing failed.
            Assert.NotEqual(GpuBackendSource.FallbackAfterFailure, fell.Source);
        }
    }
}
