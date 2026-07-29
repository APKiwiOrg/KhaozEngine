using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// Reads <c>D3D11_FEATURE_DATA_THREADING</c> off a live Veldrid Direct3D11 device, via the raw
    /// <c>ID3D11Device</c> pointer Veldrid publishes on <c>BackendInfoD3D11.Device</c> wrapped in Vortice's
    /// <c>ID3D11Device</c> (Vortice.Direct3D11 is already the D3D11 binding Veldrid itself is built on, so this
    /// adds no new native surface, only a declared reference).
    /// <para>
    /// The whole thing is diagnostics. It must never be able to break device creation, so every failure path
    /// degrades to "unknown" rather than throwing, and it is a hard no-op on any backend other than Direct3D11
    /// and on any OS other than Windows. <see cref="QueryWindows"/> is the only method that names a Vortice type,
    /// and it is <see cref="MethodImplOptions.NoInlining"/> exactly so the JIT resolves those types when that body
    /// is first compiled and not before. Since the guards in <see cref="TryQuery"/> gate the only call, the
    /// Vortice assembly is never loaded on macOS or Linux.
    /// </para>
    /// </summary>
    internal static class D3D11ThreadingProbe
    {
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
                if (caps is null) failure = "the Direct3D11 device did not answer the threading feature query";
                return caps;
            }
            catch (Exception ex)
            {
                // Deliberately broad. A diagnostic that takes down device creation is far worse than the problem
                // it was added to diagnose, and everything below this point is interop against a driver.
                failure = $"the Direct3D11 threading query threw {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Whether the probe has anything to ask: Direct3D11, on Windows. Pure and separate so the no-op
        /// guarantee off Windows and off D3D11 is an ASSERTED property rather than a claim in a comment. It is
        /// also what keeps the Vortice assembly unloaded on macOS and Linux: this returning false is the reason
        /// <see cref="QueryWindows"/> never gets JIT-compiled there.
        /// <para>
        /// <see cref="SupportedOSPlatformGuardAttribute"/> is what lets the ONE guard serve both readers: the
        /// platform-compatibility analyzer treats a false return as ruling Windows out, so
        /// <see cref="QueryWindows"/> keeps its <c>[SupportedOSPlatform("windows")]</c> contract without the call
        /// site needing a second, drift-prone copy of the same check.
        /// </para>
        /// </summary>
        [SupportedOSPlatformGuard("windows")]
        internal static bool IsApplicable(GpuBackendKind backend, bool isWindows)
            => isWindows && backend == GpuBackendKind.Direct3D11;

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static GpuThreadingCaps? QueryWindows(GraphicsDevice device)
        {
            if (!device.GetD3D11Info(out BackendInfoD3D11 info)) return null;
            if (info.Device == IntPtr.Zero) return null;

            // Non-owning wrapper over a pointer that belongs to Veldrid's GraphicsDevice. SharpGen's ComObject
            // constructor does NOT AddRef, but its Dispose DOES Release, so take a reference here to keep the
            // pair balanced and leave the device's refcount exactly as it was found.
            var d3d = new Vortice.Direct3D11.ID3D11Device(info.Device);
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
