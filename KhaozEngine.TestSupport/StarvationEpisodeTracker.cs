using System;

namespace KhaozEngine.Tests;

/// <summary>Thread pool counters read at the slowest reading of a starvation episode.</summary>
public readonly record struct PoolCounters(int ThreadCount, long PendingWorkItemCount, long CompletedWorkItemCount);

/// <summary>
/// One stretch of thread pool queue latency at or above the episode threshold. <see cref="StartUtc"/> is when the
/// first slow probe was queued, so it predates the reading that noticed it.
/// </summary>
public sealed record StarvationEpisode(DateTime StartUtc, TimeSpan PeakLatency, PoolCounters CountersAtPeak);

/// <summary>What one reading asks the watchdog to do: capture the dump now, report a closed episode, or neither.</summary>
public readonly record struct StarvationObservation(bool CaptureDump, StarvationEpisode? Closed);

/// <summary>
/// The decision half of <see cref="ThreadPoolStarvationWatchdog"/>, kept free of threads and clocks so it can be
/// driven with made up readings.
///
/// <para>Each reading is either the age of a probe work item that is still queued, or the latency a probe
/// actually ran with. An episode opens on the first reading at or above the episode threshold, stays open across
/// further slow readings, and closes when a probe completes under the threshold, which is the pool servicing work
/// promptly again.</para>
///
/// <para>The one dump this tracker will ever ask for comes on the first slow reading whose probe is STILL QUEUED
/// and which either waited the dump threshold itself or belongs to an episode that has lasted that long. The
/// second half is not optional. A pool held at a steady three or four second wait never produces a single ten
/// second probe, yet a loopback HTTPS request that needs a dozen pool hops times out under it, which is exactly
/// what the local starvation harness showed. A probe that has already completed does not ask, because by then the
/// pool has recovered and the stacks that starved it are gone.</para>
/// </summary>
public sealed class StarvationEpisodeTracker
{
    private readonly TimeSpan episodeThreshold;
    private readonly TimeSpan dumpThreshold;
    private bool dumpRequested;

    /// <param name="episodeThreshold">Latency at which an episode opens. Must be positive.</param>
    /// <param name="dumpThreshold">Probe wait, or episode length, at which the one dump is requested. Must not be below
    /// <paramref name="episodeThreshold"/>.</param>
    public StarvationEpisodeTracker(TimeSpan episodeThreshold, TimeSpan dumpThreshold)
    {
        if (episodeThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(episodeThreshold), episodeThreshold, "must be positive");
        if (dumpThreshold < episodeThreshold)
            throw new ArgumentOutOfRangeException(nameof(dumpThreshold), dumpThreshold, "must not be below the episode threshold");
        this.episodeThreshold = episodeThreshold;
        this.dumpThreshold = dumpThreshold;
    }

    /// <summary>The episode in progress, or null when the pool is keeping up.</summary>
    public StarvationEpisode? Open { get; private set; }

    /// <summary>True when a reading reaches the episode threshold, so the caller knows when counters matter.</summary>
    public bool IsSlow(TimeSpan latency) => latency >= episodeThreshold;

    /// <summary>Feeds one reading.</summary>
    /// <param name="utcNow">When the reading was taken.</param>
    /// <param name="latency">The probe's age if still queued, or the latency it ran with if completed.</param>
    /// <param name="probeCompleted">Whether the probe has run.</param>
    /// <param name="counters">Pool counters at this reading. Only kept when the reading is slow.</param>
    public StarvationObservation Observe(DateTime utcNow, TimeSpan latency, bool probeCompleted, PoolCounters counters)
    {
        if (latency >= episodeThreshold)
        {
            if (Open is null)
                Open = new StarvationEpisode(utcNow - latency, latency, counters);
            else if (latency > Open.PeakLatency)
                Open = Open with { PeakLatency = latency, CountersAtPeak = counters };

            bool sustained = latency >= dumpThreshold || utcNow - Open.StartUtc >= dumpThreshold;
            bool dump = !probeCompleted && !dumpRequested && sustained;
            dumpRequested |= dump;
            return new StarvationObservation(dump, null);
        }

        if (probeCompleted && Open is { } closed)
        {
            Open = null;
            return new StarvationObservation(false, closed);
        }

        return default;
    }
}
