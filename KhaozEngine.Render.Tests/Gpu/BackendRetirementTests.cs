using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The 18.0.0 retirement of the four <see cref="GpuBackendKind"/> members the deleted Veldrid backend
    /// implemented, checked at the three places a consumer meets it: a stored preference, a
    /// <c>KE_GRAPHICS_BACKEND</c> token, and a backend named outright in code.
    /// <para>
    /// THE SHAPE IS NOT SYMMETRIC ACROSS THE THREE, on purpose, and that asymmetry is the feature. A saved
    /// settings file must never be able to make the engine throw at boot, so a stored preference self-heals and
    /// is reported through the ordinary <see cref="GpuBackendSource.FallbackAfterFailure"/> path the consuming
    /// game already clears a stored choice on. A token in a soak script must keep the run going, so it redirects
    /// with a warning. Code that NAMES a retired member is a compile-time decision by a developer, so it throws.
    /// </para>
    /// <para>
    /// Section 5.2 of <c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c> rules out the fourth possibility,
    /// which is the tidy-looking one: repointing the member at its API's native implementation. That would move
    /// every Windows tester's stored <c>Direct3D11</c> onto a different implementation with no rebuild signal and
    /// no player notice, and the whole soak measurement rests on knowing which implementation ran.
    /// </para>
    /// </summary>
    public sealed class BackendRetirementTests
    {
        /// <summary>The retirement list itself, pinned. Four members, and no live one on it.</summary>
        [Theory]
        [InlineData(GpuBackendKind.Metal, true)]
        [InlineData(GpuBackendKind.Vulkan, true)]
        [InlineData(GpuBackendKind.Direct3D11, true)]
        [InlineData(GpuBackendKind.OpenGL, true)]
        [InlineData(GpuBackendKind.MetalNative, false)]
        [InlineData(GpuBackendKind.VulkanNative, false)]
        [InlineData(GpuBackendKind.Direct3D11Native, false)]
        public void IsRetired_NamesTheFourVeldridMembersAndNothingElse(GpuBackendKind kind, bool expected)
            => Assert.Equal(expected, GpuBackendSelector.IsRetired(kind));

        /// <summary>
        /// The members keep their published numbers. This is the reason the retirement is a retirement rather
        /// than a deletion: a consuming game persists the player's chosen backend, and renumbering would silently
        /// repoint every saved graphics setting at a different backend.
        /// </summary>
        [Fact]
        public void TheRetiredMembers_KeepTheirPublishedNumbers()
        {
            Assert.Equal(0, (int)GpuBackendKind.Metal);
            Assert.Equal(1, (int)GpuBackendKind.Vulkan);
            Assert.Equal(2, (int)GpuBackendKind.Direct3D11);
            Assert.Equal(3, (int)GpuBackendKind.OpenGL);
        }

        // --- a stored preference: the player's saved choice, which must never crash a boot ---

        /// <summary>
        /// THE ROW THIS WHOLE RETIREMENT IS BUILT AROUND. A settings file written by a 17.x build still says
        /// <c>Direct3D11</c>, and a Windows tester launching an 18.0.0 build has to get a running game. The
        /// engine self-heals to the platform's native backend and reports
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/>, which is exactly the signal a consuming game
        /// already handles by clearing the stored choice and telling the player.
        /// </summary>
        [Theory]
        [InlineData(GpuBackendKind.Direct3D11, OSPlatformKind.Windows, GpuBackendKind.Direct3D11Native)]
        [InlineData(GpuBackendKind.Metal, OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        [InlineData(GpuBackendKind.Vulkan, OSPlatformKind.Linux, GpuBackendKind.VulkanNative)]
        // A cross-platform stored value, which is what a settings file copied between machines produces. It
        // resolves to THIS platform's default rather than to the retired member's own API, because the API it
        // named cannot run here at all.
        [InlineData(GpuBackendKind.Direct3D11, OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        // OpenGL has no native replacement anywhere, so every platform answers its own default.
        [InlineData(GpuBackendKind.OpenGL, OSPlatformKind.Linux, GpuBackendKind.VulkanNative)]
        public void AStoredPreferenceForARetiredBackend_SelfHealsAndReportsFallback(
            GpuBackendKind stored, OSPlatformKind os, GpuBackendKind expected)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(null, os, stored);

            Assert.Equal(expected, selection.Backend);
            Assert.Equal(GpuBackendSource.FallbackAfterFailure, selection.Source);
            // What was asked for rides along, so a game can name the dead backend in the notice it shows.
            Assert.Equal(stored, selection.RequestedBackend);
        }

        /// <summary>
        /// The negative half, which is the assertion that trips if the redirect is ever widened. A LIVE stored
        /// preference is still honoured as a preference and reports <see cref="GpuBackendSource.UserPreference"/>,
        /// so a game does not clear a choice that works.
        /// </summary>
        [Fact]
        public void AStoredPreferenceForALiveBackend_IsStillHonouredAsAPreference()
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(
                null, OSPlatformKind.Windows, GpuBackendKind.VulkanNative);

            Assert.Equal(GpuBackendKind.VulkanNative, selection.Backend);
            Assert.Equal(GpuBackendSource.UserPreference, selection.Source);
            Assert.Null(selection.RequestedBackend);
        }

        /// <summary>
        /// The boot line for a self-healed preference, which has to read nothing like the device-failure line
        /// beside it. Both are <see cref="GpuBackendSource.FallbackAfterFailure"/> to the GAME, because the
        /// action is the same, and a HUMAN reading a log needs to know a driver did not fall over.
        /// </summary>
        [Fact]
        public void TheBootHeader_NamesARetiredPreference_AsARetirementRatherThanAFailure()
        {
            string line = GpuDeviceContext.SelectionLine(GpuBackendSelector.Resolve(
                null, OSPlatformKind.Windows, GpuBackendKind.Direct3D11));

            Assert.Equal("GPU backend: Direct3D11Native (fallback, Direct3D11 retired)", line);
            Assert.DoesNotContain("failed", line, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// And the failure line it must not be confused with, asserted here rather than taken on trust, because
        /// the two are one enum member apart and the whole value of the split is that they read differently.
        /// </summary>
        [Fact]
        public void TheBootHeader_StillSaysFailed_ForAnActualDeviceFailure()
        {
            string line = GpuDeviceContext.SelectionLine(GpuBackendSelector.AfterFallback(
                new GpuBackendSelection(GpuBackendKind.VulkanNative, GpuBackendSource.UserPreference, null),
                GpuBackendKind.Direct3D11Native));

            Assert.Equal("GPU backend: Direct3D11Native (fallback, VulkanNative failed)", line);
        }

        /// <summary>
        /// The telemetry header a triage reader opens. The source reaches the capture by NAME and the retired
        /// backend rides along on <c>requestedBackend</c>, so a fleet-wide count of stored settings still naming
        /// a dead backend is one query rather than a guess.
        /// </summary>
        [Fact]
        public void TheTelemetryHeader_CarriesTheRetiredBackend_ByName()
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(
                null, OSPlatformKind.Windows, GpuBackendKind.Direct3D11);

            var info = new TelemetrySessionInfo().WithGpu(selection, "Microsoft Basic Render Driver", null, null);

            Assert.Equal("Direct3D11Native", info.GpuBackend);
            Assert.Equal("FallbackAfterFailure", info.GpuBackendSource);
            Assert.Equal("Direct3D11", info.GpuRequestedBackend);
        }

        // --- a KE_GRAPHICS_BACKEND token: a tester's variable, which must keep the run going ---

        /// <summary>
        /// The retired tokens still PARSE, to the retired member, and that is what keeps
        /// <see cref="GpuBackendSelector.TryParseBackend"/> a pure lookup rather than a policy. A token map that
        /// quietly answered a different member than the one it has always answered would make a log line and a
        /// telemetry header disagree with the variable that produced them.
        /// </summary>
        [Theory]
        [InlineData("metal", GpuBackendKind.Metal)]
        [InlineData("vulkan", GpuBackendKind.Vulkan)]
        [InlineData("d3d11", GpuBackendKind.Direct3D11)]
        [InlineData("direct3d11", GpuBackendKind.Direct3D11)]
        [InlineData("gl", GpuBackendKind.OpenGL)]
        [InlineData("opengl", GpuBackendKind.OpenGL)]
        public void TheRetiredTokens_StillParseToTheRetiredMember(string token, GpuBackendKind expected)
        {
            Assert.True(GpuBackendSelector.TryParseBackend(token, out GpuBackendKind parsed));
            Assert.Equal(expected, parsed);
        }

        /// <summary>
        /// THE OTHER ROW THE RETIREMENT IS BUILT AROUND, and the reason the token is redirected rather than
        /// refused: every soak script, CI leg and shell alias in the fleet that still says
        /// <c>KE_GRAPHICS_BACKEND=metal</c> keeps working, on the implementation that serves that API now. The
        /// run is still ATTRIBUTED correctly, because the backend on the selection is the one that will actually
        /// create the device.
        /// </summary>
        [Theory]
        [InlineData("metal", OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        [InlineData("vulkan", OSPlatformKind.Linux, GpuBackendKind.VulkanNative)]
        [InlineData("d3d11", OSPlatformKind.Windows, GpuBackendKind.Direct3D11Native)]
        // The API is named across platforms too: a Mac asked for Vulkan gets the native Vulkan backend, which is
        // a real answer, unlike the retired member.
        [InlineData("vulkan", OSPlatformKind.MacOS, GpuBackendKind.VulkanNative)]
        // gl has no implementation to redirect to, so it lands on the platform default.
        [InlineData("gl", OSPlatformKind.MacOS, GpuBackendKind.MetalNative)]
        public void ARetiredToken_ResolvesToTheApisNativeBackend(
            string token, OSPlatformKind os, GpuBackendKind expected)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve(token, os);

            Assert.Equal(expected, selection.Backend);
            // Still an override, because one really was honoured. The redirect is what rides on
            // RequestedBackend, and it is what the boot line and the WARN below are keyed off.
            Assert.Equal(GpuBackendSource.EnvironmentOverride, selection.Source);
            Assert.Equal(token, selection.RequestedOverride);
        }

        /// <summary>
        /// A redirected token still says so on the boot line, which is the difference between this and the
        /// silent implementation swap the design refuses.
        /// </summary>
        [Fact]
        public void TheBootHeader_NamesARedirectedToken_AsARetirement()
            => Assert.Equal(
                $"GPU backend: MetalNative ({GpuBackendSelector.EnvVarName} override, Metal retired)",
                GpuDeviceContext.SelectionLine(
                    GpuBackendSelector.Resolve("metal", OSPlatformKind.MacOS)));

        /// <summary>
        /// The WARN itself, read as a string rather than through the logger, the same way
        /// <c>GpuDeviceContext.FallbackWarning</c> and the unrecognized-override warning are: a test asserting on
        /// a reconstruction of a log line passes while the line itself says something else.
        /// </summary>
        [Fact]
        public void TheRetirementWarning_NamesTheRetirementTheReleaseAndTheReplacement()
        {
            string warning = GpuBackendSelector.RetirementWarning(
                GpuBackendKind.Metal, GpuBackendKind.MetalNative);

            Assert.Contains("Metal names the Veldrid backend removed in 18.0.0", warning, StringComparison.Ordinal);
            Assert.Contains("Running MetalNative instead", warning, StringComparison.Ordinal);
            Assert.Contains("metal-native", warning, StringComparison.Ordinal);
        }

        /// <summary>
        /// The canonical token list a diagnostic prints offers LIVE backends only. Offering <c>metal</c> as a
        /// choice would be offering a backend that no longer exists, which is the failure the list exists to
        /// prevent in the other direction (it once named five tokens while the parser accepted six).
        /// </summary>
        [Fact]
        public void TheUnrecognizedOverrideWarning_OffersNoRetiredToken()
        {
            string warning = GpuDeviceContext.UnrecognizedOverrideWarning("metel", GpuBackendKind.MetalNative);

            Assert.Contains("metal-native", warning, StringComparison.Ordinal);
            Assert.Contains("vulkan-native", warning, StringComparison.Ordinal);
            Assert.Contains("d3d11-native", warning, StringComparison.Ordinal);
            // The retired tokens are substrings of their native successors, so the assertion has to be about the
            // token list rather than about the word: no bare token stands on its own between the separators.
            foreach (string retired in new[] { "metal", "vulkan", "d3d11", "gl" })
            {
                Assert.DoesNotContain($"/{retired}/", warning, StringComparison.Ordinal);
                Assert.DoesNotContain($" {retired}/", warning, StringComparison.Ordinal);
            }
        }

        // --- naming one in code: a developer's decision, which throws ---

        /// <summary>
        /// A retired member named outright throws, and it throws AHEAD of the provider registry. The ordering is
        /// the point: leaving it to <see cref="GpuBackendProviders.Require"/> would report a retirement as a
        /// forgotten registration and send a reader off to add a package reference that would not help.
        /// </summary>
        [Theory]
        [InlineData(GpuBackendKind.Metal)]
        [InlineData(GpuBackendKind.Vulkan)]
        [InlineData(GpuBackendKind.Direct3D11)]
        [InlineData(GpuBackendKind.OpenGL)]
        public void NamingARetiredBackend_ThrowsTheRetiredExceptionRatherThanTheMissingProviderOne(
            GpuBackendKind retired)
        {
            var ex = Assert.Throws<GpuBackendRetiredException>(
                () => GpuDeviceContext.PreflightProvider(
                    new GpuBackendSelection(retired, GpuBackendSource.OsProbe, null), allowFallback: true, out _));

            Assert.Equal(retired, ex.Backend);
            Assert.Contains("retired in 18.0.0", ex.Message, StringComparison.Ordinal);
            Assert.Contains($"GpuBackendKind.{ex.Replacement}", ex.Message, StringComparison.Ordinal);
            Assert.False(GpuBackendSelector.IsRetired(ex.Replacement));
        }

        /// <summary>
        /// A settings screen never offers a retired member, which is the other half of what makes the stored
        /// preference safe: the engine self-heals the ones already saved, and stops new ones being created.
        /// </summary>
        [Fact]
        public void SupportedBackends_NeverOffersARetiredMember()
        {
            foreach (GpuBackendKind kind in GpuBackendSelector.SupportedBackends())
                Assert.False(GpuBackendSelector.IsRetired(kind));
        }

        /// <summary>
        /// And the probe underneath it answers false for a retired member unconditionally, without asking the
        /// registry. A retired kind has no implementation to probe, so a true answer could only ever come from
        /// something else having been registered under its number.
        /// </summary>
        [Theory]
        [InlineData(GpuBackendKind.Metal)]
        [InlineData(GpuBackendKind.Vulkan)]
        [InlineData(GpuBackendKind.Direct3D11)]
        [InlineData(GpuBackendKind.OpenGL)]
        public void IsBackendSupported_IsFalseForEveryRetiredMember(GpuBackendKind retired)
            => Assert.False(GpuBackendSelector.IsBackendSupported(retired));
    }
}
