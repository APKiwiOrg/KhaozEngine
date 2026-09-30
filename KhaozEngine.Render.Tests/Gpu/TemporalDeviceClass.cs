using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The device class a temporal fact measures on, for the few bounds stated apart on a software rasteriser. WARP is
    /// Direct3D 11's software rasteriser on an adapter the device reports as software, the CI's Direct3D 11 leg. A
    /// bound that names it holds on every other device at its hardware value.
    /// </summary>
    internal static class TemporalDeviceClass
    {
        static readonly Lazy<bool> s_warp = new(() =>
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            return IsWarp(gpu.GpuDevice);
        });

        /// <summary>Whether the headless device this process creates is WARP. It creates one device the first time it
        /// is read, the same selection every fixture makes.</summary>
        public static bool Warp => s_warp.Value;

        /// <summary>Whether <paramref name="device"/> is WARP.</summary>
        public static bool IsWarp(IGpuDevice device) =>
            device.Backend == GpuBackendKind.Direct3D11Native && device.Diagnostics.SoftwareAdapter == true;
    }
}
