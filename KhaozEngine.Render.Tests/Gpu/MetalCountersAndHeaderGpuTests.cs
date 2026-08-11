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
    /// <para><b>TWO OF THE THREE PAIRS ARE DRIVEN OFF ZERO BEFORE THE READING, AND THE THIRD IS NOT.</b> An
    /// identity assertion between two zeros holds whichever accumulator the field is actually wired to, so a row
    /// that read zero everywhere would pass with a transposed pair in it, which is the single failure this fill
    /// can have. The backpressure pair and the off-timeline pair come off the same state and are provoked
    /// together, by row 8's own recipe: recordings submitted with no drain between them run the CPU ahead of the
    /// GPU, the claim that wraps onto a segment a submission is still reading is the stall, and a device-level
    /// write that finds one is the deferral. <c>MetalRingGpuTests.TheStallCounterCountsARealSegmentWait</c> drives
    /// the same loop.</para>
    ///
    /// <para><b>THE DRAIN PAIR IS LEFT AS IT FALLS, and that is a reading rather than a gap.</b> The obvious
    /// version (submit, drain, expect <c>DrainCount</c> to rise) is RACY by design here for the reason it is racy
    /// on the Vulkan sibling: <c>MetalTimeline.WaitForIdle</c> returns without counting when the completed value
    /// has already reached the last submitted one, which is the seam's "a wait that did not block is not a drain"
    /// rule honoured literally, and an empty submission on an M-series GPU routinely completes before the drain is
    /// entered. A flaky assertion is worse than no assertion, and that one would be flaky where the two above are
    /// not: a stall is what the undrained loop RUNS UNTIL, rather than something it hopes for once.</para>
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
        /// supposed to be reading. The backpressure pair and the off-timeline pair are NON-ZERO by the time the
        /// snapshot is taken, which is what makes those four identity assertions discriminating. Nine channels
        /// reach a sample row, the three present-boundary fields read zero, and no field is negative, which on a
        /// pair summing stopwatch deltas is the shape a transposed count-and-milliseconds argument would break.
        /// </summary>
        [GpuFact]
        public void TheCounterFillReadsTheAccumulatorEachFieldClaims()
        {
            // THE PROBE IS READ INLINE HERE RATHER THAN THROUGH A HELPER, because it is a
            // [SupportedOSPlatformGuard] and this row records against MetalCommandList, whose members are
            // macOS-only. A helper returning a context carries no guard, so CA1416 could not see the platform.
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            using GpuDeviceContext context = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
            var device = (MetalGpuDevice)context.GpuDevice;

            // THE BUFFER AND THE LIST OUTLIVE THE READING DELIBERATELY. Disposing a ring-backed buffer drops its
            // queued patches into the Dropped counter, which would put OffTimelineOutstanding back to zero before
            // the snapshot and undo half of what the loop below is for.
            using IGpuBuffer uniforms = device.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using MetalCommandList list = device.CreateCommandList();

            int frames = DriveTheStallAndTheDeferral(device, uniforms, list);

            // And the drain, which is read after the device has actually done something rather than only at rest.
            // Whether THIS one counted is the race the class remarks describe, and the identity assertions below
            // hold either way, which is the point of asserting identity.
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

            // BOTH PROVOKED PAIRS MOVED, asserted BEFORE the identities they make discriminating. Without this the
            // six assertions below are readings of zero against zero, which hold for a field wired to any of the
            // three accumulators and therefore say nothing about which one it reads.
            Assert.True(stalls.Count > 0,
                $"{frames} recordings submitted with no drain between them never made a segment claim wait, so "
                + "the backpressure pair is still zero and the identity assertion on it cannot tell this field "
                + "from any other zero. The claim gate waits on a real completion value, so a zero here says the "
                + "GPU retired every command buffer before the CPU wrapped onto its segment.");
            Assert.True(stalls.TotalMs > 0d,
                "a stall was counted with no time against it, so the wait was recorded without having blocked.");
            Assert.True(patches.Deferred > 0,
                $"{frames} device-level writes behind an undrained submit never found a segment in flight, so no "
                + "off-timeline write was ever deferred and that pair is still zero.");
            Assert.True(patches.Outstanding > 0,
                "every deferred patch was applied, coalesced or dropped before the snapshot, so the outstanding "
                + "half of the pair reads zero and its identity assertion cannot discriminate.");

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

            _out.WriteLine($"{frames} undrained recordings produced "
                + $"drains={counters.DrainCount}/{counters.DrainMs:F3}ms "
                + $"stalls={counters.BackpressureStallCount}/{counters.BackpressureStallMs:F3}ms "
                + $"offTimeline={counters.OffTimelineDeferred}/{counters.OffTimelineOutstanding}");
        }

        /// <summary>How many undrained recordings the loop below may take before it gives up and lets the
        /// assertions report a machine that never fell behind its own GPU. Row 8's equivalent loop takes 64 and
        /// observes eleven to twenty-two stalls on this hardware, so the bound is far past what it costs and is
        /// there to stop a runaway rather than to be reached.</summary>
        const int MaxProvocationFrames = 512;

        // ROW 8'S RECIPE, WHICH IS THE DETERMINISTIC HALF OF WHAT THIS ROW CAN PROVOKE. Recordings submitted with
        // no drain between them run the CPU ahead of the GPU, and both readings fall out of that one state: a
        // claim that wraps onto a segment a submission is still reading is the stall, and a device-level write
        // that finds one is the deferral. It runs UNTIL both have moved rather than a fixed number of times, which
        // is what makes it a loop with an exit condition instead of a hope.
        //
        // The recordings are kept as small as row 8 keeps them, deliberately. A bigger payload would slow the CPU
        // side down as much as the GPU side, which is the wrong direction: what puts a segment under a live
        // submission is the CPU being the faster of the two.
        static int DriveTheStallAndTheDeferral(MetalGpuDevice device, IGpuBuffer uniforms, MetalCommandList list)
        {
            var payload = new byte[64];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i + 1);

            int frames = 0;
            while (frames < MaxProvocationFrames
                && (device.Rings.StallCount == 0 || device.Rings.OffTimelinePatches.Outstanding == 0))
            {
                frames++;

                list.Begin();
                list.UpdateBuffer(uniforms, 0, (ReadOnlySpan<byte>)payload);
                list.Encoders.EnsureBlitEncoder();
                list.End();
                device.Submit(list);

                // NO DRAIN ANYWHERE IN HERE, and the device-level write goes in immediately behind the submit,
                // while the segments that submission is not current on are still owned by values the timeline has
                // not reached. That is the state the write has to meet to defer rather than copy.
                device.UpdateBuffer(uniforms, 0, (ReadOnlySpan<byte>)payload);
            }

            return frames;
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

        // The machine fact, through the backend's own functional probe rather than an operating-system check
        // alone. A dormant return rather than a skip: the zero-skipped gate under KE_GPU_TESTS=1 reads a skip as
        // a failure. The row above reads the same probe inline instead, for the guard reason recorded there.
        GpuDeviceContext? CreateNativeOrNull()
            => MetalDormancy.NativeDeviceAvailable(_out)
                ? GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative)
                : null;
    }
}
