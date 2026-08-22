namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// The one decision behind "the backend nobody pinned is one this process cannot build", which only became
    /// reachable in 17.40.0 when <see cref="GpuBackendSelector.ProbeOS"/> started answering a provider-backed
    /// kind on every platform.
    /// <para>
    /// The native backend packages are in no umbrella and the engine cannot reference them, so a game gets a
    /// native backend by adding a <c>PackageReference</c> and calling its <c>Register()</c>. A game that
    /// repins the engine and does neither now has an OS default nothing in its process can create. Without
    /// this, <c>GpuBackendProviders.Require</c> throws and that client does not boot, which is the exact
    /// opposite of the incumbents remaining an opt-out for one release.
    /// </para>
    /// <para>
    /// SPLITTING DECISION I2, NOT WAIVING IT. I2 makes a missing provider throw so a soak session cannot
    /// quietly measure the incumbent and report it as the native backend. A soak session PINS the backend
    /// through <c>KE_GRAPHICS_BACKEND</c>, as do all five cross-platform GPU legs, and a pinned backend still
    /// throws. What falls back is everything else, where "you have not referenced the package" is a fact about
    /// the app's dependencies rather than a mistake in a request the caller made.
    /// </para>
    /// <para>
    /// A STORED PREFERENCE FALLS BACK TOO, and that is the 17.40.0 correction rather than the original rule.
    /// <c>SupportedBackends()</c> offers native rows now, so a player can store a native kind, and a later
    /// build that dropped the package or the <c>Register()</c> line would throw at boot with the setting that
    /// caused it unreachable from inside the game. It falls back reporting
    /// <see cref="GpuBackendSource.FallbackAfterFailure"/>, the signal a game clears a stored preference on,
    /// while a DEFAULTED backend reports <see cref="GpuBackendSource.DefaultProviderMissing"/>, which no game
    /// should clear anything for. <see cref="Report"/> is where that split is made, once, for both paths.
    /// </para>
    /// <para>
    /// It is a type of its own rather than four lines inside <see cref="GpuDeviceContext"/> because the
    /// windowed path and the headless path both take it, and two copies of a rule this subtle would drift into
    /// one path throwing where the other falls back.
    /// </para>
    /// </summary>
    internal static class UnregisteredNativeDefault
    {
        /// <summary>
        /// The reason the fallback WARN carries. Worded so a reader knows whose line is missing and where it
        /// goes, since the answer is in the game rather than anywhere in the engine.
        /// </summary>
        internal const string Reason =
            "no provider is registered for it in this process. The native backend packages "
            + "(KhaozEngine.Gpu.Metal / KhaozEngine.Gpu.D3D11 / KhaozEngine.Gpu.Vulkan) are referenced and "
            + "registered by the game, not by the engine";

        /// <summary>
        /// Whether <paramref name="backend"/> is a provider-backed kind that the ENVIRONMENT did not pin and
        /// that has no registered provider, so creation must fall back to the platform's incumbent instead of
        /// throwing.
        /// </summary>
        internal static bool Applies(GpuBackendKind backend, bool pinnedByEnvironment)
            => !pinnedByEnvironment
                && GpuBackendProviders.RequiresProvider(backend)
                && !GpuBackendProviders.IsRegistered(backend);

        /// <summary>
        /// How the fallback is REPORTED, which depends on who asked for the backend and not on what went
        /// missing. A stored preference gets <see cref="GpuBackendSource.FallbackAfterFailure"/>, because
        /// clearing that preference is the action the game has to take. Anything else was a DEFAULT nobody
        /// chose, and gets <see cref="GpuBackendSource.DefaultProviderMissing"/>, which is addressed to the
        /// developer and asks a game to clear nothing.
        /// </summary>
        internal static GpuBackendSelection Report(GpuBackendSelection selection, GpuBackendKind incumbent)
            => selection.Source == GpuBackendSource.UserPreference
                ? GpuBackendSelector.AfterFallback(selection, incumbent)
                : GpuBackendSelector.AfterMissingDefaultProvider(selection, incumbent);

        /// <summary>
        /// The WARN a DEFAULTED backend gets, aimed at the developer rather than the player. The one the
        /// player-facing <c>WarnFallback</c> prints tells a reader to clear a stored graphics choice, which
        /// here would be advice about a setting nobody touched. Built as a string so a test reads exactly what
        /// a session log does.
        /// </summary>
        internal static string Warning(GpuBackendKind backend, GpuBackendKind incumbent)
            => $"{backend} is this platform's default graphics backend, but {Reason}. Falling back to "
                + $"{incumbent}. Add the package reference and call its Register() once at startup "
                + "(KhaozEngineMetal.Register(), KhaozEngineD3D11.Register() or KhaozEngineVulkan.Register()) "
                + "to run on the default. This is a gap in the app's wiring rather than a machine that cannot "
                + "run the backend, and no stored graphics choice caused it, so there is nothing to clear.";
    }
}
