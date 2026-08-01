using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The one-call bridge that fills a <see cref="TelemetrySessionInfo"/>'s GPU fields from what the engine
    /// already resolved at device creation, so a telemetry recording names the backend, its provenance, the
    /// adapter, the injected overlays, and the Direct3D11 driver threading caps without the game re-deriving
    /// any of it.
    /// <para>
    /// It lives HERE rather than in <c>KhaozEngine.Diagnostics</c> because that package sits under this one
    /// (<c>KhaozEngine.Gpu</c> references it, not the reverse), so the mapping from these enums onto the
    /// header's plain strings belongs in the package that owns the enums.
    /// </para>
    /// </summary>
    public static class GpuTelemetry
    {
        /// <summary>
        /// Fill <paramref name="info"/>'s GPU fields from a live device. The one-liner for a consumer holding a
        /// <see cref="GpuDeviceContext"/>.
        /// </summary>
        /// <returns><paramref name="info"/>, so calls chain.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="info"/> or <paramref name="device"/> is null.</exception>
        public static TelemetrySessionInfo WithGpu(this TelemetrySessionInfo info, GpuDeviceContext device)
        {
            ArgumentNullException.ThrowIfNull(device);
            return info.WithGpu(device.Selection, device.AdapterDescription, device.InjectedModules, device.ThreadingCaps);
        }

        /// <summary>
        /// Fill <paramref name="info"/>'s GPU fields from the individual values. This is the overload a consumer
        /// holding an <c>AppWindow</c> uses, since the window surfaces the same four facts without handing out
        /// its device: <c>info.WithGpu(window.BackendSelection, window.AdapterDescription, window.InjectedModules,
        /// window.ThreadingCaps)</c>.
        /// </summary>
        /// <param name="info">The header options to fill.</param>
        /// <param name="selection">The backend that ran and where that choice came from.</param>
        /// <param name="adapterDescription">The adapter name, or blank when the backend reports none.</param>
        /// <param name="injectedModules">
        /// The injected-overlay scan result. Null (never scanned) and empty (scanned, clean) are opposite facts
        /// and are carried through as such.
        /// </param>
        /// <param name="threadingCaps">The Direct3D11 driver threading caps, or null on every other backend.</param>
        /// <returns><paramref name="info"/>, so calls chain.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="info"/> is null.</exception>
        public static TelemetrySessionInfo WithGpu(
            this TelemetrySessionInfo info,
            GpuBackendSelection selection,
            string? adapterDescription,
            IReadOnlyList<string>? injectedModules,
            GpuThreadingCaps? threadingCaps)
        {
            ArgumentNullException.ThrowIfNull(info);

            // Enum NAMES, not the numbers: the header is read by a person triaging a capture, and the members
            // are append-only by contract, so the name is as stable as the number and says what it means.
            info.GpuBackend = selection.Backend.ToString();
            info.GpuBackendSource = selection.Source.ToString();
            info.AdapterDescription = adapterDescription;
            info.InjectedModules = injectedModules;
            info.DriverCommandLists = threadingCaps?.DriverCommandLists;
            info.DriverConcurrentCreates = threadingCaps?.DriverConcurrentCreates;
            return info;
        }
    }
}
