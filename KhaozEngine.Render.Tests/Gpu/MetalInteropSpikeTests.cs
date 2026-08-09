using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// VERIFICATION TASK ONE of row 1 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, run
    /// against a real device. <see cref="MetalInteropSpike"/> touches every Objective-C call the design names
    /// and this is what makes it RUN, which is the difference between this spike and phase 3's: that one could
    /// only be a compile-time inventory, because the machine that wrote it had no Vulkan loader.
    /// <para>
    /// WHAT A RED RUN MEANS. Bet MM9 is "the engine-owned interop layer is ABI-correct on arm64", and section 16
    /// gives it no kill switch, because an ABI error is a crash rather than a tunable. So a failure here is not a
    /// tuning problem: it says a call shape in <c>MetalInteropSpike.Native.cs</c> does not match the real method,
    /// and every row that copies that shape would be corrupting memory. The full transcript is printed either
    /// way, because the numbers are the deliverable and a green assertion alone records nothing.
    /// </para>
    /// <para>
    /// DORMANT OFF macOS RATHER THAN SKIPPED. Under <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run
    /// the whole assembly in strict mode, where a skip is a failure, so this returns early with the platform
    /// recorded rather than skipping. That is phase 3's row-19 lesson: a dormant row is not a skip, and a
    /// zero-skipped gate that was satisfied by rows asserting nothing is worth nothing.
    /// </para>
    /// <para>
    /// ONE ANSWER IS RECORDED RATHER THAN ASSERTED. M-G3 asks whether in-process environment mutation reaches
    /// the Metal validation layer, and a "no" there is not a defect: section 3.1 names the fallback (a job-level
    /// environment variable in CI plus a documented local prefix) and row 4 takes whichever answer this gives.
    /// It is not asserted for a second reason too: the reading is only ATTRIBUTABLE in a process that has not
    /// already used Metal and did not inherit the variable, so a full-suite run and the native CI leg (which
    /// sets <c>MTL_DEBUG_LAYER</c> at job level) both answer it for reasons that are not the mechanism. The
    /// attributable reading is the one taken with this test alone, and it is recorded in the design doc with the
    /// control that makes it mean something.
    /// </para>
    /// </summary>
    public sealed class MetalInteropSpikeTests
    {
        readonly ITestOutputHelper _output;

        public MetalInteropSpikeTests(ITestOutputHelper output) => _output = output;

        [GpuFact]
        public void TheInteropLayerIsAbiCorrectAgainstARealDevice()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                _output.WriteLine("dormant: not macOS, so there is no Metal device to measure against.");
                return;
            }

            MetalInteropSpikeResult result = MetalInteropSpike.Run();
            string report = result.Report();
            _output.WriteLine(report);

            Assert.True(result.DeviceCreated,
                "MTLCreateSystemDefaultDevice returned nil on a machine the platform guard said was macOS. "
                + "Everything below is unmeasured:\n" + report);

            // The load-bearing assertion. Every call the spike recorded went into ONE command buffer, so a
            // completed status with no error is the device accepting all of them: the array setters, the offset
            // setters, all three by-value struct shapes and both encoder kinds. A wrong argument class shows up
            // here (or as a crash) rather than as a wrong pixel, which section 3.1 calls the one comforting
            // property of this risk.
            Assert.True(result.CommandBufferStatus == 4 && result.CommandBufferErrorWasNil,
                "the command buffer carrying every recorded interop call did not complete cleanly:\n" + report);

            Assert.True(result.ArraySettersRecorded, "the array setters (M-R6) did not record:\n" + report);
            Assert.True(result.OffsetSettersRecorded, "the offset setters (M-R7) did not record:\n" + report);
            Assert.True(result.ByValueStructsRecorded,
                "the by-value struct setters (viewport, scissor, clear colour) did not record:\n" + report);

            // M-F3. No delegate, no GetFunctionPointerForDelegate and no GC handle on the completion path, which
            // is what keeps it AOT-clean. The named fallback is the incumbent's delegate-and-dictionary shape.
            Assert.True(result.CompletionHandlerFired,
                "the [UnmanagedCallersOnly] completion handler never fired:\n" + report);

            // M-F1, all four members. The ring's segment recycling reads this timeline, so a fallback here
            // changes row 8 as well as row 5, and removes M-P4's fifth extraction candidate.
            Assert.True(result.SharedEventCreated, "MTLSharedEvent could not be created:\n" + report);
            Assert.True(result.SharedEventWaitSucceeded,
                "waitUntilSignaledValue:timeoutMS: did not observe the encoded signal:\n" + report);
            Assert.Equal(42UL, result.SharedEventSignaledValue);

            // The arm64 scalar caveats, which are the ones section 3.1 says hand-rolled interop dies on.
            Assert.True(result.BoolIsOneByte, "BOOL did not round-trip as one byte:\n" + report);
            Assert.True(result.CGFloatIsDouble, "CGFloat did not round-trip as a double:\n" + report);

            // M-N3 and M-W4.
            Assert.NotEqual("(none)", result.SupportedFamilies);
            Assert.True(result.MetalLayerCreated, "CAMetalLayer could not be created:\n" + report);
            Assert.Equal((nuint)3, result.MaximumDrawableCountReadBack);
        }
    }
}
