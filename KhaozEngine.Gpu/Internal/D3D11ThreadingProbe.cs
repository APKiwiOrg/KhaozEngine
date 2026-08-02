using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// Reads <c>D3D11_FEATURE_DATA_THREADING</c> off a live Direct3D11 device, wrapping the raw
    /// <c>ID3D11Device</c> pointer in Vortice's <c>ID3D11Device</c> (Vortice.Direct3D11 is already the D3D11
    /// binding Veldrid itself is built on, so this adds no new native surface, only a declared reference).
    /// <para>
    /// TWO entry points, one query. <see cref="TryQuery(GraphicsDevice, GpuBackendKind, out string)"/> reads the
    /// pointer Veldrid publishes on <c>BackendInfoD3D11.Device</c>, and
    /// <see cref="TryQuery(IntPtr, out string)"/> takes a pointer a caller already holds, which is how a device
    /// the engine created natively gets the same diagnostic line.
    /// </para>
    /// <para>
    /// The whole thing is diagnostics. It must never be able to break device creation, so every failure path
    /// degrades to "unknown" rather than throwing, and it is a hard no-op on any backend other than Direct3D11
    /// and on any OS other than Windows. <c>QueryWindows</c> is the only place that names a Vortice type, and both
    /// of its overloads are <see cref="MethodImplOptions.NoInlining"/> exactly so the JIT resolves those types
    /// when that body is first compiled and not before. Since the <c>IsApplicable</c> guards gate the only calls,
    /// the Vortice assembly is never loaded on macOS or Linux.
    /// </para>
    /// </summary>
    internal static class D3D11ThreadingProbe
    {
        // The two failure strings both entry points hand back, in one place: the Veldrid entry and the raw-pointer
        // entry report the same condition the same way, or a reader comparing two session logs is comparing the
        // wording of two probes rather than the answers of two drivers.
        const string NoAnswer = "the Direct3D11 device did not answer the threading feature query";

        static string Threw(Exception ex)
            => $"the Direct3D11 threading query threw {ex.GetType().Name}: {ex.Message}";

        /// <summary>
        /// Read the driver threading caps for <paramref name="device"/>, or null when there is nothing to read.
        /// <paramref name="failure"/> is non-null only when the query was attempted and did not produce an answer,
        /// so the caller can log the reason. Not applicable (wrong backend, wrong OS) returns null with a null
        /// failure, since that is not a fault.
        /// </summary>
        internal static GpuThreadingCaps? TryQuery(GraphicsDevice device, GpuBackendKind backend, out string? failure)
        {
            failure = null;
            // OperatingSystem.IsWindows rather than RuntimeInformation.IsOSPlatform: same answer, and it is the
            // form the platform-compatibility analyzer understands, which is what lets QueryWindows carry
            // [SupportedOSPlatform] without the call site warning. The whole guard returns BEFORE anything reads
            // the device, so an inapplicable call cannot fault on it either.
            if (!IsApplicable(backend, OperatingSystem.IsWindows())) return null;

            try
            {
                GpuThreadingCaps? caps = QueryWindows(device);
                if (caps is null) failure = NoAnswer;
                return caps;
            }
            catch (Exception ex)
            {
                // Deliberately broad. A diagnostic that takes down device creation is far worse than the problem
                // it was added to diagnose, and everything below this point is interop against a driver.
                failure = Threw(ex);
                return null;
            }
        }

        /// <summary>
        /// The same query against a RAW <c>ID3D11Device</c> pointer, for a device the engine created itself and
        /// that therefore has no Veldrid <see cref="GraphicsDevice"/> to read <c>BackendInfoD3D11.Device</c> off.
        /// A native backend owns its own <c>ID3D11Device</c> and can hand the pointer straight in, so the driver
        /// threading line and the telemetry fields it feeds survive the move off Veldrid rather than going dark on
        /// exactly the backend they were written to diagnose.
        /// <para>
        /// There is no backend argument, because a caller holding an <c>ID3D11Device</c> has already answered
        /// that question. <see cref="IntPtr.Zero"/> is "nothing to ask", the same not-a-fault answer the wrong
        /// backend gets from the overload above: this is diagnostics, and it must never be the thing that fails a
        /// device creation. The caller keeps ownership of the pointer, whose refcount is left exactly as found.
        /// </para>
        /// </summary>
        internal static GpuThreadingCaps? TryQuery(IntPtr d3d11Device, out string? failure)
        {
            failure = null;
            if (!IsApplicable(d3d11Device, OperatingSystem.IsWindows())) return null;

            try
            {
                GpuThreadingCaps? caps = QueryWindows(d3d11Device);
                if (caps is null) failure = NoAnswer;
                return caps;
            }
            catch (Exception ex)
            {
                failure = Threw(ex);
                return null;
            }
        }

        /// <summary>
        /// Whether the probe has anything to ask: Direct3D11, on Windows. Pure and separate so the no-op
        /// guarantee off Windows and off D3D11 is an ASSERTED property rather than a claim in a comment. It is
        /// also what keeps the Vortice assembly unloaded on macOS and Linux: this returning false is the reason
        /// <c>QueryWindows</c> never gets JIT-compiled there.
        /// <para>
        /// "Direct3D11" here means EITHER implementation
        /// (<see cref="GpuBackendKinds.IsDirect3D11"/>), because what is being asked about is the DRIVER, and the
        /// driver is the same one whichever implementation drove it. This is the source of the session header's
        /// <c>driverCommandLists</c> and <c>driverConcurrentCreates</c>, so an equality check against the Veldrid
        /// kind alone would drop both fields on the native leg, silently, with the session still looking healthy.
        /// </para>
        /// <para>
        /// <see cref="SupportedOSPlatformGuardAttribute"/> is what lets the ONE guard serve both readers: the
        /// platform-compatibility analyzer treats a false return as ruling Windows out, so
        /// <c>QueryWindows</c> keeps its <c>[SupportedOSPlatform("windows")]</c> contract without the call
        /// site needing a second, drift-prone copy of the same check.
        /// </para>
        /// </summary>
        [SupportedOSPlatformGuard("windows")]
        internal static bool IsApplicable(GpuBackendKind backend, bool isWindows)
            => isWindows && backend.IsDirect3D11();

        /// <summary>
        /// The raw-pointer entry's half of the same guarantee: Windows, and a device actually supplied. Pure and
        /// separate for the same reason the backend overload is, and it carries the same
        /// <see cref="SupportedOSPlatformGuardAttribute"/> so the one check serves the analyzer too.
        /// </summary>
        [SupportedOSPlatformGuard("windows")]
        internal static bool IsApplicable(IntPtr d3d11Device, bool isWindows)
            => isWindows && d3d11Device != IntPtr.Zero;

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static GpuThreadingCaps? QueryWindows(GraphicsDevice device)
        {
            if (!device.GetD3D11Info(out BackendInfoD3D11 info)) return null;
            return QueryWindows(info.Device);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static GpuThreadingCaps? QueryWindows(IntPtr d3d11Device)
        {
            if (d3d11Device == IntPtr.Zero) return null;

            // Non-owning wrapper over a pointer that belongs to the caller. SharpGen's ComObject
            // constructor does NOT AddRef, but its Dispose DOES Release, so take a reference here to keep the
            // pair balanced and leave the device's refcount exactly as it was found.
            var d3d = new Vortice.Direct3D11.ID3D11Device(d3d11Device);
            d3d.AddRef();
            try
            {
                // Vortice takes the payload by ref (not out), so it starts zeroed and is only trusted when the
                // call reports success. A failed HRESULT would otherwise read as "both capabilities false",
                // which is the exact bad-driver signature this probe exists to report.
                var data = default(Vortice.Direct3D11.FeatureDataThreading);
                if (!d3d.CheckFeatureSupport(Vortice.Direct3D11.Feature.Threading, ref data)) return null;
                return new GpuThreadingCaps(data.DriverCommandLists, data.DriverConcurrentCreates);
            }
            finally
            {
                d3d.Dispose();
            }
        }
    }
}
