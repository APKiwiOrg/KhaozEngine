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
        /// engine fell back to the OS-probe default to keep the app bootable.
        /// <see cref="GpuBackendSelection.Backend"/> is what actually runs and
        /// <see cref="GpuBackendSelection.RequestedBackend"/> is what was asked for and did not work. A consuming
        /// game that stores a backend preference MUST clear it when it sees this, or the player retries the same
        /// broken choice on every launch. Appended in 17.23.0.
        /// </summary>
        FallbackAfterFailure = 4,
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
    /// The backend that was ASKED for but did not work, set only when <paramref name="Source"/> is
    /// <see cref="GpuBackendSource.FallbackAfterFailure"/> (null otherwise). Paired with
    /// <paramref name="Backend"/>, which is what actually runs, this is what lets a consuming game say "your
    /// Vulkan choice failed, you are on Direct3D11" and clear the stored preference that caused it. Added in
    /// 17.23.0 with a default so every existing three-argument construction still compiles.
    /// </param>
    public readonly record struct GpuBackendSelection(
        GpuBackendKind Backend,
        GpuBackendSource Source,
        string? RequestedOverride,
        GpuBackendKind? RequestedBackend = null);

    /// <summary>
    /// Centralizes graphics-backend selection. <see cref="Select()"/> reads the <c>KE_GRAPHICS_BACKEND</c>
    /// environment variable as an override (case-insensitive, one of
    /// <c>metal</c>/<c>vulkan</c>/<c>vulkan-native</c>/<c>d3d11</c>/<c>d3d11-native</c>/<c>gl</c>) and otherwise probes the OS
    /// (macOS -> Metal, Windows -> Direct3D11, Linux -> Vulkan,
    /// with Vulkan as the catch-all default). <see cref="Resolve()"/> answers the same question but also reports
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
        /// <c>metal</c>/<c>vulkan</c>/<c>vulkan-native</c>/<c>d3d11</c>/<c>d3d11-native</c>/<c>gl</c>) it wins,
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
        /// Every backend is reachable by name, including the ones the OS probe never picks, because naming one
        /// variable is the whole ergonomic story of a field soak: <c>d3d11</c> and <c>d3d11-native</c> are two
        /// implementations of the same API and the difference between them is exactly what a soak session is
        /// measuring, so it has to be expressible in the variable a tester already knows. <c>vulkan</c> and
        /// <c>vulkan-native</c> are the second such pair, and the incumbent token in each pair keeps pointing at
        /// the incumbent indefinitely.
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
                // Vulkan and keeps meaning it indefinitely, which is what makes it the kill switch the native
                // Vulkan design leans on: an A/B against the native implementation is one variable away.
                case "vulkan-native": case "vk-native":
                    backend = GpuBackendKind.VulkanNative; return true;
                case "gl": case "opengl": backend = GpuBackendKind.OpenGL; return true;
                default: backend = default; return false;
            }
        }

        /// <summary>
        /// The default backend for an OS family (macOS -> Metal, Windows -> D3D11, else Vulkan).
        /// <para>
        /// Windows deliberately still answers <see cref="GpuBackendKind.Direct3D11"/>, the Veldrid implementation,
        /// and keeps answering it until the native backend has passed all five rollout gates (decision I4 and
        /// section 14 of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>). Flipping the default is the
        /// LAST step of that program, not a side effect of the member existing: until then the native leg is
        /// exercised by naming it, through <c>KE_GRAPHICS_BACKEND</c> and its own CI matrix leg.
        /// </para>
        /// <para>
        /// Linux answers <see cref="GpuBackendKind.Vulkan"/> on exactly the same terms and for exactly the same
        /// reason (decision V-RO3). Worth stating separately because the two flips are not the same edit: this
        /// line is where a native Vulkan default would land, so flipping it changes the LINUX default while the
        /// Windows one stays where it is, and the two programs reach their gates independently.
        /// </para>
        /// </summary>
        public static GpuBackendKind ProbeOS(OSPlatformKind os) => os switch
        {
            OSPlatformKind.MacOS => GpuBackendKind.Metal,
            OSPlatformKind.Windows => GpuBackendKind.Direct3D11,
            OSPlatformKind.Linux => GpuBackendKind.Vulkan,
            _ => GpuBackendKind.Vulkan,
        };

        // The backends the engine can create a WINDOWED device on, in a stable presentation order. OpenGL is
        // deliberately absent: CreateForWindow has no windowed GL path (Silk would have to own the GL context),
        // so offering it to a player would be offering a choice that cannot boot.
        //
        // Direct3D11Native and VulkanNative are absent for a different reason, and stay absent until their
        // respective default flips (decisions I4 and V-RO3).
        // This list is what a game's graphics settings screen OFFERS, and a player picks an API, not an
        // implementation of one: two entries both reading "Direct3D 11" is a choice nobody outside this repo can
        // make, and two both reading "Vulkan" is the same choice again. The native legs are named explicitly
        // instead, through KE_GRAPHICS_BACKEND, until each becomes what its API's name means.
        static readonly GpuBackendKind[] _windowCandidates =
            { GpuBackendKind.Metal, GpuBackendKind.Vulkan, GpuBackendKind.Direct3D11 };

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
        /// Every backend this machine can actually run a windowed device on, in a stable order
        /// (Metal, Vulkan, Direct3D11). This is the list a game's graphics settings screen must offer: presenting
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
            _ => throw NotAVeldridBackend(kind),
        };

        static NotSupportedException NotAVeldridBackend(GpuBackendKind kind)
            => new($"{kind} is not a Veldrid backend, so it has no GraphicsBackend to map onto. It is created by "
                + "its registered provider instead (GpuBackendProviders), and every path that could reach here "
                + "checks GpuBackendProviders.RequiresProvider first. Reaching this means that check was skipped.");
    }
}
