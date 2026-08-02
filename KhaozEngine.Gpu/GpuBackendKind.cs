namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The graphics backend the engine runs on. Selection is centralized in <see cref="GpuBackendSelector"/>, and
    /// the active backend is exposed on <see cref="GpuDeviceContext.Backend"/>. Four of the members name a Veldrid
    /// backend the engine creates itself. <see cref="Direct3D11Native"/> names an engine-owned implementation that
    /// arrives through <see cref="GpuBackendProviders"/> instead.
    /// </summary>
    /// <remarks>
    /// Members are APPEND-ONLY and pinned to explicit values, the same contract
    /// <see cref="GpuBackendSource"/> carries. A consuming game persists the player's chosen backend as a stored
    /// preference and hands it back here as a <see cref="GpuBackendKind"/>, so renumbering would silently repoint
    /// every saved graphics setting at a different backend. Never reorder, renumber, or remove one.
    /// <para>
    /// Appending IS supported, and section 4.3 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c> is the audit an append has to pass. The enum
    /// itself is the safe part. What is not safe is every place that switches on it, compares against it, or
    /// derives a string from it: three of those degrade a new backend SILENTLY rather than failing, and the worst
    /// of them does not throw at all (a discard arm that asks Veldrid for a Metal device). Walk the table.
    /// </para>
    /// </remarks>
    public enum GpuBackendKind
    {
        /// <summary>Apple Metal, through Veldrid (the default on macOS).</summary>
        Metal = 0,
        /// <summary>Vulkan, through Veldrid (the default on Linux).</summary>
        Vulkan = 1,
        /// <summary>Direct3D 11, through Veldrid (the default on Windows).</summary>
        Direct3D11 = 2,
        /// <summary>OpenGL, through Veldrid.</summary>
        OpenGL = 3,

        /// <summary>
        /// Direct3D 11 through the engine's OWN native backend (<c>KhaozEngine.Gpu.D3D11</c>) rather than through
        /// Veldrid. Selected by name (<c>KE_GRAPHICS_BACKEND=d3d11-native</c>) and created by the
        /// <see cref="IGpuBackendProvider"/> that package registers, never by this one: it is a separate member
        /// precisely so a session log, a telemetry header and a frame time are attributed to the implementation
        /// that actually ran. It renders the SAME images as <see cref="Direct3D11"/>, so it shares that backend's
        /// golden family rather than owning one.
        /// </summary>
        Direct3D11Native = 4,
    }

    /// <summary>
    /// Predicates over <see cref="GpuBackendKind"/> that more than one site needs. They live here rather than being
    /// spelled out at each site so the answer cannot drift: <see cref="IsDirect3D11"/> in particular is read by the
    /// driver-threading probe and by the log line that reports what the probe found, and a copy that disagreed
    /// would produce a session log claiming an answer nobody asked for.
    /// </summary>
    public static class GpuBackendKinds
    {
        /// <summary>
        /// Whether <paramref name="kind"/> is Direct3D 11 through EITHER implementation. This is the right question
        /// for anything that talks to the D3D11 API or reports on the D3D11 driver (the
        /// <c>D3D11_FEATURE_DATA_THREADING</c> probe and its log line), because the driver underneath is the same
        /// one whichever implementation drove it. It is the WRONG question for anything that maps a kind onto a
        /// Veldrid backend or creates a device, since only <see cref="GpuBackendKind.Direct3D11"/> is Veldrid's.
        /// </summary>
        public static bool IsDirect3D11(this GpuBackendKind kind)
            => kind is GpuBackendKind.Direct3D11 or GpuBackendKind.Direct3D11Native;
    }

    /// <summary>
    /// OS family used by <see cref="GpuBackendSelector"/>'s default probe. A tiny engine enum (rather than
    /// touching <c>RuntimeInformation</c> directly) so the selection logic is headless-testable: a test can
    /// drive <see cref="GpuBackendSelector.Select(string?, OSPlatformKind)"/> with any OS without mocking the
    /// real environment.
    /// </summary>
    public enum OSPlatformKind
    {
        /// <summary>An OS the probe does not specially recognize (falls back to the default backend).</summary>
        Unknown,
        /// <summary>macOS / OSX.</summary>
        MacOS,
        /// <summary>Windows.</summary>
        Windows,
        /// <summary>Linux.</summary>
        Linux,
    }
}
