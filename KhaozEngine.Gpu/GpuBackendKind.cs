namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The graphics backend the engine runs on. Mirrors the subset of Veldrid backends the 5.x stack targets.
    /// Selection is centralized in <see cref="GpuBackendSelector"/>; the active backend is exposed on
    /// <see cref="GpuDeviceContext.Backend"/>.
    /// </summary>
    public enum GpuBackendKind
    {
        /// <summary>Apple Metal (the only backend exercised on the current dev box).</summary>
        Metal,
        /// <summary>Vulkan (default on Linux).</summary>
        Vulkan,
        /// <summary>Direct3D 11 (default on Windows).</summary>
        Direct3D11,
        /// <summary>OpenGL.</summary>
        OpenGL,
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
