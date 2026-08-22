using System;
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

        // The MSAA gate (#603). A device below four samples downgrades an MSAA request to Fxaa inside
        // AntiAliasing.ResolveFor, so a test that asks for MSAA and does not check would compare the single-sample
        // path against itself and pass having measured nothing. The named skip is what stops that being invisible.
        [Theory]
        [InlineData(4, false)]
        [InlineData(8, false)]
        [InlineData(2, true)]
        [InlineData(1, true)]
        public void Four_sample_msaa_skip_fires_only_below_four_samples(int maxMsaa, bool expectSkip)
        {
            string? reason = GpuFactAttribute.FourSampleMsaaSkipReason(("Vulkan", maxMsaa));
            Assert.Equal(expectSkip, reason != null);
            if (expectSkip)
            {
                Assert.Contains("Vulkan", reason!, StringComparison.Ordinal);
                Assert.Contains("MaxMsaaSampleCount", reason!, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void No_device_at_all_runs_the_msaa_gate_too()
        {
            // The same rule the completion-fence gate follows: a device that could not be created is an error
            // downstream, never a capability skip.
            Assert.Null(GpuFactAttribute.FourSampleMsaaSkipReason(null));
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

        // The paired gate (#682). The paravirtual device drops setDepthClipMode Clamp under MTLDebugDevice, and
        // under nothing else, so BOTH halves have to be true before a row is given up. Keying on the layer alone
        // would retire the row from real Metal, which is the one machine that proves the engine right, and keying
        // on the adapter alone would retire it from the leg that runs it on every push.
        [Theory]
        [InlineData("Apple Paravirtual device", true, true)]
        [InlineData("Apple Paravirtual device", false, false)]
        [InlineData("Apple M2 Max", true, false)]
        [InlineData("Apple M2 Max", false, false)]
        [InlineData(null, true, false)]
        [InlineData("", true, false)]
        public void The_metal_validation_gate_needs_both_the_adapter_and_the_layer(
            string? deviceName, bool apiValidationHoldsTheDevice, bool expectSkip)
        {
            string? reason = GpuFactAttribute.VirtualGpuUnderMetalApiValidationSkipReason(
                deviceName, apiValidationHoldsTheDevice);

            Assert.Equal(expectSkip, reason != null);
            if (expectSkip)
            {
                Assert.Contains(deviceName!, reason!, StringComparison.Ordinal);
                Assert.Contains("MTL_DEBUG_LAYER", reason!, StringComparison.Ordinal);
                Assert.Contains("issues/682", reason!, StringComparison.Ordinal);
            }
        }

        // A capture DISPLACES the debug device (#614), so a capture-tier run is holding a CaptureMTLDevice that
        // validates nothing and must keep running the row. Reading MTL_DEBUG_LAYER alone would skip it there for
        // an instrument that is not present.
        [Theory]
        [InlineData("1", null, true)]
        [InlineData("1", "", true)]
        [InlineData("1", "1", false)]
        [InlineData("0", null, false)]
        [InlineData(null, null, false)]
        [InlineData(null, "1", false)]
        public void The_api_validation_layer_holds_the_device_only_when_no_capture_displaced_it(
            string? debugLayer, string? capture, bool expectHeld)
        {
            Assert.Equal(expectHeld, GpuFactAttribute.ApiValidationHoldsTheDevice(debugLayer, capture));
        }

        // The dormancy escape (VulkanDormancy). Its whole value is what the message SAYS, since a leg that set
        // KE_VULKAN_REQUIRED=1 and then went red learns nothing from "the probe said no". The decision half is
        // pure so it is assertable here, on a machine with no Vulkan loader, which is the only kind of machine
        // that will ever review this file.
        [Fact]
        public void A_required_vulkan_leg_is_told_what_the_probe_objected_to()
        {
            string message = VulkanDormancy.RefusalMessage(
                "no physical device clears the Vulkan 1.3 floor: llvmpipe reports 1.2.0");

            Assert.Contains(VulkanDormancy.RequiredVariable, message, StringComparison.Ordinal);
            Assert.Contains("llvmpipe reports 1.2.0", message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_refusal_with_no_named_requirement_still_says_where_to_look()
        {
            // Null means the requirement walk found nothing missing, so the refusal came from somewhere else. The
            // message has to say so rather than printing an empty reason, which would read as a broken test.
            string message = VulkanDormancy.RefusalMessage(null);

            Assert.Contains(VulkanDormancy.RequiredVariable, message, StringComparison.Ordinal);
            Assert.Contains("KE_VULKAN_DEVICE", message, StringComparison.Ordinal);
        }

        // The same escape on the third backend (MetalDormancy), and the same reason for asserting the pure half
        // here: this message is only ever PRODUCED on a machine that has no Metal, and every Windows and Linux leg
        // in the matrix is one, so a message that read badly would be discovered by whoever was already debugging
        // a red leg. The two spellings are asserted rather than the whole text: the variable, because the
        // workflow, the file headers and this message all have to agree on it, and the machine's own words,
        // because a refusal that does not carry them tells a reader nothing they did not already know.
        [Fact]
        public void A_required_metal_leg_is_told_what_the_machine_objected_to()
        {
            string message = MetalDormancy.RefusalMessage(
                "the default device does not support the MTLGPUFamily floor");

            Assert.Contains(MetalDormancy.RequiredVariable, message, StringComparison.Ordinal);
            Assert.Contains("MTLGPUFamily floor", message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_metal_refusal_with_no_recorded_reason_still_says_so()
        {
            string message = MetalDormancy.RefusalMessage(null);

            Assert.Contains(MetalDormancy.RequiredVariable, message, StringComparison.Ordinal);
            Assert.Contains("no reason was recorded", message, StringComparison.Ordinal);
        }
    }
}
