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
            // Bad/empty/null override is ignored; the OS probe decides (Windows -> D3D11).
            Assert.Equal(GpuBackendKind.Direct3D11, GpuBackendSelector.Select(env, OSPlatformKind.Windows));
        }

        // --- OS probe mapping ---

        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.Metal)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Vulkan)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.Vulkan)]
        public void Probe_MapsOsToDefaultBackend(OSPlatformKind os, GpuBackendKind expected)
        {
            Assert.Equal(expected, GpuBackendSelector.ProbeOS(os));
            // Same result via Select with no override.
            Assert.Equal(expected, GpuBackendSelector.Select(null, os));
        }

        [Theory]
        [InlineData("metal", true, GpuBackendKind.Metal)]
        [InlineData("vulkan", true, GpuBackendKind.Vulkan)]
        [InlineData("d3d11", true, GpuBackendKind.Direct3D11)]
        [InlineData("direct3d11", true, GpuBackendKind.Direct3D11)]   // alias matching GpuBackendKind.ToString()
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

        // --- the exact KE_GRAPHICS_BACKEND values the cross-platform-gpu CI matrix sets per runner, asserted
        // regardless of the host OS so the override drives the backend (and thus the per-backend golden path). ---

        [Theory]
        [InlineData("metal", GpuBackendKind.Metal)]      // macos-14 leg
        [InlineData("d3d11", GpuBackendKind.Direct3D11)] // windows-latest leg
        [InlineData("vulkan", GpuBackendKind.Vulkan)]    // ubuntu-latest leg
        [InlineData("gl", GpuBackendKind.OpenGL)]        // (out of CI scope, but the override still resolves)
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
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.Metal)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Vulkan)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.Vulkan)]
        public void Resolve_NoOverride_IsOsProbeWithNoRawValue(OSPlatformKind os, GpuBackendKind expected)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(null, os);

            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.OsProbe, selection.Source);
            Assert.Null(selection.RequestedOverride);
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
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.Metal)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Vulkan)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.Vulkan)]
        public void Resolve_UnparseableOverride_FallsBackToProbeButKeepsWhatWasAsked(OSPlatformKind os, GpuBackendKind expected)
        {
            // "vulcan" is the realistic typo: close enough to look right in a launcher, silently not a backend.
            GpuBackendSelection selection = GpuBackendSelector.Resolve("vulcan", os);

            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.UnrecognizedOverride, selection.Source);
            Assert.Equal("vulcan", selection.RequestedOverride);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void Resolve_BlankOverride_CountsAsNoOverrideAtAll(string env)
        {
            // A launcher that exports the var empty has not asked for anything, so it is not a misconfiguration.
            GpuBackendSelection selection = GpuBackendSelector.Resolve(env, OSPlatformKind.Windows);

            Assert.Equal(GpuBackendKind.Direct3D11, selection.Backend);
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
                null, "", "   ", "\t", "vulcan", "directx", "nonsense",
                "metal", "vulkan", "d3d11", "direct3d11", "gl", "opengl",
                " Vulkan ", "METAL", "\tD3D11\n",
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
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.Metal)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Vulkan)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.Vulkan)]
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
        }
    }
}
