namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// The one decision behind "the OS default is a backend this process cannot build", which only became
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
    /// quietly measure the incumbent and report it as the native backend. A session that asks NAMES the
    /// backend, through <c>KE_GRAPHICS_BACKEND</c> or a stored preference, and a named backend still throws.
    /// What falls back is only the case where nobody asked for anything, where "you have not referenced the
    /// package" is a fact about the app's dependencies rather than a mistake in a request it never made.
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
        /// Whether <paramref name="backend"/> is a provider-backed kind that nobody NAMED and that has no
        /// registered provider, so creation must fall back to the platform's incumbent instead of throwing.
        /// </summary>
        internal static bool Applies(GpuBackendKind backend, bool wasNamed)
            => !wasNamed
                && GpuBackendProviders.RequiresProvider(backend)
                && !GpuBackendProviders.IsRegistered(backend);
    }
}
