using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Opt-in counters owned by the executing thread. Installation is outside measured intervals.</summary>
internal readonly record struct SupportGroundObservation(Vector3 Feet, float Height, float HeightError, bool Supported);

internal readonly record struct SupportFindObservation(Vector2 Axis, float FeetY, float Radius,
    float ReachUp, float ReachDown, float CosMaxSlope, byte Status, float Height, float HeightError);

internal enum SupportQueryRoute
{
    WallRetry, PushedPlacement, MediumResolve, Ground, Slide, Air, Water, RoundedRead, MediumHandoff,
}

internal readonly record struct SupportRouteCounts(long WallRetries, long PushedPlacements, long MediumResolves,
    long MaximumMediumRetries, long GroundSegments, long SlideSegments, long AirSegments, long WaterSegments,
    long RoundedReads, long MediumHandoffs)
{
    public static SupportRouteCounts operator +(SupportRouteCounts a, SupportRouteCounts b) => new(
        checked(a.WallRetries + b.WallRetries), checked(a.PushedPlacements + b.PushedPlacements),
        checked(a.MediumResolves + b.MediumResolves), Math.Max(a.MaximumMediumRetries, b.MaximumMediumRetries),
        checked(a.GroundSegments + b.GroundSegments), checked(a.SlideSegments + b.SlideSegments),
        checked(a.AirSegments + b.AirSegments), checked(a.WaterSegments + b.WaterSegments),
        checked(a.RoundedReads + b.RoundedReads), checked(a.MediumHandoffs + b.MediumHandoffs));
}

internal static partial class SupportQueryDiagnostics
{
    [ThreadStatic] static bool t_enabled;
    [ThreadStatic] static long[]? t_counts;
    [ThreadStatic] static Action? t_begin;
    [ThreadStatic] static Action? t_end;
    [ThreadStatic] static SupportGroundObservation t_ground;
    [ThreadStatic] static Action<SupportFindObservation>? t_find;
    [ThreadStatic] static SupportRouteCounts t_routes;
    [ThreadStatic] static Action<SupportCurvedWorkObservation>? t_curvedWork;

    [ThreadStatic] static string? t_measurementLabel;
    [ThreadStatic] static bool t_hasCompleteLifetimes;

    internal static string? MeasurementLabel => t_measurementLabel;
    internal static bool HasCompleteLifetimes => t_hasCompleteLifetimes && t_retention.Keys == 0 && t_retention.Bytes == 0;
    internal static SupportRouteCounts Routes => t_routes;
    internal static void SetFindObserver(Action<SupportFindObservation>? observer) => t_find = observer;
    internal static void ObserveFind(in SupportFindObservation observation)
    {
        if (t_enabled) t_find?.Invoke(observation);
    }
    internal static void SetCurvedWorkObserver(Action<SupportCurvedWorkObservation>? observer) => t_curvedWork = observer;
    internal static void ObserveCurvedWork(in SupportCurvedWorkObservation observation)
    {
        if (t_enabled) t_curvedWork?.Invoke(observation);
    }
    internal static void MediumRetryMaximum(int count)
    {
        if (t_enabled && count > t_routes.MaximumMediumRetries)
            t_routes = t_routes with { MaximumMediumRetries = count };
    }
    internal static void Route(SupportQueryRoute route)
    {
        if (!t_enabled) return;
        t_routes = route switch
        {
            SupportQueryRoute.WallRetry => t_routes with { WallRetries = checked(t_routes.WallRetries + 1) },
            SupportQueryRoute.PushedPlacement => t_routes with { PushedPlacements = checked(t_routes.PushedPlacements + 1) },
            SupportQueryRoute.MediumResolve => t_routes with { MediumResolves = checked(t_routes.MediumResolves + 1) },
            SupportQueryRoute.Ground => t_routes with { GroundSegments = checked(t_routes.GroundSegments + 1) },
            SupportQueryRoute.Slide => t_routes with { SlideSegments = checked(t_routes.SlideSegments + 1) },
            SupportQueryRoute.Air => t_routes with { AirSegments = checked(t_routes.AirSegments + 1) },
            SupportQueryRoute.Water => t_routes with { WaterSegments = checked(t_routes.WaterSegments + 1) },
            SupportQueryRoute.RoundedRead => t_routes with { RoundedReads = checked(t_routes.RoundedReads + 1) },
            SupportQueryRoute.MediumHandoff => t_routes with { MediumHandoffs = checked(t_routes.MediumHandoffs + 1) },
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
    }

    internal static SupportGroundObservation Ground => t_ground;
    internal static void GroundState(Vector3 feet, float height, float error, bool supported)
    {
        if (t_enabled) t_ground = new(feet, height, error, supported);
    }

    internal static bool Enabled => t_enabled;

    internal static void Enable(bool enabled)
    {
        if (enabled) t_counts ??= new long[CounterCount];
        t_enabled = enabled;
    }

    const int CounterCount = 52;

    /// <summary>Every thread-local counter, observation and callback of one thread.</summary>
    internal readonly record struct ThreadState(bool Enabled, long[]? Counts, Action? Begin, Action? End,
        SupportGroundObservation Ground, Action<SupportFindObservation>? Find, SupportRouteCounts Routes,
        Action<SupportCurvedWorkObservation>? CurvedWork = null, SupportProofReuseRetention Retention = default,
        string? MeasurementLabel = null, bool HasCompleteLifetimes = true)
    {
        /// <summary>An empty recorder with the given enablement and no callbacks.</summary>
        internal static ThreadState Fresh(bool enabled) =>
            new(enabled, enabled ? new long[CounterCount] : null, null, null, default, null, default, null,
                new(t_retention.Keys, t_retention.Bytes, 0, 0), null, t_retention.Keys == 0 && t_retention.Bytes == 0);
    }

    /// <summary>Installs <paramref name="next"/> on this thread and returns the state it replaced.</summary>
    internal static ThreadState Exchange(in ThreadState next)
    {
        var prior = new ThreadState(t_enabled, t_counts, t_begin, t_end, t_ground, t_find, t_routes, t_curvedWork, t_retention,
            t_measurementLabel, t_hasCompleteLifetimes);
        t_enabled = next.Enabled;
        t_counts = next.Counts;
        t_begin = next.Begin;
        t_end = next.End;
        t_ground = next.Ground;
        t_find = next.Find;
        t_curvedWork = next.CurvedWork;
        t_routes = next.Routes;
        t_retention = next.Retention;
        t_measurementLabel = next.MeasurementLabel;
        t_hasCompleteLifetimes = next.HasCompleteLifetimes;
        return prior;
    }

    /// <summary>Adds another thread's counters and routes to this thread's recorder when it is enabled. Route
    /// counts add, and the maximum medium retry count keeps the larger value.</summary>
    internal static void Merge(in SupportQueryCounts counts, in SupportRouteCounts routes)
    {
        if (!t_enabled) return;
        SupportQueryCounts merged = Snapshot() + counts;
        SupportRouteCounts mergedRoutes = t_routes + routes;
        for (int i = 0; i < CounterCount; i++) t_counts![i] = merged[(SupportQueryCounter)i];
        t_routes = mergedRoutes;
    }

    internal static void Reset()
    {
        if (t_counts is { } counts) Array.Clear(counts);
        t_routes = default;
        if (t_retention.Keys == 0 && t_retention.Bytes == 0) t_retention = default;
    }

    internal static void Record(SupportQueryCounter counter, int count = 1)
    {
        if (t_enabled) t_counts![(int)counter] = checked(t_counts[(int)counter] + count);
    }

    internal static SupportQueryCounts Snapshot()
    {
        if (t_counts is not { } c) return default;
        return new(
            c[0],
            c[1],
            c[2],
            c[3],
            c[4],
            c[5],
            c[6],
            c[7],
            c[8],
            c[9],
            c[10],
            c[11],
            c[12],
            c[13],
            c[14],
            c[15],
            c[16],
            c[17],
            c[18],
            c[19],
            c[20],
            c[21],
            c[22],
            c[23],
            c[24],
            c[25],
            c[26],
            c[27],
            c[28],
            c[29],
            c[30],
            c[31],
            c[32],
            c[33],
            c[34],
            c[35],
            c[36],
            c[37],
            c[38],
            c[39],
            c[40],
            c[41],
            c[42],
            c[43],
            c[44],
            c[45],
            c[46],
            c[47],
            c[48],
            c[49],
            c[50],
            c[51]);
    }

    internal static void SetGroundSubstepObserver(Action? begin, Action? end)
    {
        t_begin = begin;
        t_end = end;
    }

    internal static void BeginGroundSubstep()
    {
        if (!t_enabled) return;
        Record(SupportQueryCounter.GroundSubsteps);
        t_begin?.Invoke();
    }

    internal static void EndGroundSubstep()
    {
        if (t_enabled) t_end?.Invoke();
    }

    internal static void Refusal(CapsuleFeatureStatus status)
    {
        if (status == CapsuleFeatureStatus.Unsupported) Record(SupportQueryCounter.Unsupported);
        else if (status == CapsuleFeatureStatus.Ambiguous) Record(SupportQueryCounter.Ambiguous);
        else if (status == CapsuleFeatureStatus.CapacityExceeded) Record(SupportQueryCounter.CapacityExceeded);
    }
}
