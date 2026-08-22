using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Veldrid;

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
        /// engine fell back to the platform's Veldrid incumbent (<see cref="GpuBackendSelector.IncumbentFor"/>,
        /// which is what the OS probe itself answered before 17.40.0) to keep the app bootable.
        /// <see cref="GpuBackendSelection.Backend"/> is what actually runs and
        /// <see cref="GpuBackendSelection.RequestedBackend"/> is what was asked for and did not work. A consuming
        /// game that stores a backend preference MUST clear it when it sees this, or the player retries the same
        /// broken choice on every launch. Appended in 17.23.0.
        /// <para>
        /// A STORED PREFERENCE for a provider-backed backend with no registered provider reports this too, since
        /// 17.40.0. Nothing threw, but the answer a game has to act on is the same one: that stored choice cannot
        /// run in this build and clearing it is what gets the player off it.
        /// </para>
        /// </summary>
        FallbackAfterFailure = 4,

        /// <summary>
        /// The backend the OS probe DEFAULTED to is provider-backed and its provider is not registered in this
        /// process, so the platform's Veldrid incumbent (<see cref="GpuBackendSelector.IncumbentFor"/>) was
        /// created instead. <see cref="GpuBackendSelection.RequestedBackend"/> carries the default that could
        /// not be built. Appended in 17.40.0, the release in which the probe started answering a provider-backed
        /// backend on every platform.
        /// <para>
        /// A MEMBER OF ITS OWN RATHER THAN <see cref="FallbackAfterFailure"/>, because the two say opposite
        /// things to the two readers that act on them. This one means the game has not referenced a native
        /// backend package or has not called its <c>Register()</c>, which is a wiring gap in the APP: nothing
        /// failed, no machine is incapable, and a game that stores a graphics preference must NOT clear it,
        /// because the player's stored choice had nothing to do with this. Reported as
        /// <see cref="FallbackAfterFailure"/> it would make every repinned game that has not taken a native
        /// package read as 100% device-creation failure in telemetry, and would put a "your graphics choice
        /// failed" notice in front of a player who chose nothing.
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
    /// The backend that was ASKED for but did not run, set whenever <paramref name="Source"/> says the engine
    /// took a different one (<see cref="GpuBackendSource.FallbackAfterFailure"/> and, since 17.40.0,
    /// <see cref="GpuBackendSource.DefaultProviderMissing"/>), null otherwise. Paired with
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
        /// native backend must never quietly measure the incumbent and file the number under the native name,
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
        /// clears a stored preference on. A DEFAULTED backend falls back too and reports
        /// <see cref="GpuBackendSource.DefaultProviderMissing"/>, because a game that never asked for the native
        /// package has no choice to clear and has made no mistake a player can fix.
        /// </para>
        /// <para>
        /// <see cref="GpuBackendSource.UnrecognizedOverride"/> is deliberately NOT pinned: the raw value was
        /// present but decided nothing, so the OS probe picked the backend and this is a default like any
        /// other. The two fallback sources are not pinned either, since by then the backend is what the engine
        /// chose rather than what anyone asked for.
        /// </para>
        /// </summary>
        public bool WasPinnedByEnvironment => Source is GpuBackendSource.EnvironmentOverride;
    }

    /// <summary>
    /// Centralizes graphics-backend selection. <see cref="Select()"/> reads the <c>KE_GRAPHICS_BACKEND</c>
    /// environment variable as an override (case-insensitive, one of
    /// <c>metal</c>/<c>metal-native</c>/<c>vulkan</c>/<c>vulkan-native</c>/<c>d3d11</c>/<c>d3d11-native</c>/<c>gl</c>)
    /// and otherwise probes the OS
    /// (macOS -> MetalNative, Windows -> Direct3D11Native, Linux -> VulkanNative,
    /// with VulkanNative as the catch-all default, all three flipped from their Veldrid incumbents in
    /// 17.40.0). <see cref="Resolve()"/> answers the same question but also reports
    /// WHERE the answer came from, via <see cref="GpuBackendSelection"/>. The pure overloads
    /// <see cref="Select(string?, OSPlatformKind)"/> / <see cref="Resolve(string?, OSPlatformKind)"/> make the
    /// logic headless-testable without touching the real environment.
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
                return new GpuBackendSelection(overridden, GpuBackendSource.EnvironmentOverride, envOverride);

            // Preserved verbatim for the diagnostic: an unrecognized override is reported even when the backend
            // ends up coming from the preference below, because "the env var you set did nothing" is exactly the
            // thing a tester needs told.
            string? raw = hasOverride ? envOverride : null;
            if (userPreference is GpuBackendKind preferred)
                return new GpuBackendSelection(preferred, GpuBackendSource.UserPreference, raw);

            GpuBackendSource source = raw is null ? GpuBackendSource.OsProbe : GpuBackendSource.UnrecognizedOverride;
            return new GpuBackendSelection(ProbeOS(os), source, raw);
        }

        /// <summary>
        /// Map a <c>KE_GRAPHICS_BACKEND</c> value to a backend. Case-insensitive, and it trims whitespace.
        /// <para>
        /// Every backend is reachable by name, including the ones this platform's probe never answers, because
        /// naming one variable is the whole ergonomic story of a field soak: <c>d3d11</c> and <c>d3d11-native</c>
        /// are two implementations of the same API and the difference between them is exactly what a soak session
        /// is measuring, so it has to be expressible in the variable a tester already knows. <c>vulkan</c> and
        /// <c>vulkan-native</c> are the second such pair, <c>metal</c> and <c>metal-native</c> the third, and
        /// since the 17.40.0 flip the SUFFIXED token in each pair is what the probe answers by itself. The
        /// incumbent token is the opt-out, and it keeps pointing at the incumbent for as long as the incumbent
        /// exists, which is ONE release (the removal program is
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/683). After that these tokens resolve to a member
        /// whose implementation is gone, and the parse still succeeds, because the enum is append-only.
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
                // diagnostic rather than a silent run on the incumbent implementation under the new name.
                case "d3d11-native": case "direct3d11-native":
                    backend = GpuBackendKind.Direct3D11Native; return true;
                // The same shape for the second native backend (decision V-I1). `vulkan` still means Veldrid's
                // Vulkan and keeps meaning it for the one release the incumbent survives, which is what makes it
                // the kill switch the native Vulkan design leans on: an A/B against the implementation that is
                // now the Linux default is one variable away.
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
        /// One canonical token per backend, in the order the diagnostics name them: each API's incumbent followed
        /// by its native implementation. This is the list the unrecognized-override WARN prints, and it lives
        /// HERE, one screen from the switch above, because it lived as a literal inside
        /// <see cref="GpuDeviceContext"/> and both native appends walked past it. The result was a warning that
        /// named five tokens while the parser accepted six.
        /// <para>
        /// This list and <see cref="TryParseBackend"/> must agree, and the pair of rows in
        /// <c>GpuBackendKindAppendAuditTests</c> named after the warning is what holds them together: every token
        /// here has to parse, TO A DISTINCT BACKEND, and every <see cref="GpuBackendKind"/> has to be named by one
        /// of them. The aliases (<c>direct3d11</c>, <c>vk-native</c>, <c>mtl-native</c>, <c>opengl</c>) are
        /// deliberately absent, since this is what a reader is asked to type rather than everything the parser
        /// tolerates. The distinctness half is why an append adds ONE token here and not both of its pair: a
        /// second token for a backend already named would make this list read as offering seven choices when it
        /// offers six.
        /// </para>
        /// </summary>
        internal const string RecognizedTokens = "metal/metal-native/vulkan/vulkan-native/d3d11/d3d11-native/gl";

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
        /// The incumbent of each API stays reachable for ONE release: by <c>KE_GRAPHICS_BACKEND</c>
        /// (<c>metal</c> / <c>vulkan</c> / <c>d3d11</c>), by a stored user preference, and as the backend a
        /// failed native device creation falls back TO (<see cref="IncumbentFor"/>). The Veldrid removal
        /// program takes them out in the next release.
        /// </para>
        /// <para>
        /// This stays PURE, and answers the same whether or not the platform's native provider is registered in
        /// this process. A consumer that has not taken the native package is handled at CREATION instead, where
        /// a provider-backed backend NOBODY NAMED falls back to <see cref="IncumbentFor"/> with a warning.
        /// Making the probe read the provider registry would have made an OS default depend on process-wide
        /// mutable state, so the same call would answer differently before and after a registration line.
        /// </para>
        /// </summary>
        public static GpuBackendKind ProbeOS(OSPlatformKind os) => os switch
        {
            OSPlatformKind.MacOS => GpuBackendKind.MetalNative,
            OSPlatformKind.Windows => GpuBackendKind.Direct3D11Native,
            OSPlatformKind.Linux => GpuBackendKind.VulkanNative,
            _ => GpuBackendKind.VulkanNative,
        };

        /// <summary>
        /// The VELDRID INCUMBENT for an OS family (macOS -> <see cref="GpuBackendKind.Metal"/>, Windows ->
        /// <see cref="GpuBackendKind.Direct3D11"/>, else <see cref="GpuBackendKind.Vulkan"/>): exactly what
        /// <see cref="ProbeOS"/> answered before 17.40.0, and the backend a failed device creation falls back
        /// TO now that the probe answers a native one.
        /// <para>
        /// A member of its own rather than a second reading of <see cref="ProbeOS"/>, because the two questions
        /// came apart at the flip and only one of them can move again. This map is FROZEN and is deleted whole
        /// with the incumbents in the next release. Until then it is the escape hatch all three rollout designs
        /// call their primary field-diagnostic instrument, and it is what a game gets when it repins without
        /// taking a native backend package.
        /// </para>
        /// </summary>
        public static GpuBackendKind IncumbentFor(OSPlatformKind os) => os switch
        {
            OSPlatformKind.MacOS => GpuBackendKind.Metal,
            OSPlatformKind.Windows => GpuBackendKind.Direct3D11,
            OSPlatformKind.Linux => GpuBackendKind.Vulkan,
            _ => GpuBackendKind.Vulkan,
        };

        // The backends the engine can create a WINDOWED device on, in a stable presentation order: each API's
        // NATIVE implementation followed by that API's Veldrid incumbent. OpenGL is deliberately absent:
        // CreateForWindow has no windowed GL path (Silk would have to own the GL context), so offering it to a
        // player would be offering a choice that cannot boot.
        //
        // The three native kinds joined this list at 17.40.0, when each became what its API's NAME means. The
        // objection that kept them off it until then was that a settings screen offers an API and not an
        // implementation of one, so two rows both reading "Direct3D 11" is a choice nobody outside this repo can
        // make. The flip ANSWERS that objection rather than waiving it: the native row is what "Direct3D 11"
        // means now, and the incumbent row beside it is the one-release opt-out, which a game labels as such.
        // The pair collapses back to one row when the incumbents are removed.
        //
        // A native kind is only ever OFFERED where its provider is registered, because SupportedBackends()
        // probes through IsBackendSupported and a provider-backed kind with no registered provider answers
        // false. So a game that has not taken the native package sees exactly the list it saw before.
        static readonly GpuBackendKind[] _windowCandidates =
        {
            GpuBackendKind.MetalNative, GpuBackendKind.Metal,
            GpuBackendKind.VulkanNative, GpuBackendKind.Vulkan,
            GpuBackendKind.Direct3D11Native, GpuBackendKind.Direct3D11,
        };

        // Machine capability does not change while the process runs, and the Vulkan probe is genuinely
        // expensive (it loads the loader, creates an instance, and enumerates physical devices). A settings
        // screen may ask every frame, so each answer is computed at most once.
        static readonly ConcurrentDictionary<GpuBackendKind, bool> _supportCache = new();

        /// <summary>
        /// Whether this machine can actually run the given backend, as a FUNCTIONAL probe rather than a guess:
        /// Veldrid loads the backend's library, creates an instance, and enumerates devices (for Vulkan that
        /// includes checking the required surface extensions). Answers are cached for the process lifetime.
        /// <para>
        /// A backend supplied by a registered <see cref="IGpuBackendProvider"/> is answered by that provider's own
        /// functional probe instead, because Veldrid cannot answer for a backend it does not implement. With no
        /// provider registered the answer is false and is NOT cached, so a later registration still gets to answer
        /// for real. That false is for a settings screen only: it is not why a device creation fails, since the
        /// creation path asks the registry FIRST and throws
        /// (<see cref="GpuBackendProviders.Require"/>), which is what keeps a forgotten registration from reading
        /// as an incapable machine and falling back to a different backend (decision I2).
        /// </para>
        /// <para>
        /// Always false for <see cref="GpuBackendKind.OpenGL"/>, which Veldrid may well support but the engine
        /// has no windowed device path for. Never throws: a probe that blows up is reported as unsupported.
        /// </para>
        /// <para>
        /// A true answer is NECESSARY but not SUFFICIENT. A broken or partial driver can pass this and still
        /// fail at device creation, which is why <c>GpuDeviceContext.CreateForWindow</c> pairs the probe with a
        /// try/catch fallback rather than trusting it alone. Use this to decide what to OFFER in a settings UI;
        /// use the fallback to survive what actually happens.
        /// </para>
        /// </summary>
        public static bool IsBackendSupported(GpuBackendKind backend)
        {
            if (backend == GpuBackendKind.OpenGL) return false;
            if (GpuBackendProviders.RequiresProvider(backend)) return ProviderSupport(backend);

            return _supportCache.GetOrAdd(backend, static kind =>
            {
                try
                {
                    return GraphicsDevice.IsBackendSupported(ToVeldrid(kind));
                }
                catch (Exception)
                {
                    // A missing loader library throws out of the P/Invoke layer rather than returning false.
                    // "We could not even ask" and "no" are the same answer to a settings screen.
                    return false;
                }
            });
        }

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
                    // Same rule as the Veldrid probe above, and the interface says so: a probe that blows up is an
                    // answer of no, never an exception out of the settings screen that asked.
                    return false;
                }
            }, provider);
        }

        // Drops the cached support answer for one backend, so the next call re-probes. Called when a provider is
        // registered or replaced: the cached value came from a different answerer, or from no answerer at all.
        internal static void InvalidateSupportCache(GpuBackendKind backend)
            => _supportCache.TryRemove(backend, out _);

        /// <summary>
        /// Every backend this machine can actually run a windowed device on, in a stable order (each API's
        /// native implementation followed by its Veldrid incumbent: MetalNative, Metal, VulkanNative, Vulkan,
        /// Direct3D11Native, Direct3D11). A native kind appears only where its provider is registered, so a
        /// game that has not referenced the native package still sees only the three incumbents. This is the
        /// list a game's graphics settings screen must offer: presenting
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

        /// <summary>
        /// The selection to report when the OS default is a provider-backed backend with NO REGISTERED PROVIDER
        /// and <paramref name="incumbent"/> was created instead: the source becomes
        /// <see cref="GpuBackendSource.DefaultProviderMissing"/> and the default that could not be built is
        /// preserved on <see cref="GpuBackendSelection.RequestedBackend"/>.
        /// <para>
        /// Deliberately NOT <see cref="AfterFallback"/>, whose contract says device creation failed and a stored
        /// preference must be cleared. Nothing failed here and nothing is stored: the game has not referenced a
        /// native backend package. Pure, so both the windowed and the headless path can be pinned with no GPU.
        /// </para>
        /// </summary>
        public static GpuBackendSelection AfterMissingDefaultProvider(
            GpuBackendSelection original, GpuBackendKind incumbent)
            => original with
            {
                Backend = incumbent,
                Source = GpuBackendSource.DefaultProviderMissing,
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

        /// <summary>
        /// Map an engine <see cref="GpuBackendKind"/> to the Veldrid backend (internal: Veldrid stays here).
        /// <para>
        /// Provider-backed kinds have no Veldrid equivalent and throw rather than mapping onto the nearest thing,
        /// which is what the discard arm used to do: it answered <c>Metal</c> for anything it did not recognize, so
        /// an appended member asked Veldrid for a Metal device on Windows and failed naming an API nobody had
        /// selected. Nothing reaches here for such a kind today, because every caller branches on
        /// <see cref="GpuBackendProviders.RequiresProvider"/> first. The arm is the belt to that braces, and it
        /// fails saying what actually went wrong.
        /// </para>
        /// </summary>
        internal static GraphicsBackend ToVeldrid(GpuBackendKind kind) => kind switch
        {
            GpuBackendKind.Metal => GraphicsBackend.Metal,
            GpuBackendKind.Vulkan => GraphicsBackend.Vulkan,
            GpuBackendKind.Direct3D11 => GraphicsBackend.Direct3D11,
            GpuBackendKind.OpenGL => GraphicsBackend.OpenGL,
            GpuBackendKind.Direct3D11Native => throw NotAVeldridBackend(kind),
            GpuBackendKind.VulkanNative => throw NotAVeldridBackend(kind),
            GpuBackendKind.MetalNative => throw NotAVeldridBackend(kind),
            _ => throw NotAVeldridBackend(kind),
        };

        static NotSupportedException NotAVeldridBackend(GpuBackendKind kind)
            => new($"{kind} is not a Veldrid backend, so it has no GraphicsBackend to map onto. It is created by "
                + "its registered provider instead (GpuBackendProviders), and every path that could reach here "
                + "checks GpuBackendProviders.RequiresProvider first. Reaching this means that check was skipped.");
    }
}
