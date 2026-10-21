using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The device class a temporal fact measures on, for the few bounds stated apart on a software rasteriser. WARP is
    /// Direct3D 11's software rasteriser on an adapter the device reports as software, the CI's Direct3D 11 leg, and
    /// lavapipe is Vulkan's, the CI's Vulkan leg. A bound that names neither holds on every device at its hardware
    /// value, and one that names only WARP holds on lavapipe at its hardware value.
    /// </summary>
    internal static class TemporalDeviceClass
    {
        static readonly Lazy<(bool Warp, bool Lavapipe)> s_class = new(() =>
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            return (IsWarp(gpu.GpuDevice), IsLavapipe(gpu.GpuDevice));
        });

        /// <summary>Whether the headless device this process creates is WARP. It creates one device the first time it
        /// or <see cref="Lavapipe"/> is read, the same selection every fixture makes.</summary>
        public static bool Warp => s_class.Value.Warp;

        /// <summary>Whether the headless device this process creates is lavapipe (<see cref="Warp"/>).</summary>
        public static bool Lavapipe => s_class.Value.Lavapipe;

        /// <summary>Whether <paramref name="device"/> is WARP.</summary>
        public static bool IsWarp(IGpuDevice device) =>
            device.Backend == GpuBackendKind.Direct3D11Native && device.Diagnostics.SoftwareAdapter == true;

        /// <summary>Whether <paramref name="device"/> is lavapipe.</summary>
        public static bool IsLavapipe(IGpuDevice device) =>
            device.Backend == GpuBackendKind.VulkanNative && device.Diagnostics.SoftwareAdapter == true;

        /// <summary>The report-only colour and confidence rule against frozen D is limited to hardware NVIDIA Direct3D 11.</summary>
        public static bool IsHardwareNvidiaD3D11(IGpuDevice device) =>
            device.Backend == GpuBackendKind.Direct3D11Native && !IsWarp(device)
            && device.Diagnostics.SoftwareAdapter == false
            && !TemporalResolveCostPerfGpuTests.IsSoftwareDevice(device.Capabilities.DeviceName)
            && device.Capabilities.DeviceName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

        /// <summary>The class's name for a measurement's output: WARP, lavapipe or hardware.</summary>
        public static string Name => Warp ? "WARP" : Lavapipe ? "lavapipe" : "hardware";
    }
}
