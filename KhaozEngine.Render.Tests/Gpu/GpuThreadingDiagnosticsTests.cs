using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Headless tests for <see cref="GpuThreadingDiagnostics"/>, the pure half of the Direct3D11 driver-threading
    /// probe: what the INFO line says for each of the three answers (native, emulated, unknown) and when the WARN
    /// arm fires.
    /// <para>
    /// The native query itself is NOT covered here and cannot be. It calls
    /// <c>ID3D11Device::CheckFeatureSupport</c> on a live Direct3D11 device, so it only ever executes on Windows
    /// on the D3D11 backend, and this suite runs on macOS and Linux with no device at all. Everything that could
    /// be factored out of it was, which is exactly this formatting plus the warn decision. Faking the interop to
    /// claim coverage would assert that a stub returns what the stub was told to return.
    /// </para>
    /// </summary>
    public sealed class GpuThreadingDiagnosticsTests
    {
        [Fact]
        public void Describe_NativeCommandLists_ReadsAsTheGoodCase()
        {
            string text = GpuThreadingDiagnostics.Describe(new GpuThreadingCaps(true, true));

            Assert.Contains("DriverCommandLists=TRUE", text);
            Assert.Contains("DriverConcurrentCreates=TRUE", text);
            Assert.Contains("the driver builds command lists", text);
            Assert.DoesNotContain("EMULATING", text);
        }

        [Fact]
        public void Describe_EmulatedCommandLists_SaysSoInTheLine()
        {
            string text = GpuThreadingDiagnostics.Describe(new GpuThreadingCaps(false, true));

            Assert.Contains("DriverCommandLists=FALSE", text);
            Assert.Contains("EMULATING", text);
        }

        [Fact]
        public void Describe_ReportsConcurrentCreatesIndependently()
        {
            string text = GpuThreadingDiagnostics.Describe(new GpuThreadingCaps(true, false));

            Assert.Contains("DriverCommandLists=TRUE", text);
            Assert.Contains("DriverConcurrentCreates=FALSE", text);
        }

        [Fact]
        public void Describe_Null_IsTheUnknownBucket()
        {
            Assert.Equal(GpuThreadingDiagnostics.UnknownDescription, GpuThreadingDiagnostics.Describe(null));
        }

        [Fact]
        public void ShouldWarn_OnlyForAKnownFalseDriverCommandLists()
        {
            Assert.True(GpuThreadingDiagnostics.ShouldWarn(new GpuThreadingCaps(false, true)));
            Assert.True(GpuThreadingDiagnostics.ShouldWarn(new GpuThreadingCaps(false, false)));
            Assert.False(GpuThreadingDiagnostics.ShouldWarn(new GpuThreadingCaps(true, false)));

            // "We could not ask" is not evidence of a bad driver, so it must not raise the alarm that a real
            // false does. A warning nobody can act on trains the reader to skip the one that matters.
            Assert.False(GpuThreadingDiagnostics.ShouldWarn(null));
        }

        [Fact]
        public void EmulatedCommandListsWarning_TellsANonExpertWhatToDo()
        {
            string warning = GpuThreadingDiagnostics.EmulatedCommandListsWarning;

            Assert.Contains("DriverCommandLists=FALSE", warning);
            Assert.Contains("SEVERE", warning);
            // It must name the escape hatch, otherwise the reader knows they have a problem and not one thing
            // to try about it.
            Assert.Contains(GpuBackendSelector.EnvVarName + "=vulkan", warning);
        }

        /// <summary>
        /// The failure arm's own body. It reports that NOTHING was learned, which is a different fact from a
        /// driver that answered badly, so it names the reason and says what a later report cannot rule out rather
        /// than telling the reader to change a setting.
        /// </summary>
        [Fact]
        public void ProbeFailureWarning_NamesTheReason_AndWhatItCosts()
        {
            string warning = GpuThreadingDiagnostics.ProbeFailureWarning("the device did not answer");

            Assert.Contains("Could not read the Direct3D11 driver threading capabilities", warning);
            Assert.Contains("the device did not answer", warning);
            Assert.Contains("cannot rule out", warning);
            // Not the emulating-driver alarm: that one claims a KNOWN problem, and this arm knows nothing.
            Assert.DoesNotContain("SEVERE", warning);
        }

        /// <summary>
        /// The whole warn decision under the threading INFO line, as one pure function, so BOTH device-creation
        /// paths can be pinned on a machine with no Direct3D11 anywhere. The failure string is the one input an
        /// adopted native device must supply for itself, and without it a faulted probe renders exactly like an
        /// ordinary non-Direct3D11 session: "unknown", and no warning at all.
        /// </summary>
        [Fact]
        public void WarningFor_PicksTheFailureArm_WhenTheProbeFaulted()
        {
            string? warning = GpuThreadingDiagnostics.WarningFor(caps: null, probeFailure: "boom");

            Assert.Equal(GpuThreadingDiagnostics.ProbeFailureWarning("boom"), warning);
        }

        [Fact]
        public void WarningFor_IsSilent_WhenThereIsNothingToReport()
        {
            // Answered, and the answer is good.
            Assert.Null(GpuThreadingDiagnostics.WarningFor(new GpuThreadingCaps(true, true), null));
            // Never asked: not applicable is not a fault, so it warns about nothing.
            Assert.Null(GpuThreadingDiagnostics.WarningFor(null, null));
        }

        /// <summary>
        /// A driver that answered badly outranks a failure reason. Caps that came back are the actionable fact,
        /// and the reader gets one warning rather than two competing ones.
        /// </summary>
        [Fact]
        public void WarningFor_PrefersAKnownBadDriver_OverAFailureReason()
        {
            Assert.Equal(GpuThreadingDiagnostics.EmulatedCommandListsWarning,
                GpuThreadingDiagnostics.WarningFor(new GpuThreadingCaps(false, true), "boom"));
        }

        [Fact]
        public void CommandListsAreEmulated_IsTheInverseOfTheDriverFlag()
        {
            Assert.True(new GpuThreadingCaps(false, true).CommandListsAreEmulated);
            Assert.False(new GpuThreadingCaps(true, true).CommandListsAreEmulated);
        }

        /// <summary>
        /// The no-op guarantee, asserted rather than claimed: the probe asks a driver ONLY on Windows on a
        /// Direct3D11 backend. Everything else returns before any device access and before the Vortice types are
        /// ever named, which is what keeps that assembly unloaded on macOS and Linux.
        /// <para>
        /// "A Direct3D11 backend" is BOTH implementations. What is being asked about is the driver, and the driver
        /// is the same one whichever implementation drove it, so the native leg gets the same INFO line, the same
        /// emulated-command-lists WARN, and the same two telemetry fields.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(GpuBackendKind.Direct3D11, true, true)]
        [InlineData(GpuBackendKind.Direct3D11, false, false)]
        [InlineData(GpuBackendKind.Direct3D11Native, true, true)]
        [InlineData(GpuBackendKind.Direct3D11Native, false, false)]
        [InlineData(GpuBackendKind.Metal, true, false)]
        [InlineData(GpuBackendKind.Metal, false, false)]
        [InlineData(GpuBackendKind.Vulkan, true, false)]
        [InlineData(GpuBackendKind.Vulkan, false, false)]
        [InlineData(GpuBackendKind.OpenGL, true, false)]
        [InlineData(GpuBackendKind.OpenGL, false, false)]
        public void Probe_RunsOnlyOnWindowsDirect3D11(GpuBackendKind backend, bool isWindows, bool expected)
        {
            Assert.Equal(expected, D3D11ThreadingProbe.IsApplicable(backend, isWindows));
        }

        /// <summary>
        /// The raw-pointer entry the native backend feeds carries the same no-op guarantee on the same shape of
        /// pure predicate. There is no backend argument, because a caller holding an <c>ID3D11Device</c> has
        /// already answered that question, so what stands in for it is whether a device was supplied at all.
        /// </summary>
        [Theory]
        [InlineData(1, true, true)]
        [InlineData(1, false, false)]
        [InlineData(0, true, false)]
        [InlineData(0, false, false)]
        public void RawPointerProbe_RunsOnlyOnWindowsWithADevice(int pointer, bool isWindows, bool expected)
        {
            Assert.Equal(expected, D3D11ThreadingProbe.IsApplicable(new IntPtr(pointer), isWindows));
        }

        /// <summary>
        /// And the entry point itself degrades rather than throwing when there is nothing to ask, on every OS this
        /// suite runs on. Null caps with a NULL failure is the not-applicable answer, kept distinct from a probe
        /// that was attempted and faulted, which reports a reason.
        /// </summary>
        [Fact]
        public void RawPointerProbe_WithNoDevice_AnswersUnknownWithoutAFailure()
        {
            GpuThreadingCaps? caps = D3D11ThreadingProbe.TryQuery(IntPtr.Zero, out string? failure);

            Assert.Null(caps);
            Assert.Null(failure);
        }
    }
}
