using System;
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
    public enum GpuBackendSource
    {
        /// <summary>No override was present, so the backend came from the OS probe.</summary>
        OsProbe,

        /// <summary><c>KE_GRAPHICS_BACKEND</c> was set to a recognized backend, and it was honoured.</summary>
        EnvironmentOverride,

        /// <summary>
        /// <c>KE_GRAPHICS_BACKEND</c> was set to something unparseable, so the OS probe decided instead. The raw
        /// value is kept on <see cref="GpuBackendSelection.RequestedOverride"/> for the diagnostic.
        /// </summary>
        UnrecognizedOverride,
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
    public readonly record struct GpuBackendSelection(
        GpuBackendKind Backend,
        GpuBackendSource Source,
        string? RequestedOverride);

    /// <summary>
    /// Centralizes graphics-backend selection. <see cref="Select()"/> reads the <c>KE_GRAPHICS_BACKEND</c>
    /// environment variable as an override (values <c>metal</c>/<c>vulkan</c>/<c>d3d11</c>/<c>gl</c>,
    /// case-insensitive) and otherwise probes the OS (macOS -> Metal, Windows -> Direct3D11, Linux -> Vulkan,
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
        /// (case-insensitive; <c>metal</c>/<c>vulkan</c>/<c>d3d11</c>/<c>gl</c>) it wins; otherwise (null,
        /// empty, or unrecognized) the choice falls through to the <paramref name="os"/> probe.
        /// </summary>
        public static GpuBackendKind Select(string? envOverride, OSPlatformKind os)
            => Resolve(envOverride, os).Backend;

        /// <summary>
        /// The same decision <see cref="Select()"/> makes, read from the live environment, but reported with its
        /// provenance so callers can log it and spot a misconfigured override.
        /// </summary>
        public static GpuBackendSelection Resolve()
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), DetectOS());

        /// <summary>
        /// Pure, headless-testable backend selection WITH provenance, and the one decision path
        /// <see cref="Select(string?, OSPlatformKind)"/> is built on so the two can never drift. A null, empty, or
        /// whitespace-only <paramref name="envOverride"/> counts as no override at all
        /// (<see cref="GpuBackendSource.OsProbe"/>, no raw value recorded). A non-blank value that fails to parse
        /// is <see cref="GpuBackendSource.UnrecognizedOverride"/>: the <paramref name="os"/> probe still decides
        /// the backend, but the raw value is preserved so the caller can say what was asked for.
        /// </summary>
        public static GpuBackendSelection Resolve(string? envOverride, OSPlatformKind os)
        {
            if (string.IsNullOrWhiteSpace(envOverride))
                return new GpuBackendSelection(ProbeOS(os), GpuBackendSource.OsProbe, null);
            if (TryParseBackend(envOverride, out GpuBackendKind overridden))
                return new GpuBackendSelection(overridden, GpuBackendSource.EnvironmentOverride, envOverride);
            return new GpuBackendSelection(ProbeOS(os), GpuBackendSource.UnrecognizedOverride, envOverride);
        }

        /// <summary>Map a <c>KE_GRAPHICS_BACKEND</c> value to a backend. Case-insensitive; trims whitespace.</summary>
        public static bool TryParseBackend(string? value, out GpuBackendKind backend)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "metal": backend = GpuBackendKind.Metal; return true;
                case "vulkan": backend = GpuBackendKind.Vulkan; return true;
                case "d3d11": case "direct3d11": backend = GpuBackendKind.Direct3D11; return true;
                case "gl": case "opengl": backend = GpuBackendKind.OpenGL; return true;
                default: backend = default; return false;
            }
        }

        /// <summary>The default backend for an OS family (macOS -> Metal, Windows -> D3D11, else Vulkan).</summary>
        public static GpuBackendKind ProbeOS(OSPlatformKind os) => os switch
        {
            OSPlatformKind.MacOS => GpuBackendKind.Metal,
            OSPlatformKind.Windows => GpuBackendKind.Direct3D11,
            OSPlatformKind.Linux => GpuBackendKind.Vulkan,
            _ => GpuBackendKind.Vulkan,
        };

        /// <summary>Detect the running OS family via <see cref="RuntimeInformation"/>.</summary>
        public static OSPlatformKind DetectOS()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return OSPlatformKind.MacOS;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return OSPlatformKind.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return OSPlatformKind.Linux;
            return OSPlatformKind.Unknown;
        }

        /// <summary>Map an engine <see cref="GpuBackendKind"/> to the Veldrid backend (internal: Veldrid stays here).</summary>
        internal static GraphicsBackend ToVeldrid(GpuBackendKind kind) => kind switch
        {
            GpuBackendKind.Metal => GraphicsBackend.Metal,
            GpuBackendKind.Vulkan => GraphicsBackend.Vulkan,
            GpuBackendKind.Direct3D11 => GraphicsBackend.Direct3D11,
            GpuBackendKind.OpenGL => GraphicsBackend.OpenGL,
            _ => GraphicsBackend.Metal,
        };
    }
}
