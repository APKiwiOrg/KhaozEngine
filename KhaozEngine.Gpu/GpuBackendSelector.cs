using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Where a <see cref="GpuBackendKind"/> choice came from. Carried on <see cref="GpuBackendSelection"/> so a
    /// misconfigured override is distinguishable from a working one: without it, a typo'd
    /// <c>KE_GRAPHICS_BACKEND</c> silently falls back to the OS probe and the run looks like the requested backend
    /// was tried and did not help.
    /// </summary>
    /// <remarks>
    /// The numeric values are a PUBLISHED CONTRACT: consuming games record <c>(int)GpuBackendSource</c> into
    /// telemetry, and captured traces are read back against these numbers. Members are therefore pinned to
    /// explicit values and may only ever be APPENDED. Never reorder, renumber, or remove one.
    /// </remarks>
    public enum GpuBackendSource
    {
        /// <summary>No override or preference was present, so the backend came from the OS probe.</summary>
        OsProbe = 0,

        /// <summary><c>KE_GRAPHICS_BACKEND</c> was set to a recognized backend, and it was honoured.</summary>
        EnvironmentOverride = 1,

        /// <summary>
        /// <c>KE_GRAPHICS_BACKEND</c> was set to something unparseable AND no stored preference was supplied, so
        /// the OS probe decided instead. The raw value is kept on
        /// <see cref="GpuBackendSelection.RequestedOverride"/> for the diagnostic.
        /// </summary>
        UnrecognizedOverride = 2,

        /// <summary>
        /// The backend came from the STORED USER PREFERENCE the consuming game handed in (its in-game graphics
        /// setting), with no environment override outranking it. Appended in 17.23.0.
        /// </summary>
        UserPreference = 3,

        /// <summary>
        /// Device creation on the requested backend FAILED (or the backend failed its support probe), so the
        /// engine fell back to the platform's default backend (<see cref="GpuBackendSelector.ProbeOS"/>) to keep
        /// the app bootable. <see cref="GpuBackendSelection.Backend"/> is what actually runs and
        /// <see cref="GpuBackendSelection.RequestedBackend"/> is what was asked for and did not work. A consuming
        /// game that stores a backend preference MUST clear it when it sees this, or the player retries the same
        /// broken choice on every launch. Appended in 17.23.0.
        /// <para>
        /// A STORED PREFERENCE for a backend with no registered provider reports this too, since 17.40.0.
        /// Nothing threw, but the answer a game has to act on is the same one: that stored choice cannot run in
        /// this build and clearing it is what gets the player off it.
        /// </para>
        /// <para>
        /// A STORED PREFERENCE for a RETIRED member reports this as well, since 18.0.0, and it is the reason the
        /// retirement is safe for a player. The four members the removed incumbent backend used to own
        /// (<see cref="GpuBackendKind.Metal"/>, <see cref="GpuBackendKind.Vulkan"/>,
        /// <see cref="GpuBackendKind.Direct3D11"/>, <see cref="GpuBackendKind.OpenGL"/>) still parse and still
        /// deserialize, so a settings file written by an older build loads. Selection rejects them ahead of the
        /// provider registry and self-heals to that API's native backend through
        /// <see cref="GpuBackendSelector.NativeReplacementFor"/>, the same map the environment token takes, and
        /// the game clears the stored choice on exactly the signal it already handles.
        /// </para>
        /// </summary>
        FallbackAfterFailure = 4,

        /// <summary>
        /// RETIRED IN 18.0.0, AND THE ENGINE NEVER PRODUCES IT ANY MORE. The number stays because this enum is
        /// append-only and captured traces are read back against it, so a 17.40.0 capture that recorded a 5 still
        /// reads as what it meant: the OS default was a provider-backed backend whose provider was not registered
        /// in that process, and the platform's Veldrid incumbent was created instead.
        /// <para>
        /// What removed it is that there is no incumbent to create instead. Every backend is provider-backed
        /// since 18.0.0, so a default with no registered provider is a wiring gap with no second answer, and it
        /// throws <see cref="GpuBackendProviderMissingException"/> naming the package and the one call. In
        /// practice a game does not reach that either: the three native packages ship in the
        /// <c>KhaozEngine.Game2D</c> and <c>KhaozEngine.Game3D</c> umbrellas, and <c>AppWindow</c> registers the
        /// platform's own at boot, so a repinned game boots with no new call of its own.
        /// </para>
        /// </summary>
        DefaultProviderMissing = 5,
    }

    /// <summary>
    /// A backend choice plus its provenance: which backend, where the decision came from, and the RAW
    /// <c>KE_GRAPHICS_BACKEND</c> value that drove it (untrimmed, original case) when one was present.
    /// </summary>
    /// <param name="Backend">The backend that will actually be used.</param>
    /// <param name="Source">Whether the OS probe decided, an override was honoured, or an override failed to parse.</param>
    /// <param name="RequestedOverride">
    /// The raw environment value exactly as read, or null when no non-blank override was present. Deliberately not
    /// normalized: the untouched string is what makes a typo (<c>vulcan</c>) or stray quoting obvious in a log.
    /// </param>
    /// <param name="RequestedBackend">
    /// The backend that was ASKED for but did not run, set whenever the engine took a different one:
    /// <see cref="GpuBackendSource.FallbackAfterFailure"/>, and since 18.0.0 also a RETIRED member redirected to
    /// its native replacement on either the environment-override or the stored-preference path. Null otherwise.
    /// Paired with
    /// <paramref name="Backend"/>, which is what actually runs, this is what lets a consuming game say "your
    /// Vulkan choice failed, you are on Direct3D11" and clear the stored preference that caused it. Added in
    /// 17.23.0 with a default so every existing three-argument construction still compiles.
    /// </param>
    public readonly record struct GpuBackendSelection(
        GpuBackendKind Backend,
        GpuBackendSource Source,
        string? RequestedOverride,
        GpuBackendKind? RequestedBackend = null)
    {
        /// <summary>
        /// True when <c>KE_GRAPHICS_BACKEND</c> PINNED this backend. That is the one provenance for which a
        /// missing provider is a hard error rather than something to route around. Added in 17.40.0, when the
        /// OS probe started answering a provider-backed backend on every platform.
        /// <para>
        /// It splits decision I2 along the line I2 was aimed at: the SOAK SESSION. A pinned provider-backed kind
        /// with no registered provider still throws, because a session that set the variable to measure the
        /// native backend must never quietly measure a different implementation and file the number under that name,
        /// and the same reasoning covers the five cross-platform GPU legs, each of which pins its backend this
        /// way and captures goldens headlessly.
        /// </para>
        /// <para>
        /// EVERY OTHER PROVENANCE FALLS BACK, and <see cref="GpuBackendSource.UserPreference"/> is the one that
        /// had to move. <see cref="GpuBackendSelector.SupportedBackends"/> offers native rows since 17.40.0, so
        /// a player can store <see cref="GpuBackendKind.MetalNative"/>, and a later build that dropped the
        /// package or the <c>Register()</c> line would then throw at boot with the setting that caused it
        /// unreachable from inside the game. It falls back with
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/> instead, which is precisely the signal a game
        /// clears a stored preference on, and a stored RETIRED member takes that same path since 18.0.0.
        /// </para>
        /// <para>
        /// <see cref="GpuBackendSource.UnrecognizedOverride"/> is deliberately NOT pinned: the raw value was
        /// present but decided nothing, so the OS probe picked the backend and this is a default like any
        /// other. The two fallback sources are not pinned either, since by then the backend is what the engine
        /// chose rather than what anyone asked for.
        /// </para>
        /// </summary>
        public bool WasPinnedByEnvironment => Source is GpuBackendSource.EnvironmentOverride;

        /// <summary>
        /// True when a PLAYER'S STORED CHOICE is what put this backend here: either honoured as it stands
        /// (<see cref="GpuBackendSource.UserPreference"/>), or already redirected off a member retired in
        /// 18.0.0, which reports <see cref="GpuBackendSource.FallbackAfterFailure"/> with the retired member on
        /// <see cref="RequestedBackend"/>.
        /// <para>
        /// The mirror of <see cref="WasPinnedByEnvironment"/>, deciding the same question from the other side:
        /// whether a MISSING provider for this backend may be routed around or has to throw. A stored choice
        /// outlives the build that wrote it and the machine it was written on, and the player cannot reach the
        /// setting from a game that refused to boot, so it routes around. Everything else is either the engine's
        /// own default (nothing to route to) or somebody's deliberate pin (routing around it is the
        /// misattribution decision I2 exists to prevent).
        /// </para>
        /// <para>
        /// The retirement arm is narrowed to a RETIRED <see cref="RequestedBackend"/> on purpose, because
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/> is also what a selection reports on the way back
        /// IN after a fallback, carrying the live backend that just failed. That one is the engine's last
        /// attempt and has nothing left to route to.
        /// </para>
        /// </summary>
        public bool CameFromStoredPreference => Source is GpuBackendSource.UserPreference
            || (Source is GpuBackendSource.FallbackAfterFailure
                && RequestedBackend is GpuBackendKind stored && GpuBackendSelector.IsRetired(stored));
    }

    /// <summary>
    /// Centralizes graphics-backend selection. <see cref="Select()"/> reads the <c>KE_GRAPHICS_BACKEND</c>
    /// environment variable as an override (case-insensitive, one of
    /// <c>metal-native</c>/<c>vulkan-native</c>/<c>d3d11-native</c>, with the retired
    /// <c>metal</c>/<c>vulkan</c>/<c>d3d11</c>/<c>gl</c> tokens still accepted and redirected) and otherwise
    /// probes the OS (macOS -> MetalNative, Windows -> Direct3D11Native, Linux -> VulkanNative, with
    /// VulkanNative as the catch-all default). <see cref="Resolve()"/> answers the same question but also
    /// reports WHERE the answer came from, via <see cref="GpuBackendSelection"/>. The pure overloads
    /// <see cref="Select(string?, OSPlatformKind)"/> / <see cref="Resolve(string?, OSPlatformKind)"/> make the
    /// logic headless-testable without touching the real environment.
    /// <para>
    /// RETIREMENT LIVES HERE, and deliberately not in the provider registry. The four members the removed
    /// Veldrid incumbent owned still parse, still deserialize and still name a backend a player may have stored,
    /// so <see cref="Resolve(string?, OSPlatformKind, GpuBackendKind?)"/> rejects them AHEAD of
    /// <see cref="GpuBackendProviders.Require"/> and answers that API's native backend instead, by the one map
    /// <see cref="NativeReplacementFor"/>, whichever of the two paths named it. A retired
    /// member that reaches device creation by being NAMED outright throws
    /// <see cref="GpuBackendRetiredException"/> rather than being redirected, because a caller that named one
    /// implementation is not asking to be quietly given another.
    /// </para>
    /// </summary>
    public static class GpuBackendSelector
    {
        /// <summary>The env var that overrides the OS probe.</summary>
        public const string EnvVarName = "KE_GRAPHICS_BACKEND";

        /// <summary>
        /// Resolve the backend from the live environment: <c>KE_GRAPHICS_BACKEND</c> override if present and
        /// valid, else the OS probe.
        /// </summary>
        public static GpuBackendKind Select() => Resolve().Backend;

        /// <summary>
        /// Pure backend-selection logic. If <paramref name="envOverride"/> is a recognized backend name
        /// (case-insensitive, one of
        /// <c>metal</c>/<c>metal-native</c>/<c>vulkan</c>/<c>vulkan-native</c>/<c>d3d11</c>/<c>d3d11-native</c>/<c>gl</c>)
        /// it wins,
        /// otherwise (null, empty, or unrecognized) the choice falls through to the <paramref name="os"/> probe.
        /// </summary>
        public static GpuBackendKind Select(string? envOverride, OSPlatformKind os)
            => Resolve(envOverride, os).Backend;

        /// <summary>
        /// Pure backend selection with a stored USER PREFERENCE in the middle of the precedence chain
        /// (environment override, then preference, then OS probe). See
        /// <see cref="Resolve(string?, OSPlatformKind, GpuBackendKind?)"/>.
        /// </summary>
        public static GpuBackendKind Select(string? envOverride, OSPlatformKind os, GpuBackendKind? userPreference)
            => Resolve(envOverride, os, userPreference).Backend;

        /// <summary>
        /// The same decision <see cref="Select()"/> makes, read from the live environment, but reported with its
        /// provenance so callers can log it and spot a misconfigured override.
        /// </summary>
        public static GpuBackendSelection Resolve() => Resolve(userPreference: null);

        /// <summary>
        /// Resolve from the live environment with a stored user preference (the consuming game's in-game graphics
        /// setting) sitting between the <c>KE_GRAPHICS_BACKEND</c> override and the OS probe. The engine never
        /// reads that preference from disk itself: it arrives here as DATA, so <c>KhaozEngine.Gpu</c> keeps its
        /// Diagnostics + Primitives dependency set and takes on no settings or persistence edge.
        /// </summary>
        public static GpuBackendSelection Resolve(GpuBackendKind? userPreference)
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), DetectOS(), userPreference);

        /// <summary>
        /// Pure, headless-testable backend selection WITH provenance, and the one decision path
        /// <see cref="Select(string?, OSPlatformKind)"/> is built on so the two can never drift. A null, empty, or
        /// whitespace-only <paramref name="envOverride"/> counts as no override at all
        /// (<see cref="GpuBackendSource.OsProbe"/>, no raw value recorded). A non-blank value that fails to parse
        /// is <see cref="GpuBackendSource.UnrecognizedOverride"/>: the <paramref name="os"/> probe still decides
        /// the backend, but the raw value is preserved so the caller can say what was asked for.
        /// </summary>
        public static GpuBackendSelection Resolve(string? envOverride, OSPlatformKind os)
            => Resolve(envOverride, os, userPreference: null);

        /// <summary>
        /// Pure, headless-testable backend selection with the full precedence chain, highest first:
        /// <list type="number">
        /// <item>a recognized <c>KE_GRAPHICS_BACKEND</c> value (the debug lever, and it must keep winning so a
        /// developer can always force a backend regardless of what the player picked),</item>
        /// <item><paramref name="userPreference"/>, the backend the player chose in game,</item>
        /// <item>the <paramref name="os"/> probe.</item>
        /// </list>
        /// A non-blank <paramref name="envOverride"/> that fails to parse is NOT an override, so it falls through
        /// to the preference like any other miss. Its raw text is still carried on
        /// <see cref="GpuBackendSelection.RequestedOverride"/> either way, so the "you typed <c>vulcan</c>"
        /// diagnostic survives even when a preference supplied the backend. With
        /// <paramref name="userPreference"/> null this is byte-for-byte the pre-17.23.0 behaviour, which is what
        /// keeps every existing call site unchanged.
        /// </summary>
        public static GpuBackendSelection Resolve(string? envOverride, OSPlatformKind os, GpuBackendKind? userPreference)
        {
            bool hasOverride = !string.IsNullOrWhiteSpace(envOverride);
            if (hasOverride && TryParseBackend(envOverride, out GpuBackendKind overridden))
            {
                // A retired token still names an API a tester means, so it runs that API's native backend and
                // says so. The alternative, refusing the boot, would turn every soak script, CI leg and shell
                // alias in the fleet that still says KE_GRAPHICS_BACKEND=metal into a crash, for a variable whose
                // whole purpose is to get a run going. What it must NOT do is run silently: the redirect is
                // recorded on RequestedBackend and warned about at the boot line.
                if (IsRetired(overridden))
                {
                    return new GpuBackendSelection(NativeReplacementFor(overridden, os),
                        GpuBackendSource.EnvironmentOverride, envOverride, overridden);
                }

                return new GpuBackendSelection(overridden, GpuBackendSource.EnvironmentOverride, envOverride);
            }

            // Preserved verbatim for the diagnostic: an unrecognized override is reported even when the backend
            // ends up coming from the preference below, because "the env var you set did nothing" is exactly the
            // thing a tester needs told.
            string? raw = hasOverride ? envOverride : null;
            if (userPreference is GpuBackendKind preferred)
            {
                // A STORED preference is a player's saved choice rather than a tester's variable, so a retired
                // one is reported the way a broken one is: FallbackAfterFailure, with the retired member on
                // RequestedBackend. That is the signal a consuming game already acts on, and acting on it clears
                // the setting, which is the only thing that gets the player off it permanently. Rejecting here,
                // ahead of GpuBackendProviders.Require, is decision 5.2 of the removal design: Require throws by
                // contract, and a saved settings file must never be able to make the engine throw at boot.
                //
                // WHAT IT LANDS ON IS THE SAME MAP THE ENV TOKEN TAKES, NativeReplacementFor rather than
                // ProbeOS, and the two used to disagree: a Windows player's stored Vulkan resolved to
                // Direct3D11Native while KE_GRAPHICS_BACKEND=vulkan on the same machine resolved to
                // VulkanNative. The player chose Vulkan OVER this platform's default, so the faithful
                // replacement is that API's native backend, and dropping them onto the default silently
                // reverses a choice they made deliberately. Where the replacement cannot be created, the
                // ordinary fallback at creation still takes them to the platform default and warns. OpenGL has
                // no native sibling and NativeReplacementFor answers ProbeOS for it, so that one arm is
                // unchanged.
                if (IsRetired(preferred))
                {
                    return new GpuBackendSelection(NativeReplacementFor(preferred, os),
                        GpuBackendSource.FallbackAfterFailure, raw, preferred);
                }

                return new GpuBackendSelection(preferred, GpuBackendSource.UserPreference, raw);
            }

            GpuBackendSource source = raw is null ? GpuBackendSource.OsProbe : GpuBackendSource.UnrecognizedOverride;
            return new GpuBackendSelection(ProbeOS(os), source, raw);
        }

        /// <summary>
        /// Whether <paramref name="backend"/> is one of the four members RETIRED in 18.0.0 with the Veldrid
        /// incumbent that implemented them: <see cref="GpuBackendKind.Metal"/>,
        /// <see cref="GpuBackendKind.Vulkan"/>, <see cref="GpuBackendKind.Direct3D11"/> and
        /// <see cref="GpuBackendKind.OpenGL"/>.
        /// <para>
        /// The members are kept because the enum is append-only and a consuming game has persisted them as a
        /// player's saved choice. They are not repointed at the native implementations, which is the tidy-looking
        /// move the removal design rules out by name: repointing would silently move every Windows tester's
        /// stored <c>Direct3D11</c> onto a different implementation with no rebuild signal and no player notice.
        /// </para>
        /// </summary>
        public static bool IsRetired(GpuBackendKind backend) => backend is GpuBackendKind.Metal
            or GpuBackendKind.Vulkan or GpuBackendKind.Direct3D11 or GpuBackendKind.OpenGL;

        /// <summary>
        /// The live backend a RETIRED member's API is served by now: Metal -&gt;
        /// <see cref="GpuBackendKind.MetalNative"/>, Vulkan -&gt; <see cref="GpuBackendKind.VulkanNative"/>,
        /// Direct3D11 -&gt; <see cref="GpuBackendKind.Direct3D11Native"/>.
        /// <para>
        /// <see cref="GpuBackendKind.OpenGL"/> has NO native replacement, because the engine never had an OpenGL
        /// implementation of its own and is not gaining one, so it answers <paramref name="os"/>'s own default
        /// through <see cref="ProbeOS"/>. Anything not retired answers itself, so this is safe to call blind.
        /// </para>
        /// </summary>
        public static GpuBackendKind NativeReplacementFor(GpuBackendKind backend, OSPlatformKind os) => backend switch
        {
            GpuBackendKind.Metal => GpuBackendKind.MetalNative,
            GpuBackendKind.Vulkan => GpuBackendKind.VulkanNative,
            GpuBackendKind.Direct3D11 => GpuBackendKind.Direct3D11Native,
            GpuBackendKind.OpenGL => ProbeOS(os),
            _ => backend,
        };

        /// <summary>
        /// The one sentence a reader gets when a retired member was asked for and something else ran, built here
        /// rather than at the log call so a test reads exactly what a session log does. It names the retirement,
        /// the release, what is running instead, and the token to move to.
        /// </summary>
        public static string RetirementWarning(GpuBackendKind retired, GpuBackendKind replacement)
            => $"{retired} names the Veldrid backend removed in 18.0.0. Running {replacement} instead. "
                + $"The {retired} enum member is kept so a stored graphics preference still loads, and it no "
                + $"longer has an implementation behind it: set KE_GRAPHICS_BACKEND to one of "
                + $"{RecognizedTokens} and store {replacement} instead.";

        /// <summary>
        /// Map a <c>KE_GRAPHICS_BACKEND</c> value to a backend. Case-insensitive, and it trims whitespace.
        /// <para>
        /// Every backend is reachable by name, including the ones this platform's probe never answers, because
        /// naming one variable is the whole ergonomic story of a field soak.
        /// </para>
        /// <para>
        /// THE FOUR RETIRED TOKENS STILL PARSE, to the retired member, and that is what keeps this function a
        /// pure lookup rather than a policy. <c>metal</c>, <c>vulkan</c>, <c>d3d11</c> and <c>gl</c> named the
        /// Veldrid implementations removed in 18.0.0, and a token map that quietly answered a DIFFERENT member
        /// than the one it has always answered would make a log line and a telemetry header disagree with the
        /// variable that produced them. The redirect onto the API's native backend, and the warning that says so,
        /// belong to <see cref="Resolve(string?, OSPlatformKind, GpuBackendKind?)"/>, which is the layer allowed
        /// to have a policy.
        /// </para>
        /// </summary>
        public static bool TryParseBackend(string? value, out GpuBackendKind backend)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "metal": backend = GpuBackendKind.Metal; return true;
                case "vulkan": backend = GpuBackendKind.Vulkan; return true;
                case "d3d11": case "direct3d11": backend = GpuBackendKind.Direct3D11; return true;
                // Suffixed rather than a second variable. The whole token is matched, so these can never be
                // confused with the two above, and a tester who typo'd the suffix gets the UnrecognizedOverride
                // diagnostic rather than a silent run on a different implementation under the new name.
                case "d3d11-native": case "direct3d11-native":
                    backend = GpuBackendKind.Direct3D11Native; return true;
                // The same shape for the second native backend (decision V-I1). `vulkan` meant Veldrid's Vulkan until
                // 18.0.0, which made it the kill switch the native Vulkan design leaned on: an A/B against the
                // implementation that is now the Linux default was one variable away. With the incumbent gone
                // the token is retired and redirects here with a WARN.
                case "vulkan-native": case "vk-native":
                    backend = GpuBackendKind.VulkanNative; return true;
                // And the third (decision M-I1). The A/B this pair buys is worth more than either of the others,
                // because `metal` is the family the fleet's reference images are baked on, so a suspected
                // difference on a Mac has to be answerable on the same build without a rebuild or a re-bake.
                case "metal-native": case "mtl-native":
                    backend = GpuBackendKind.MetalNative; return true;
                case "gl": case "opengl": backend = GpuBackendKind.OpenGL; return true;
                default: backend = default; return false;
            }
        }

        /// <summary>
        /// One canonical token per LIVE backend. This is the list the unrecognized-override WARN prints, and it
        /// lives HERE, one screen from the switch above, because it lived as a literal inside
        /// <see cref="GpuDeviceContext"/> and both native appends walked past it. The result was a warning that
        /// named five tokens while the parser accepted six.
        /// <para>
        /// This list and <see cref="TryParseBackend"/> must agree, and the pair of rows in
        /// <c>GpuBackendKindAppendAuditTests</c> named after the warning is what holds them together: every token
        /// here has to parse, TO A DISTINCT AND LIVE BACKEND, and every live <see cref="GpuBackendKind"/> has to
        /// be named by one of them. The aliases (<c>vk-native</c>, <c>mtl-native</c>, <c>direct3d11-native</c>)
        /// are deliberately absent, since this is what a reader is asked to type rather than everything the
        /// parser tolerates.
        /// </para>
        /// <para>
        /// THE FOUR RETIRED TOKENS ARE ABSENT TOO, since 18.0.0, and that is the point of the list rather than an
        /// omission: a diagnostic that offers <c>metal</c> as a choice is offering a backend that no longer
        /// exists. They still PARSE, so a script that sets one keeps working, and the redirect warns.
        /// </para>
        /// </summary>
        internal const string RecognizedTokens = "metal-native/vulkan-native/d3d11-native";

        /// <summary>
        /// The default backend for an OS family: macOS -> <see cref="GpuBackendKind.MetalNative"/>,
        /// Windows -> <see cref="GpuBackendKind.Direct3D11Native"/>, Linux and everything else ->
        /// <see cref="GpuBackendKind.VulkanNative"/>.
        /// <para>
        /// FLIPPED IN 17.40.0. Every arm used to answer the API's Veldrid incumbent and to say, at length, that
        /// it was waiting on the last gate of that backend's rollout (decisions I4, V-RO3 and M-RO5). The flip
        /// was taken by decision on 2026-08-22 ahead of the gates that were still open, and each of the three
        /// designs carries a dated addendum in its rollout record saying so, naming which gates remain open as
        /// issues. Read those before concluding from a green run that every gate was met.
        /// </para>
        /// <para>
        /// SINCE 18.0.0 THIS IS ALSO WHAT A FALLBACK LANDS ON. The Veldrid incumbent is gone and
        /// <c>IncumbentFor</c> went with it, so a failed device creation and an unrecognized override both end
        /// up here, as does a stored preference for <see cref="GpuBackendKind.OpenGL"/>, the one retired member
        /// with no native sibling. The other three retired members resolve to their own API's native backend
        /// (<see cref="NativeReplacementFor"/>) and reach this only if that backend cannot be created. There is exactly one default per platform now,
        /// which is what makes the fallback's "nothing to fall back TO when the request already IS the default"
        /// guard a complete statement rather than a first approximation.
        /// </para>
        /// <para>
        /// This stays PURE, and answers the same whether or not the platform's native provider is registered in
        /// this process. Making the probe read the provider registry would have made an OS default depend on
        /// process-wide mutable state, so the same call would answer differently before and after a registration
        /// line. A consumer that registered nothing is handled at CREATION instead, where
        /// <see cref="GpuBackendProviders.Require"/> throws and names the package and the call.
        /// </para>
        /// </summary>
        public static GpuBackendKind ProbeOS(OSPlatformKind os) => os switch
        {
            OSPlatformKind.MacOS => GpuBackendKind.MetalNative,
            OSPlatformKind.Windows => GpuBackendKind.Direct3D11Native,
            OSPlatformKind.Linux => GpuBackendKind.VulkanNative,
            _ => GpuBackendKind.VulkanNative,
        };

        // The backends the engine can create a WINDOWED device on, in a stable presentation order. One row per
        // API since 18.0.0, because there is one implementation of each again: the pair of rows the 17.40.0 flip
        // created (native plus its Veldrid incumbent, which a game had to label as an opt-out) collapsed back to
        // one the day the incumbent was deleted, exactly as the flip's own note said it would.
        //
        // OpenGL is deliberately absent and always was: CreateForWindow has no windowed GL path (Silk would have
        // to own the GL context), so offering it to a player would be offering a choice that cannot boot. It is
        // now absent for a second reason as well, being retired.
        //
        // A kind is only ever OFFERED where its provider is registered, because SupportedBackends() probes
        // through IsBackendSupported and a kind with no registered provider answers false. So a settings screen
        // on a machine that took only its own platform's package sees exactly one row.
        static readonly GpuBackendKind[] _windowCandidates =
        {
            GpuBackendKind.MetalNative,
            GpuBackendKind.VulkanNative,
            GpuBackendKind.Direct3D11Native,
        };

        // Machine capability does not change while the process runs, and the Vulkan probe is genuinely
        // expensive (it loads the loader, creates an instance, and enumerates physical devices). A settings
        // screen may ask every frame, so each answer is computed at most once.
        static readonly ConcurrentDictionary<GpuBackendKind, bool> _supportCache = new();

        /// <summary>
        /// Whether this machine can actually run the given backend, as a FUNCTIONAL probe rather than a guess:
        /// the backend's registered <see cref="IGpuBackendProvider"/> loads its own loader library, creates an
        /// instance, and enumerates devices (for Vulkan that includes checking the required surface extensions).
        /// Answers are cached for the process lifetime.
        /// <para>
        /// With no provider registered the answer is false and is NOT cached, so a later registration still gets
        /// to answer for real. That false is for a settings screen only: it is not why a device creation fails,
        /// since the creation path asks the registry FIRST and throws
        /// (<see cref="GpuBackendProviders.Require"/>), which is what keeps a forgotten registration from reading
        /// as an incapable machine and falling back to a different backend (decision I2).
        /// </para>
        /// <para>
        /// Always false for a RETIRED member, which has no implementation to probe. Never throws: a probe that
        /// blows up is reported as unsupported.
        /// </para>
        /// <para>
        /// A true answer is NECESSARY but not SUFFICIENT. A broken or partial driver can pass this and still
        /// fail at device creation, which is why <c>GpuDeviceContext.CreateForWindow</c> pairs the probe with a
        /// try/catch fallback rather than trusting it alone. Use this to decide what to OFFER in a settings UI;
        /// use the fallback to survive what actually happens.
        /// </para>
        /// </summary>
        public static bool IsBackendSupported(GpuBackendKind backend)
            => !IsRetired(backend) && ProviderSupport(backend);

        // The provider-backed half of the probe. Nothing is cached until a provider exists to answer, because a
        // cached "no" from before registration would outlive the registration for the rest of the process and
        // freeze a settings screen on a backend that is now perfectly available.
        static bool ProviderSupport(GpuBackendKind backend)
        {
            if (!GpuBackendProviders.TryGet(backend, out IGpuBackendProvider? provider) || provider is null)
                return false;

            return _supportCache.GetOrAdd(backend, static (_, probe) =>
            {
                try
                {
                    return probe.IsSupported();
                }
                catch (Exception)
                {
                    // The interface says so: a probe that blows up is an answer of no, never an exception out of
                    // the settings screen that asked.
                    return false;
                }
            }, provider);
        }

        // Drops the cached support answer for one backend, so the next call re-probes. Called when a provider is
        // registered or replaced: the cached value came from a different answerer, or from no answerer at all.
        internal static void InvalidateSupportCache(GpuBackendKind backend)
            => _supportCache.TryRemove(backend, out _);

        /// <summary>
        /// Every backend this machine can actually run a windowed device on, in a stable order: MetalNative,
        /// VulkanNative, Direct3D11Native. A kind appears only where its provider is registered, and a RETIRED
        /// member never appears at all, so a settings screen cannot offer the player a backend that was removed.
        /// This is the list a game's graphics settings screen must offer: presenting
        /// a backend that is not on it hands the player a choice that cannot start, which is precisely the
        /// lock-out the fallback exists to catch after the fact. Probed via <see cref="IsBackendSupported"/>, so
        /// the first call pays the probe cost and later ones are cached.
        /// </summary>
        public static IReadOnlyList<GpuBackendKind> SupportedBackends()
        {
            var supported = new List<GpuBackendKind>(_windowCandidates.Length);
            foreach (GpuBackendKind kind in _windowCandidates)
            {
                if (IsBackendSupported(kind)) supported.Add(kind);
            }
            return supported;
        }

        /// <summary>
        /// The selection to report once device creation on <paramref name="original"/>'s backend has FAILED and
        /// <paramref name="fallbackBackend"/> was created instead: the backend becomes what actually runs, the
        /// source becomes <see cref="GpuBackendSource.FallbackAfterFailure"/>, and what was asked for is preserved
        /// on <see cref="GpuBackendSelection.RequestedBackend"/>. Pure, so the reporting contract is testable with
        /// no GPU. <c>GpuDeviceContext</c> uses this for its own fallback; a consumer driving its own retry through
        /// the explicit-backend <c>CreateForWindow</c> overload should use it too, so both report identically.
        /// </summary>
        public static GpuBackendSelection AfterFallback(GpuBackendSelection original, GpuBackendKind fallbackBackend)
            => original with
            {
                Backend = fallbackBackend,
                Source = GpuBackendSource.FallbackAfterFailure,
                RequestedBackend = original.Backend,
            };

        /// <summary>Detect the running OS family via <see cref="RuntimeInformation"/>.</summary>
        public static OSPlatformKind DetectOS()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return OSPlatformKind.MacOS;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return OSPlatformKind.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return OSPlatformKind.Linux;
            return OSPlatformKind.Unknown;
        }

    }
}
