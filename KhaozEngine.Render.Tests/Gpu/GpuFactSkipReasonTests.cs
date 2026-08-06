using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Headless coverage for <see cref="GpuFactAttribute.SkipReason"/>: the pure gate that decides run-vs-skip
    /// from the raw <c>KE_GPU_TESTS</c> value and a device probe. Uses stub probes so it never touches a real
    /// device and never mutates process environment variables.
    /// </summary>
    public sealed class GpuFactSkipReasonTests
    {
        [Fact]
        public void Strict_always_runs_even_when_probe_would_fail()
        {
            // "1" must never consult the probe: strict mode runs so that a downstream device-creation failure
            // errors instead of silently skipping. Fail the stub probe to prove it is not called.
            string? reason = GpuFactAttribute.SkipReason("1", () => "probe should not be consulted in strict mode");
            Assert.Null(reason);
        }

        [Fact]
        public void Probe_runs_when_device_available()
        {
            string? reason = GpuFactAttribute.SkipReason("probe", () => null);
            Assert.Null(reason);
        }

        [Fact]
        public void Probe_skips_with_reason_when_device_unavailable()
        {
            string? reason = GpuFactAttribute.SkipReason("probe", () => "no device: stub");
            Assert.Equal("no device: stub", reason);
        }

        [Fact]
        public void Unset_skips_and_never_probes()
        {
            string? reason = GpuFactAttribute.SkipReason(null, () => "probe should not be consulted when unset");
            Assert.NotNull(reason);
            Assert.Contains("KE_GPU_TESTS", reason);
        }

        [Fact]
        public void Other_value_skips_and_never_probes()
        {
            string? reason = GpuFactAttribute.SkipReason("0", () => "probe should not be consulted for other values");
            Assert.NotNull(reason);
            Assert.Contains("KE_GPU_TESTS", reason);
        }

        // The capability gate (#423): a device that cannot signal a completion fence skips the tests that measure
        // the fence path, instead of failing an assertion no Direct3D11 run can ever satisfy.
        [Fact]
        public void A_device_without_completion_fences_skips_with_the_backend_named()
        {
            string? reason = GpuFactAttribute.CompletionFenceSkipReason(("Direct3D11", false));
            Assert.NotNull(reason);
            Assert.Contains("Direct3D11", reason);
            Assert.Contains("SupportsCompletionFences", reason);
        }

        [Fact]
        public void A_device_with_completion_fences_runs()
        {
            Assert.Null(GpuFactAttribute.CompletionFenceSkipReason(("Metal", true)));
        }

        [Fact]
        public void No_device_at_all_runs_so_the_failure_stays_an_error()
        {
            // Strict mode's whole point: a leg whose device is broken must go red, never quiet. A capability
            // requirement is about what a device CAN do and must not swallow a device that is not there.
            Assert.Null(GpuFactAttribute.CompletionFenceSkipReason(null));
        }

        [Theory]
        [InlineData("Apple Paravirtual device", true)]
        [InlineData("Microsoft Basic Render Driver", false)]
        [InlineData("Apple M2 Pro", false)]
        [InlineData("llvmpipe (LLVM 15.0.7, 256 bits)", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Virtual_gpu_skip_fires_only_on_a_virtualised_adapter(string? deviceName, bool expectSkip)
        {
            // A null name means no device could be created. That must RUN so the failure surfaces downstream as an
            // error, never a quiet capability skip, which is the rule CompletionFenceSkipReason already follows.
            string? reason = GpuFactAttribute.VirtualGpuSkipReason(deviceName);
            Assert.Equal(expectSkip, reason != null);
            if (expectSkip) Assert.Contains(deviceName!, reason!);
        }
    }
}
