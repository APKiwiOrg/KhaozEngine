using System;
using System.Collections.Generic;
using System.Text.Json;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT A NATIVE VULKAN DEVICE REPORTS ABOUT ITSELF, against a real one: the whole
    /// <see cref="GpuDeviceCounters"/> fill (V-G6), and the two <see cref="GpuDeviceDiagnostics"/> fields reaching
    /// the telemetry session header, which is rollout gate 5's fifth clause. Work-breakdown row 18
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/528).
    ///
    /// <para><b>THE FILL WAS BUILT BY THE ROWS THAT OWN THE SUBSYSTEMS, AND THIS IS WHERE IT IS CHECKED.</b> Each
    /// field is a reading taken off the subsystem that owns it: the drain pair off the timeline (row 5), the
    /// backpressure pair off the accumulator both the command lists and the uniform ring stall into (rows 7 and
    /// 8), the off-timeline pair off the ring's pending patches (row 8), and <c>FramesBegun</c> with the acquire
    /// pair off the present boundary (row 17). Each of those rows tests its own accumulator where it can provoke
    /// the wait deterministically, so what this row adds is narrower and worth stating exactly rather than
    /// generously: <c>HasValue</c> is true on a real device, the three present-boundary fields read zero,
    /// deferred bounds outstanding, nothing is negative, the projection carries the nine expected channel NAMES,
    /// and both diagnostics fields travel the whole path into the session header.</para>
    ///
    /// <para><b>THE HEADLESS ZEROS ARE READINGS RATHER THAN GAPS, AND TELLING THOSE APART IS THE WHOLE POINT OF
    /// <see cref="GpuDeviceCounters.HasValue"/>.</b> A headless device has no swapchain, so it opens no frame at
    /// this seam and has no acquire to wait on, and the three fields that come off the present boundary are
    /// therefore exactly zero and literally true. The hazard that shape creates is named in row 5's own note:
    /// a gate-4 reader can take a zero <c>BackpressureStallCount</c> for an M3 pass. <c>HasValue</c> being true
    /// is what makes a capture carry columns rather than nothing, and the reader's job is then subtraction across
    /// two rows rather than reading one.</para>
    ///
    /// <para><b>WHAT IS DELIBERATELY NOT ASSERTED IS THAT ANY ACCUMULATOR MOVES.</b> The obvious version of that
    /// (submit an empty list, drain, expect <c>DrainCount</c> to rise) is RACY on this backend by design: a drain
    /// that finds the timeline counter already past the last submitted value is not counted, which is the seam's
    /// own rule honoured literally, and on a software rasterizer an empty submission can complete before the
    /// drain is entered. A flaky assertion on the one leg that runs it is worse than no assertion, and the
    /// subsystems that own those accumulators have their own tests where the wait is provoked deterministically.
    /// </para>
    ///
    /// <para><b>DORMANT UNTIL A LEG HAS A VULKAN DEVICE.</b> Nothing on the current legs can create one and this
    /// developer machine has no loader at all, so this first RUNS on the <c>vulkan-native</c> leg row 19
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/529) installs, against lavapipe. The early return is a
    /// machine fact read off the backend's own functional probe, the shape #504 settled.
    /// <b>That leg must set <c>KE_VULKAN_REQUIRED=1</c></b>, which turns the probe's refusal into a throw naming
    /// what it objected to (<see cref="VulkanDormancy"/>). Without it, a loader regression there empties both
    /// rows into passes that asserted nothing, and a dormant row does not skip, so the zero-skipped gate never
    /// sees it. Unset, everywhere else, both rows go dormant exactly as before.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class VulkanCountersAndHeaderGpuTests
    {
        readonly ITestOutputHelper _out;
        public VulkanCountersAndHeaderGpuTests(ITestOutputHelper o) => _out = o;

        /// <summary>
        /// Every field of the fill, off a real device. Nine channels reach a sample row, the three present-boundary
        /// fields read zero on a device with no swapchain, and nothing is negative, which on a pair of accumulators
        /// summing stopwatch deltas is the shape a transposed count-and-milliseconds argument would break.
        /// </summary>
        [GpuFact]
        public void TheCounterFillIsNineReadingsAndNotAnAbsence()
        {
            using GpuDeviceContext? native = CreateNativeOrNull();
            if (native is null) return;

            GpuDeviceCounters counters = native.Counters;

            Assert.True(counters.HasValue,
                "a native Vulkan device counts, so reporting the default value would say nobody looked.");

            Assert.Equal(0, counters.FramesBegun);
            Assert.Equal(0, counters.AcquireWaitCount);
            Assert.Equal(0d, counters.AcquireWaitMs);

            Assert.True(counters.DrainCount >= 0);
            Assert.True(counters.DrainMs >= 0d);
            Assert.True(counters.BackpressureStallCount >= 0);
            Assert.True(counters.BackpressureStallMs >= 0d);
            Assert.True(counters.OffTimelineDeferred >= 0);
            Assert.True(counters.OffTimelineOutstanding >= 0);
            Assert.True(counters.OffTimelineOutstanding <= counters.OffTimelineDeferred,
                "a patch cannot still be outstanding without having been deferred first.");

            // THE NAMES, NOT THE COUNT. Once HasValue is true the count is a tautology about the projection
            // (GpuTelemetryChannels.For appends nine unconditionally), so it says nothing about THIS device. The
            // name set is the question a gate-4 reader actually has: which columns does a capture off this
            // backend carry. Order-insensitive on purpose, since a row is read by name.
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
        /// V-G2's <c>softwareAdapter</c> and V-G4's <c>deviceLossReason</c>, from the device through
        /// <c>WithGpu</c> and into the header JSON, which is the path gate 5 reads and the reason both fields are
        /// live members rather than creation-time arguments. The flag's VALUE is not asserted, because CI pins
        /// lavapipe and would answer true while a developer on a discrete card answers the same question
        /// correctly with false. What is asserted is that a real boolean survives the whole path: the header's
        /// <c>softwareAdapter</c> is read with <c>GetBoolean</c> and compared to the device's own field, so a
        /// backend that never looked lands a JSON null there and that read throws. An
        /// <c>Assert.NotNull</c> on the field would say nothing, since this device fills it from a
        /// non-nullable <c>bool</c>.
        /// </summary>
        [GpuFact]
        public void BothHeaderFieldsReachTheSessionHeader()
        {
            using GpuDeviceContext? native = CreateNativeOrNull();
            if (native is null) return;

            GpuDeviceDiagnostics diagnostics = native.Diagnostics;
            Assert.Null(diagnostics.DeviceLossReason);
            Assert.False(diagnostics.IsDeviceLost);

            var info = new TelemetrySessionInfo().WithGpu(native);
            Assert.Equal(diagnostics.SoftwareAdapter, info.SoftwareAdapter);

            using JsonDocument document = JsonDocument.Parse(
                TelemetrySessionHeader.Build(info, Array.Empty<TelemetryHeaderValue>()));
            JsonElement gpu = document.RootElement.GetProperty("session").GetProperty("gpu");

            Assert.Equal(diagnostics.SoftwareAdapter, gpu.GetProperty("softwareAdapter").GetBoolean());
            Assert.Equal(JsonValueKind.Null, gpu.GetProperty("deviceLossReason").ValueKind);

            _out.WriteLine($"softwareAdapter={diagnostics.SoftwareAdapter} device='{native.AdapterDescription}'");
        }

        // The one machine fact both rows share: a box the native backend's own functional probe refuses has no
        // device to ask, which on every leg but the one row 19 builds is every box. Read through the probe rather
        // than through an operating-system check, because Vulkan is not a Windows API and V-P1 leaves the whole
        // question to the probe. On the leg that DOES have a device, KE_VULKAN_REQUIRED=1 turns the refusal into
        // a throw rather than a quiet dormancy (VulkanDormancy).
        GpuDeviceContext? CreateNativeOrNull()
        {
            if (!VulkanDormancy.NativeDeviceAvailable())
            {
                _out.WriteLine("dormant: this machine cannot run the native Vulkan backend.");
                return null;
            }
            return GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative);
        }
    }
}
