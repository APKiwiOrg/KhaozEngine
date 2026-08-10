using System;
using System.Collections.Generic;
using System.Text.Json;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT A NATIVE METAL DEVICE REPORTS ABOUT ITSELF, against a real one: the whole
    /// <see cref="GpuDeviceCounters"/> fill (M-G6) and the two <see cref="GpuDeviceDiagnostics"/> fields reaching
    /// the telemetry session header, which is rollout gate 4's path. Work-breakdown row 16
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/582).
    ///
    /// <para><b>THE ACCUMULATORS WERE BUILT BY THE ROWS THAT OWN THE SUBSYSTEMS, AND THIS ROW ONLY WIRES THEM
    /// UP.</b> The drain pair comes off the timeline (row 5), the backpressure pair off the accumulator the
    /// uniform ring stalls into (row 8), and the off-timeline pair off that ring's pending patches (row 8). Each
    /// of those rows tests its own accumulator where it can provoke the wait deterministically. So what this row
    /// has to prove is narrower and is stated exactly rather than generously: that each seam field reads THE
    /// ACCUMULATOR IT CLAIMS TO. That is asserted by identity against the device's own internal readings rather
    /// than by range, because the failure this fill can actually have is a transposed pair, and a transposed pair
    /// of non-negative numbers passes every range check there is.</para>
    ///
    /// <para><b>WHAT IS DELIBERATELY NOT ASSERTED IS THAT ANY ACCUMULATOR MOVES.</b> The obvious version (submit,
    /// drain, expect <c>DrainCount</c> to rise) is RACY by design here for the reason it is racy on the Vulkan
    /// sibling: <c>MetalTimeline.WaitForIdle</c> returns without counting when the completed value has already
    /// reached the last submitted one, which is the seam's "a wait that did not block is not a drain" rule
    /// honoured literally, and an empty submission on an M-series GPU routinely completes before the drain is
    /// entered. A flaky assertion is worse than no assertion.</para>
    ///
    /// <para><b>THE THREE PRESENT-BOUNDARY ZEROS ARE READINGS RATHER THAN GAPS.</b> A headless device has no
    /// swapchain, so it opens no frame at this seam and has no drawable to wait on, and <c>FramesBegun</c> with
    /// the acquire pair are therefore exactly zero and literally true. The swapchain row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581) is what makes them move on a windowed device.
    /// <see cref="GpuDeviceCounters.HasValue"/> being true is what makes a capture carry columns at all, and it is
    /// the difference between this device and the incumbent Veldrid Metal path, which keeps the default and
    /// reports nothing.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because it builds a whole device beside the suite's
    /// own, and because the identity assertions read two snapshots in a row and want nothing else submitting
    /// between them.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalCountersAndHeaderGpuTests
    {
        readonly ITestOutputHelper _out;
        public MetalCountersAndHeaderGpuTests(ITestOutputHelper o) => _out = o;

        /// <summary>
        /// Every field of the fill, off a real device, and each one asserted against the accumulator it is
        /// supposed to be reading. Nine channels reach a sample row, the three present-boundary fields read zero,
        /// and no field is negative, which on a pair summing stopwatch deltas is the shape a transposed
        /// count-and-milliseconds argument would break.
        /// </summary>
        [GpuFact]
        public void TheCounterFillReadsTheAccumulatorEachFieldClaims()
        {
            using GpuDeviceContext? context = CreateNativeOrNull();
            if (context is null) return;

            var device = (MetalGpuDevice)context.GpuDevice;

            // A drain and a submit first, so the accumulators are read after the device has actually done
            // something rather than only at rest. Whether either one COUNTED is a race (see the class remarks),
            // and the identity assertions below hold either way, which is the point of asserting identity.
            using (IGpuCommandList list = device.Factory.CreateCommandList())
            {
                list.Begin();
                list.End();
                device.Submit(list);
            }
            device.WaitForIdle();

            GpuDeviceCounters counters = context.Counters;

            Assert.True(counters.HasValue,
                "a native Metal device counts, so reporting the default value would say nobody looked.");

            // THE WIRING, WHICH IS THE ONLY THING THIS ROW BUILT. Each seam field against the device's own
            // reading of the subsystem section 14 names as its source. A field wired to the wrong accumulator, or
            // a count and a millisecond figure swapped between two pairs, fails HERE by name.
            MetalWaitTotals drain = device.Timeline.TotalDrain;
            MetalWaitTotals stalls = device.BackpressureTotals;
            MetalRingPatchStats patches = device.Rings.OffTimelinePatches;

            Assert.Equal(drain.Count, counters.DrainCount);
            Assert.Equal(drain.TotalMs, counters.DrainMs);
            Assert.Equal(stalls.Count, counters.BackpressureStallCount);
            Assert.Equal(stalls.TotalMs, counters.BackpressureStallMs);
            Assert.Equal(patches.Deferred, counters.OffTimelineDeferred);
            Assert.Equal(patches.Outstanding, counters.OffTimelineOutstanding);

            // THE PRESENT BOUNDARY'S THREE, which a headless device answers zero to truthfully.
            Assert.Equal(0, counters.FramesBegun);
            Assert.Equal(0, counters.AcquireWaitCount);
            Assert.Equal(0d, counters.AcquireWaitMs);

            Assert.True(counters.DrainCount >= 0);
            Assert.True(counters.DrainMs >= 0d);
            Assert.True(counters.BackpressureStallCount >= 0);
            Assert.True(counters.BackpressureStallMs >= 0d);
            Assert.True(counters.OffTimelineOutstanding <= counters.OffTimelineDeferred,
                "a patch cannot still be outstanding without having been deferred first.");

            // THE NAMES, NOT THE COUNT. Once HasValue is true the count is a tautology about the projection, so
            // it says nothing about THIS device. The name set is the question a gate-4 reader actually has:
            // which columns does a capture off this backend carry.
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (TelemetryChannel channel in GpuTelemetryChannels.For(counters)) written.Add(channel.Name);

            Assert.Equal(
                new HashSet<string>(
                    new[]
                    {
                        GpuTelemetryChannels.FramesBegun,
                        GpuTelemetryChannels.DrainCount,
                        GpuTelemetryChannels.DrainMs,
                        GpuTelemetryChannels.BackpressureStalls,
                        GpuTelemetryChannels.BackpressureStallMs,
                        GpuTelemetryChannels.OffTimelineDeferred,
                        GpuTelemetryChannels.OffTimelineOutstanding,
                        GpuTelemetryChannels.AcquireWaits,
                        GpuTelemetryChannels.AcquireWaitMs,
                    },
                    StringComparer.Ordinal),
                written);

            _out.WriteLine($"drains={counters.DrainCount}/{counters.DrainMs:F3}ms "
                + $"stalls={counters.BackpressureStallCount}/{counters.BackpressureStallMs:F3}ms "
                + $"offTimeline={counters.OffTimelineDeferred}/{counters.OffTimelineOutstanding}");
        }

        /// <summary>
        /// M-G2's <c>softwareAdapter</c> and M-G4's <c>deviceLossReason</c>, from the device through
        /// <c>WithGpu</c> and into the header JSON, which is the path a soak capture reads and the reason both
        /// fields are live members rather than creation-time arguments.
        /// <para>
        /// <b>THE VALUE IS ASSERTED HERE, WHERE THE VULKAN SIBLING COULD ONLY ASSERT THAT A BOOLEAN SURVIVED.</b>
        /// That one pins lavapipe in CI and would answer true while a developer on a discrete card answers false,
        /// so it compares the header against the device rather than against a literal. Metal has no such
        /// ambiguity: Apple ships no software Metal rasterizer at all, so FALSE is the answer on every machine
        /// this can run on, and pinning the literal is what makes the header distinguishable from the incumbent
        /// Veldrid Metal path, which correctly leaves the field null because it cannot answer.
        /// </para>
        /// </summary>
        [GpuFact]
        public void BothHeaderFieldsReachTheSessionHeader()
        {
            using GpuDeviceContext? native = CreateNativeOrNull();
            if (native is null) return;

            GpuDeviceDiagnostics diagnostics = native.Diagnostics;
            Assert.False(diagnostics.SoftwareAdapter);
            Assert.Null(diagnostics.DeviceLossReason);
            Assert.False(diagnostics.IsDeviceLost);

            var info = new TelemetrySessionInfo().WithGpu(native);
            Assert.Equal(diagnostics.SoftwareAdapter, info.SoftwareAdapter);

            using JsonDocument document = JsonDocument.Parse(
                TelemetrySessionHeader.Build(info, Array.Empty<TelemetryHeaderValue>()));
            JsonElement gpu = document.RootElement.GetProperty("session").GetProperty("gpu");

            // FALSE rather than "whatever the device said", which is M-G2 pinned end to end: a null here would
            // mean nobody asked, and this backend answers with confidence.
            Assert.False(gpu.GetProperty("softwareAdapter").GetBoolean());
            Assert.Equal(JsonValueKind.Null, gpu.GetProperty("deviceLossReason").ValueKind);

            _out.WriteLine($"softwareAdapter={diagnostics.SoftwareAdapter} device='{native.AdapterDescription}'");
        }

        // The machine fact both rows share, through the backend's own functional probe rather than an
        // operating-system check alone. A dormant return rather than a skip: the zero-skipped gate under
        // KE_GPU_TESTS=1 reads a skip as a failure.
        GpuDeviceContext? CreateNativeOrNull()
            => MetalDormancy.NativeDeviceAvailable(_out)
                ? GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative)
                : null;
    }
}
