using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.TestKit;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class GpuTestGateTests
    {
        const string DisabledReason =
            "set KE_GPU_TESTS=1 (strict) or KE_GPU_TESTS=probe (skip if no device) to run GPU golden tests";

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("PROBE")]
        public void Disabled_values_skip_without_probing(string? value)
        {
            int probes = 0;

            string? reason = GpuTestGate.DecideSkipReason(value, () =>
            {
                probes++;
                return "unexpected probe";
            });

            Assert.Equal(DisabledReason, reason);
            Assert.Equal(0, probes);
        }

        [Fact]
        public void Strict_mode_runs_without_probing()
        {
            int probes = 0;

            string? reason = GpuTestGate.DecideSkipReason("1", () =>
            {
                probes++;
                return "missing device";
            });

            Assert.Null(reason);
            Assert.Equal(0, probes);
        }

        [Fact]
        public void Probe_mode_runs_when_a_device_is_available()
        {
            string? reason = GpuTestGate.DecideSkipReason("probe", () => null);

            Assert.Null(reason);
        }

        [Fact]
        public void Probe_mode_returns_the_concrete_device_failure()
        {
            const string expected = "KE_GPU_TESTS=probe: no headless GPU device (NotSupportedException: unavailable)";

            string? reason = GpuTestGate.DecideSkipReason("probe", () => expected);

            Assert.Equal(expected, reason);
        }

        [Theory]
        [InlineData(GpuBackendKind.Metal, "metal")]
        [InlineData(GpuBackendKind.Vulkan, "vulkan")]
        [InlineData(GpuBackendKind.Direct3D11, "direct3d11")]
        [InlineData(GpuBackendKind.OpenGL, "opengl")]
        [InlineData(GpuBackendKind.Direct3D11Native, "direct3d11-native")]
        [InlineData(GpuBackendKind.VulkanNative, "vulkan-native")]
        [InlineData(GpuBackendKind.MetalNative, "metal-native")]
        public void Backend_name_comes_from_the_backend_created_by_the_probe(
            GpuBackendKind createdBackend, string expected)
        {
            GpuTestGate.ProbeResult result = GpuTestGate.Probe(() => createdBackend);

            Assert.Equal(expected, result.BackendName);
            Assert.Null(result.SkipReason);
        }

        [Fact]
        public void Failed_probe_records_a_concrete_skip_reason()
        {
            GpuTestGate.ProbeResult result = GpuTestGate.Probe(
                () => throw new InvalidOperationException("driver refused"));

            Assert.Null(result.BackendName);
            Assert.Equal(
                "KE_GPU_TESTS=probe: no headless GPU device (InvalidOperationException: driver refused)",
                result.SkipReason);
        }

        [Fact]
        public void Unmapped_backend_is_a_contract_failure_not_a_missing_device()
        {
            Assert.Throws<NotSupportedException>(
                () => GpuTestGate.Probe(() => (GpuBackendKind)9001));
        }

        [Fact]
        public void Headless_probe_is_cached_once()
        {
            int attempts = 0;
            Lazy<GpuTestGate.ProbeResult> probe = GpuTestGate.CreateProbe(() =>
            {
                attempts++;
                return GpuBackendKind.MetalNative;
            });

            _ = probe.Value;
            _ = probe.Value;

            Assert.Equal(1, attempts);
        }
    }
}
