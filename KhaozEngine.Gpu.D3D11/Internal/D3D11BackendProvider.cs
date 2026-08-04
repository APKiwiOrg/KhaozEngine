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
    /// CREATION IS REAL, and this is where it starts. Both entry points build a <see cref="D3D11GpuDevice"/>,
    /// which is the whole backend joined up: the adapter choice, the device and its versioned context, the one
    /// state and emitter context, the fences, the ring, the factory, the swapchain, the capability read and the
    /// debug-layer pump. Nothing in the engine SELECTS this backend by default: it is reached by
    /// <c>KE_GRAPHICS_BACKEND</c> or by an explicit request, and the default flip is gated by the five rollout
    /// gates of https://github.com/APKiwiOrg/KhaozEngine/issues/460.
    /// </para>
    /// <para>
    /// THE PLATFORM GUARD IS THE FIRST THING BOTH ENTRY POINTS DO, and it is what keeps the Vortice assembly off
    /// the load path on macOS and Linux: the bodies that name a Direct3D type are non-inlined behind it, so the
    /// JIT never compiles one on a machine that has no Direct3D. Off Windows this is a
    /// <see cref="PlatformNotSupportedException"/> naming the platform rather than the old "not built yet"
    /// message, which is now false everywhere.
    /// </para>
    /// <para>
    /// The probe stays a SEPARATE question from creation, and the split is deliberate rather than an accident of
    /// ordering. <see cref="IsSupported"/> answers a question about the MACHINE, which is what a settings screen
    /// and the fallback path consume. Whether this package can build a device is a different fact entirely, and
    /// folding them together would make the probe answer false for a reason that has nothing to do with the
    /// hardware.
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

        /// <summary>
        /// Create the windowed device and its swapchain. The window handle must be a Win32 HWND, which is the
        /// only kind a Direct3D swapchain can present into: a handle from any other windowing platform is a
        /// caller error rather than a machine limitation, and it is refused by name here instead of reaching
        /// DXGI as an opaque pointer that fails with <c>E_INVALIDARG</c>.
        /// </summary>
        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request)
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw NotOnThisPlatform("windowed");

            if (request.Window.Kind != GpuWindowKind.Win32)
            {
                throw new ArgumentException(
                    $"The native Direct3D 11 backend was handed a {request.Window.Kind} window handle. A Direct3D "
                    + "swapchain presents into a Win32 HWND and nothing else, so this is a wiring error rather "
                    + "than a machine that cannot run the backend.", nameof(request));
            }

            return D3D11GpuDevice.CreateForWindowWindows(request.Window.Handle, request.Width, request.Height,
                request.SyncToVerticalBlank);
        }

        /// <inheritdoc/>
        public GpuProviderDevice CreateHeadless()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw NotOnThisPlatform("headless");

            return D3D11GpuDevice.CreateHeadlessWindows();
        }

        // The off-Windows refusal, and it is a PLATFORM answer rather than the "still being built" one this
        // package used to give: creation is built, and what is missing on macOS or Linux is Direct3D itself.
        // Named here rather than through D3D11PlatformGuard, whose wording is for a Windows-only OBJECT that was
        // somehow constructed elsewhere, which is a different (and unreachable) fault.
        static PlatformNotSupportedException NotOnThisPlatform(string path)
            => new($"The native Direct3D 11 backend cannot create a {path} device on this operating system, "
                + "which has no Direct3D 11. Registration is safe everywhere and reports the backend as "
                + "unsupported off Windows, so read GpuBackendSelector.IsBackendSupported (or "
                + "KhaozEngineD3D11.IsPlatformSupported) before naming this backend.");
    }
}
