using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The engine's native Direct3D 11 backend as the GPU seam sees it. Registered under
    /// <see cref="GpuBackendKind.Direct3D11Native"/> by <see cref="KhaozEngineD3D11.Register"/> and consumed
    /// only through <see cref="IGpuBackendProvider"/>, so nothing outside this package ever names a
    /// Direct3D type.
    /// <para>
    /// CREATION IS NOT BUILT YET. This is the package skeleton (work-breakdown row 4 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>), which exists so the registration seam, the
    /// architecture rows and the machine-capability probe land before the device does. The recorder, the
    /// resources, the shader path and the swapchain are rows 5 onward, and until they land both creation entry
    /// points throw a message that says so by name.
    /// </para>
    /// <para>
    /// The probe is REAL from this row, and the split is deliberate rather than an accident of ordering.
    /// <see cref="IsSupported"/> answers a question about the MACHINE, which is knowable now and is what a
    /// settings screen and the fallback path consume. Whether this package can build a device yet is a different
    /// fact entirely, and folding it into the probe would make the probe answer false for a reason that has
    /// nothing to do with the hardware, then quietly start answering true later for no observable change.
    /// </para>
    /// </summary>
    internal sealed class D3D11BackendProvider : IGpuBackendProvider
    {
        static readonly ILogger log = Log.For<D3D11BackendProvider>();

        /// <inheritdoc/>
        public bool IsSupported()
        {
            // The platform guard first, and it is the whole answer off Windows: no Direct3D type is named on this
            // path, so the Vortice assembly is never loaded on macOS or Linux.
            if (!KhaozEngineD3D11.IsPlatformSupported) return false;

            try
            {
                string? missing = D3D11FeatureProbe.MissingRequirementWindows();
                if (missing is null) return true;

                log.Info($"The native Direct3D 11 backend is not available on this machine: {missing}.");
                return false;
            }
            catch (Exception ex)
            {
                // Deliberately broad, and the contract requires it: this probe must NEVER throw, because a probe
                // that blows up and a probe that answers no are the same answer to the settings screen and to the
                // fallback that consume it. Everything below is interop against a driver, so the failure can be
                // anything from an HRESULT wrapper to a DllNotFoundException out of the P/Invoke layer.
                log.Info("The native Direct3D 11 support probe could not answer, so this machine is reported as "
                    + $"unsupported. It threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <inheritdoc/>
        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request) => throw NotBuiltYet("windowed");

        /// <inheritdoc/>
        public GpuProviderDevice CreateHeadless() => throw NotBuiltYet("headless");

        // Named rather than a bare NotImplementedException, because this exception is what a Windows tester who
        // set KE_GRAPHICS_BACKEND=d3d11-native actually sees: the creation path catches it, WARNs with this
        // message and falls back to the incumbent, which is honest only if the message says the backend is
        // unfinished rather than leaving the reader to conclude their machine is at fault.
        static NotSupportedException NotBuiltYet(string path)
            => new($"The native Direct3D 11 backend cannot create a {path} device yet. This package currently "
                + "carries only the registration seam and the machine-capability probe. Device creation (the "
                + "command recorder, resources, the shader path and the swapchain) is still being built, so "
                + "KhaozEngineD3D11.Register() makes the backend selectable and reportable, not yet runnable. "
                + "Select GpuBackendKind.Direct3D11 for a working Direct3D 11 device.");
    }
}
