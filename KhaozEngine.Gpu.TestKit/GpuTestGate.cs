using System;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Vulkan;

namespace KhaozEngine.Gpu.TestKit
{
    /// <summary>Shared run gate and backend-family resolver for GPU tests.</summary>
    public static class GpuTestGate
    {
        const string DisabledReason =
            "set KE_GPU_TESTS=1 (strict) or KE_GPU_TESTS=probe (skip if no device) to run GPU golden tests";

        static readonly Lazy<ProbeResult> HeadlessProbe = CreateProbe(CreateHeadlessBackend);

        static GpuTestGate()
        {
            KhaozEngineD3D11.Register();
            KhaozEngineVulkan.Register();
            KhaozEngineMetal.Register();
        }

        /// <summary>
        /// Returns null when GPU tests should run. Otherwise returns the reason they should be skipped.
        /// <c>KE_GPU_TESTS=1</c> is strict and never turns a missing device into a skip.
        /// </summary>
        public static string? SkipReason()
            => DecideSkipReason(
                Environment.GetEnvironmentVariable("KE_GPU_TESTS"),
                () => HeadlessProbe.Value.SkipReason);

        /// <summary>The golden-file family of the backend created by the cached headless probe.</summary>
        public static string BackendName
        {
            get
            {
                ProbeResult result = HeadlessProbe.Value;
                return result.BackendName ?? throw new InvalidOperationException(result.SkipReason);
            }
        }

        internal static string? DecideSkipReason(string? envValue, Func<string?> probe)
        {
            if (envValue == "1") return null;
            if (envValue == "probe") return probe();
            return DisabledReason;
        }

        internal static Lazy<ProbeResult> CreateProbe(Func<KhaozEngine.Gpu.GpuBackendKind> createBackend)
            => new(() => Probe(createBackend));

        internal static ProbeResult Probe(Func<KhaozEngine.Gpu.GpuBackendKind> createBackend)
        {
            KhaozEngine.Gpu.GpuBackendKind backend;
            try
            {
                backend = createBackend();
            }
            catch (Exception ex)
            {
                return new ProbeResult(null,
                    $"KE_GPU_TESTS=probe: no headless GPU device ({ex.GetType().Name}: {ex.Message})");
            }

            return new ProbeResult(BackendNameFor(backend), null);
        }

        internal static string BackendNameFor(KhaozEngine.Gpu.GpuBackendKind backend) => backend switch
        {
            KhaozEngine.Gpu.GpuBackendKind.Metal => "metal",
            KhaozEngine.Gpu.GpuBackendKind.Vulkan => "vulkan",
            KhaozEngine.Gpu.GpuBackendKind.Direct3D11 => "direct3d11",
            KhaozEngine.Gpu.GpuBackendKind.OpenGL => "opengl",
            KhaozEngine.Gpu.GpuBackendKind.Direct3D11Native => "direct3d11-native",
            KhaozEngine.Gpu.GpuBackendKind.VulkanNative => "vulkan-native",
            KhaozEngine.Gpu.GpuBackendKind.MetalNative => "metal-native",
            _ => throw new NotSupportedException(
                $"No golden family is decided for {backend}. Appending a GpuBackendKind member means deciding "
                + "whether it owns a family or shares one."),
        };

        static KhaozEngine.Gpu.GpuBackendKind CreateHeadlessBackend()
        {
            using KhaozEngine.Gpu.GpuDeviceContext context =
                KhaozEngine.Gpu.GpuDeviceContext.CreateHeadless();
            return context.GpuDevice.Backend;
        }

        internal readonly record struct ProbeResult(string? BackendName, string? SkipReason);
    }
}
