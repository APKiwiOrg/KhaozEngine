using System;
using System.Runtime.InteropServices;
using Veldrid;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Centralizes graphics-backend selection. <see cref="Select()"/> reads the <c>KE_GRAPHICS_BACKEND</c>
    /// environment variable as an override (values <c>metal</c>/<c>vulkan</c>/<c>d3d11</c>/<c>gl</c>,
    /// case-insensitive) and otherwise probes the OS (macOS -> Metal, Windows -> Direct3D11, Linux -> Vulkan,
    /// with Vulkan as the catch-all default). The pure overload <see cref="Select(string?, OSPlatformKind)"/>
    /// makes the logic headless-testable without touching the real environment.
    /// </summary>
    public static class GpuBackendSelector
    {
        /// <summary>The env var that overrides the OS probe.</summary>
        public const string EnvVarName = "KE_GRAPHICS_BACKEND";

        /// <summary>
        /// Resolve the backend from the live environment: <c>KE_GRAPHICS_BACKEND</c> override if present and
        /// valid, else the OS probe.
        /// </summary>
        public static GpuBackendKind Select()
            => Select(Environment.GetEnvironmentVariable(EnvVarName), DetectOS());

        /// <summary>
        /// Pure backend-selection logic. If <paramref name="envOverride"/> is a recognized backend name
        /// (case-insensitive; <c>metal</c>/<c>vulkan</c>/<c>d3d11</c>/<c>gl</c>) it wins; otherwise (null,
        /// empty, or unrecognized) the choice falls through to the <paramref name="os"/> probe.
        /// </summary>
        public static GpuBackendKind Select(string? envOverride, OSPlatformKind os)
        {
            if (TryParseBackend(envOverride, out GpuBackendKind overridden))
                return overridden;
            return ProbeOS(os);
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
