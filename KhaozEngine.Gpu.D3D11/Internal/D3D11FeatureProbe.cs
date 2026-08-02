using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The functional machine-capability probe behind
    /// <see cref="KhaozEngine.Gpu.IGpuBackendProvider.IsSupported"/>: creates a throwaway
    /// <c>ID3D11Device</c>, reads <c>D3D11_FEATURE_D3D11_OPTIONS</c> off it, and answers whether this machine
    /// carries the two features the native backend cannot work without.
    /// <para>
    /// Only TWO features are checked, and both are hard requirements rather than preferences.
    /// <c>ConstantBufferOffsetting</c> is required because every constant-buffer bind goes through
    /// <c>*SetConstantBuffers1</c> with an explicit first constant and constant count (decision R7).
    /// <c>MapNoOverwriteOnDynamicConstantBuffer</c> is required because the per-frame uniform ring is mapped
    /// <c>MAP_WRITE_NO_OVERWRITE</c> for the whole record phase (decision U2). A machine missing either one
    /// cannot run the backend at all, and finding that out HERE is what routes it through the reported fallback
    /// rather than through a crash on the first frame.
    /// </para>
    /// <para>
    /// This is the only file in the package that names a Vortice type, and every body that does is
    /// <see cref="MethodImplOptions.NoInlining"/> behind the
    /// <see cref="KhaozEngineD3D11.IsPlatformSupported"/> guard, exactly like
    /// <c>KhaozEngine.Gpu.Internal.D3D11ThreadingProbe</c>. That is what keeps the JIT from resolving these
    /// types until such a body is first compiled, so the Vortice assembly is never loaded on macOS or Linux even
    /// though the package targets <c>net10.0</c> and ships there.
    /// </para>
    /// </summary>
    internal static class D3D11FeatureProbe
    {
        // Feature level 11_0 and nothing higher. The two features checked below are 11.1 RUNTIME features that
        // 11_0 hardware reports through D3D11_FEATURE_D3D11_OPTIONS, so asking for a higher level would reject
        // machines the backend runs on perfectly well. Requesting 11_1 in the array is also the classic way to get
        // a blanket E_INVALIDARG out of D3D11CreateDevice on an older runtime, which would read here as "no
        // device" rather than as the version mismatch it is.
        static readonly Vortice.Direct3D.FeatureLevel[] _featureLevels = { Vortice.Direct3D.FeatureLevel.Level_11_0 };

        /// <summary>
        /// Null when this machine can run the native backend, or a sentence saying what is missing, phrased for a
        /// log line a player or a tester will read. Never returns an empty string, so null is the only "yes".
        /// <para>
        /// The caller is responsible for the platform guard and for swallowing exceptions: the provider contract
        /// says the probe must NEVER throw, because "we could not even ask" and "no" are the same answer to the
        /// settings screen and the fallback that consume it.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static string? MissingRequirementWindows()
        {
            Vortice.Direct3D11.ID3D11Device? device = CreateProbeDeviceWindows();
            if (device is null)
            {
                return "no Direct3D 11 feature level 11_0 device could be created, on the default hardware "
                    + "adapter or on WARP";
            }

            try
            {
                Vortice.Direct3D11.FeatureDataD3D11Options options = device.CheckFeatureOptions();
                if (!options.ConstantBufferOffsetting)
                {
                    return "the Direct3D 11 device does not support ConstantBufferOffsetting, which every "
                        + "constant-buffer bind needs: binds go through *SetConstantBuffers1 with an explicit "
                        + "first constant and constant count";
                }
                if (!options.MapNoOverwriteOnDynamicConstantBuffer)
                {
                    return "the Direct3D 11 device does not support MapNoOverwriteOnDynamicConstantBuffer, which "
                        + "the per-frame uniform ring needs: the ring is mapped MAP_WRITE_NO_OVERWRITE for the "
                        + "whole record phase";
                }
                return null;
            }
            finally
            {
                device.Dispose();
            }
        }

        /// <summary>
        /// A throwaway device on the default hardware adapter, falling back to WARP, or null when neither
        /// answers. WARP counts as a yes deliberately: it is the rasterizer the committed Direct3D 11 goldens are
        /// baked on and the one CI pins, so a Windows machine with no usable GPU can still run and verify this
        /// backend.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static Vortice.Direct3D11.ID3D11Device? CreateProbeDeviceWindows()
        {
            if (TryCreate(Vortice.Direct3D.DriverType.Hardware, out Vortice.Direct3D11.ID3D11Device? hardware))
                return hardware;
            return TryCreate(Vortice.Direct3D.DriverType.Warp, out Vortice.Direct3D11.ID3D11Device? warp) ? warp : null;
        }

        // One creation attempt. DeviceCreationFlags.None on purpose: the debug layer is a separate, env-gated
        // diagnostic and requiring it here would answer "unsupported" on every machine without the Windows
        // graphics tools installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static bool TryCreate(Vortice.Direct3D.DriverType driverType, out Vortice.Direct3D11.ID3D11Device? device)
        {
            SharpGen.Runtime.Result result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                IntPtr.Zero, driverType, Vortice.Direct3D11.DeviceCreationFlags.None, _featureLevels, out device);

            if (result.Success && device is not null) return true;

            // A partial success would leak the device: the call can hand one back on a non-Success HRESULT
            // (S_FALSE is the documented case), and nothing else here will ever release it.
            device?.Dispose();
            device = null;
            return false;
        }
    }
}
