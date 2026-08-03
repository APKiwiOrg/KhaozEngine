using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The one-call bridge that fills a <see cref="TelemetrySessionInfo"/>'s GPU fields from what the engine
    /// already resolved at device creation, so a telemetry recording names the backend, its provenance, what was
    /// asked for when that differs, the adapter, the injected overlays, and the Direct3D11 driver threading caps
    /// without the game re-deriving any of it.
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
            return info.WithGpu(device.Selection, device.AdapterDescription, device.InjectedModules,
                device.ThreadingCaps, device.Diagnostics);
        }

        /// <summary>
        /// Fill <paramref name="info"/>'s GPU fields from the individual values. This is the overload a consumer
        /// holding an <c>AppWindow</c> uses, since the window surfaces the same four facts without handing out
        /// its device: <c>info.WithGpu(window.BackendSelection, window.AdapterDescription, window.InjectedModules,
        /// window.ThreadingCaps)</c>.
        /// </summary>
        /// <param name="info">The header options to fill.</param>
        /// <param name="selection">
        /// The backend that ran, where that choice came from, and what was asked for. All four members are
        /// carried into the header, so a fallback capture says what failed and not only that something did.
        /// </param>
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

            // What was ASKED for, both halves of it. Without these a fallback capture is strictly less
            // informative than the "fallback, {RequestedBackend} failed" line GpuDeviceContext already logs
            // beside it, and on a UserPreference fallback the request (the player's own in-game graphics
            // setting) is recoverable nowhere else in the capture at all. RequestedOverride is the other half:
            // it is the whole diagnostic behind an UnrecognizedOverride, and it is carried untouched.
            info.GpuRequestedBackend = selection.RequestedBackend?.ToString();
            info.GpuRequestedOverride = selection.RequestedOverride;

            info.AdapterDescription = adapterDescription;
            info.InjectedModules = injectedModules;
            info.DriverCommandLists = threadingCaps?.DriverCommandLists;
            info.DriverConcurrentCreates = threadingCaps?.DriverConcurrentCreates;
            return info;
        }

        /// <summary>
        /// The same mapping PLUS the two live device facts of <see cref="GpuDeviceDiagnostics"/>: whether the
        /// session ran on a software rasterizer, and why the device was lost if it was. This is what the
        /// <see cref="GpuDeviceContext"/> overload calls.
        /// <para>
        /// A SEPARATE OVERLOAD rather than an optional parameter on the one above, so an already-compiled consumer
        /// keeps binding to the method it was compiled against. The two are otherwise identical, and a caller with
        /// no diagnostics to supply should keep using the shorter one rather than passing <c>default</c>: the
        /// shorter one leaves both header fields null, which is the honest "nobody answered", and passing a
        /// default-constructed value says exactly the same thing by a longer route.
        /// </para>
        /// </summary>
        /// <param name="info">The header options to fill.</param>
        /// <param name="selection">The backend that ran, where that choice came from, and what was asked for.</param>
        /// <param name="adapterDescription">The adapter name, or blank when the backend reports none.</param>
        /// <param name="injectedModules">The injected-overlay scan result. Null and empty are opposite facts.</param>
        /// <param name="threadingCaps">The Direct3D11 driver threading caps, or null on every other backend.</param>
        /// <param name="diagnostics">
        /// Read LIVE off the device at the moment the header is written. A value captured earlier in the session
        /// would always report a device that had not been lost yet, which is precisely the case the field exists
        /// for.
        /// </param>
        /// <returns><paramref name="info"/>, so calls chain.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="info"/> is null.</exception>
        public static TelemetrySessionInfo WithGpu(
            this TelemetrySessionInfo info,
            GpuBackendSelection selection,
            string? adapterDescription,
            IReadOnlyList<string>? injectedModules,
            GpuThreadingCaps? threadingCaps,
            GpuDeviceDiagnostics diagnostics)
        {
            info.WithGpu(selection, adapterDescription, injectedModules, threadingCaps);
            info.SoftwareAdapter = diagnostics.SoftwareAdapter;
            info.DeviceLossReason = diagnostics.DeviceLossReason;
            return info;
        }
    }
}
