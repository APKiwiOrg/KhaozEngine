using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The bridge from <see cref="GpuDeviceCounters"/> onto the named numeric channels a
    /// <see cref="TelemetryRecorder"/> sample row carries, so a field capture records the soak numbers under
    /// spellings a reader and a test share instead of ones each consumer invents.
    /// <para>
    /// SAMPLE ROWS RATHER THAN THE HEADER, which is the placement decision. The header is written once, at the
    /// start, and describes what was already true then: the build, the backend, the adapter. These numbers are
    /// still zero at that moment and mean nothing there. The sample row is the session's channel for a number that
    /// moves, and because the counters are cumulative (see <see cref="GpuDeviceCounters"/>) a row at any cadence
    /// brackets the window: subtract the first from the last and the answer is exact rather than a sample of the
    /// frames the recorder happened to catch.
    /// </para>
    /// <para>
    /// A DEVICE THAT COUNTED NOTHING WRITES NOTHING. When <see cref="GpuDeviceCounters.HasValue"/> is false the
    /// projection is empty, so a Metal or Vulkan capture carries no stall columns at all rather than columns of
    /// zeros that would read as a clean soak on a backend that never looked.
    /// </para>
    /// <para>
    /// It lives beside <see cref="GpuTelemetry"/> and not inside it because the two are different jobs: that one
    /// fills the header's creation-time identity, this one appends per-sample numbers. Same reason both are here
    /// rather than in <c>KhaozEngine.Diagnostics</c>, which sits UNDER this package and cannot name these types.
    /// </para>
    /// </summary>
    public static class GpuTelemetryChannels
    {
        /// <summary>Channel name for <see cref="GpuDeviceCounters.FramesBegun"/>.</summary>
        public const string FramesBegun = "gpuFramesBegun";

        /// <summary>Channel name for <see cref="GpuDeviceCounters.DrainCount"/>.</summary>
        public const string DrainCount = "gpuDrainCount";

        /// <summary>Channel name for <see cref="GpuDeviceCounters.DrainMs"/>.</summary>
        public const string DrainMs = "gpuDrainMs";

        /// <summary>Channel name for <see cref="GpuDeviceCounters.BackpressureStallCount"/>.</summary>
        public const string BackpressureStalls = "gpuBackpressureStalls";

        /// <summary>Channel name for <see cref="GpuDeviceCounters.BackpressureStallMs"/>.</summary>
        public const string BackpressureStallMs = "gpuBackpressureStallMs";

        /// <summary>
        /// Channel name for <see cref="GpuDeviceCounters.OffTimelineDeferred"/>. Deliberately spelled nothing like
        /// the backpressure channels above, because the two readings answer different questions and a column name
        /// that blurred them is how they would get added together by a reader who had not read either doc.
        /// </summary>
        public const string OffTimelineDeferred = "gpuOffTimelineDeferred";

        /// <summary>Channel name for <see cref="GpuDeviceCounters.OffTimelineOutstanding"/>.</summary>
        public const string OffTimelineOutstanding = "gpuOffTimelineOutstanding";

        /// <summary>
        /// How many channels <see cref="AppendTo"/> writes for a populated counter set, so a caller can size its
        /// list once instead of discovering the number by growing. Spelled out rather than named
        /// <c>Count</c>, which among seven channel-name constants reads as an eighth channel.
        /// </summary>
        public const int ChannelCount = 7;

        /// <summary>
        /// The channels for <paramref name="counters"/>, or an empty list when the device counted nothing. The
        /// convenience form for a caller that samples the GPU counters alone.
        /// </summary>
        public static IReadOnlyList<TelemetryChannel> For(GpuDeviceCounters counters)
        {
            if (!counters.HasValue) return Array.Empty<TelemetryChannel>();

            var channels = new List<TelemetryChannel>(ChannelCount);
            AppendTo(channels, counters);
            return channels;
        }

        /// <summary>
        /// Append <paramref name="counters"/> to <paramref name="channels"/>, appending NOTHING when the device
        /// counted nothing. This is the form a game's frame sampler uses, since its row already carries frame
        /// rate and its own numbers and the GPU counters join them rather than replacing them.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="channels"/> is null.</exception>
        public static void AppendTo(ICollection<TelemetryChannel> channels, GpuDeviceCounters counters)
        {
            ArgumentNullException.ThrowIfNull(channels);
            if (!counters.HasValue) return;

            channels.Add(new TelemetryChannel(FramesBegun, counters.FramesBegun));
            channels.Add(new TelemetryChannel(DrainCount, counters.DrainCount));
            channels.Add(new TelemetryChannel(DrainMs, counters.DrainMs));
            channels.Add(new TelemetryChannel(BackpressureStalls, counters.BackpressureStallCount));
            channels.Add(new TelemetryChannel(BackpressureStallMs, counters.BackpressureStallMs));
            channels.Add(new TelemetryChannel(OffTimelineDeferred, counters.OffTimelineDeferred));
            channels.Add(new TelemetryChannel(OffTimelineOutstanding, counters.OffTimelineOutstanding));
        }
    }
}
