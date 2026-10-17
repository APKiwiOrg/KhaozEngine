using System;
using System.Numerics;

namespace KhaozEngine.Physics;

internal enum SupportQueryCounter
{
    Find,
    Proposals,
    Volumes,
    SelectedStatics,
    MeshLeaves,
    Polygons,
    Members,
    Extrema,
    IntervalFallbacks,
    ExactFallbacks,
    JoinPairs,
    JoinsProved,
    JoinsPruned,
    Unsupported,
    Ambiguous,
    CapacityExceeded,
    AnalyticSamples,
    Bindings,
    PhysicsLeases,
    MovementLeases,
    ProducerPins,
    GroundSubsteps,
    MediumRetries,
    ModeSegments,
    /// <summary>Installed support world forms built, published or not.</summary>
    WorldFormBuilds,
    /// <summary>Footprint polygons that passed the bounds and column reject and began per-query operands.</summary>
    PolygonOperands,
    /// <summary>Contact members the certified separation prefilter decided negative without the contact kernel.</summary>
    PrefilterRejects,
    /// <summary>Joins of two faces of one installed convex leaf whose meeting along a captured shared edge came
    /// from the world form's topology. <see cref="JoinsProved"/> counts the geometric joins only.</summary>
    TopologyJoins,
    /// <summary>Interval samples of the curved tangent contact kernel, each one closest point evaluation over a
    /// height span or at one height of a probe axis.</summary>
    TangentSamples,
    /// <summary>Member pairs a foot support footprint query left unjoined because support certification never reads
    /// their join. <see cref="JoinPairs"/> counts the pairs it evaluated.</summary>
    JoinsSkipped,
}

internal readonly record struct SupportQueryCounts(
    int Find,
    int Proposals,
    int Volumes,
    int SelectedStatics,
    int MeshLeaves,
    int Polygons,
    int Members,
    int Extrema,
    int IntervalFallbacks,
    int ExactFallbacks,
    int JoinPairs,
    int JoinsProved,
    int JoinsPruned,
    int Unsupported,
    int Ambiguous,
    int CapacityExceeded,
    int AnalyticSamples,
    int Bindings,
    int PhysicsLeases,
    int MovementLeases,
    int ProducerPins,
    int GroundSubsteps,
    int MediumRetries,
    int ModeSegments,
    int WorldFormBuilds,
    int PolygonOperands,
    int PrefilterRejects,
    int TopologyJoins,
    int TangentSamples,
    int JoinsSkipped)
{
    public static SupportQueryCounts operator -(SupportQueryCounts a, SupportQueryCounts b) => new(
        a.Find - b.Find,
        a.Proposals - b.Proposals,
        a.Volumes - b.Volumes,
        a.SelectedStatics - b.SelectedStatics,
        a.MeshLeaves - b.MeshLeaves,
        a.Polygons - b.Polygons,
        a.Members - b.Members,
        a.Extrema - b.Extrema,
        a.IntervalFallbacks - b.IntervalFallbacks,
        a.ExactFallbacks - b.ExactFallbacks,
        a.JoinPairs - b.JoinPairs,
        a.JoinsProved - b.JoinsProved,
        a.JoinsPruned - b.JoinsPruned,
        a.Unsupported - b.Unsupported,
        a.Ambiguous - b.Ambiguous,
        a.CapacityExceeded - b.CapacityExceeded,
        a.AnalyticSamples - b.AnalyticSamples,
        a.Bindings - b.Bindings,
        a.PhysicsLeases - b.PhysicsLeases,
        a.MovementLeases - b.MovementLeases,
        a.ProducerPins - b.ProducerPins,
        a.GroundSubsteps - b.GroundSubsteps,
        a.MediumRetries - b.MediumRetries,
        a.ModeSegments - b.ModeSegments,
        a.WorldFormBuilds - b.WorldFormBuilds,
        a.PolygonOperands - b.PolygonOperands,
        a.PrefilterRejects - b.PrefilterRejects,
        a.TopologyJoins - b.TopologyJoins,
        a.TangentSamples - b.TangentSamples,
        a.JoinsSkipped - b.JoinsSkipped);
}

/// <summary>Opt-in counters owned by the executing thread. Installation is outside measured intervals.</summary>
internal readonly record struct SupportGroundObservation(Vector3 Feet, float Height, float HeightError, bool Supported);

internal readonly record struct SupportFindObservation(Vector2 Axis, float FeetY, float Radius,
    float ReachUp, float ReachDown, float CosMaxSlope, byte Status, float Height, float HeightError);

internal enum SupportQueryRoute
{
    WallRetry, PushedPlacement, MediumResolve, Ground, Slide, Air, Water, RoundedRead, MediumHandoff,
}

internal readonly record struct SupportRouteCounts(int WallRetries, int PushedPlacements, int MediumResolves,
    int MaximumMediumRetries, int GroundSegments, int SlideSegments, int AirSegments, int WaterSegments,
    int RoundedReads, int MediumHandoffs);

internal static class SupportQueryDiagnostics
{
    [ThreadStatic] static bool t_enabled;
    [ThreadStatic] static int[]? t_counts;
    [ThreadStatic] static Action? t_begin;
    [ThreadStatic] static Action? t_end;
    [ThreadStatic] static SupportGroundObservation t_ground;
    [ThreadStatic] static Action<SupportFindObservation>? t_find;
    [ThreadStatic] static SupportRouteCounts t_routes;

    internal static SupportRouteCounts Routes => t_routes;
    internal static void SetFindObserver(Action<SupportFindObservation>? observer) => t_find = observer;
    internal static void ObserveFind(in SupportFindObservation observation)
    {
        if (t_enabled) t_find?.Invoke(observation);
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
            SupportQueryRoute.WallRetry => t_routes with { WallRetries = t_routes.WallRetries + 1 },
            SupportQueryRoute.PushedPlacement => t_routes with { PushedPlacements = t_routes.PushedPlacements + 1 },
            SupportQueryRoute.MediumResolve => t_routes with { MediumResolves = t_routes.MediumResolves + 1 },
            SupportQueryRoute.Ground => t_routes with { GroundSegments = t_routes.GroundSegments + 1 },
            SupportQueryRoute.Slide => t_routes with { SlideSegments = t_routes.SlideSegments + 1 },
            SupportQueryRoute.Air => t_routes with { AirSegments = t_routes.AirSegments + 1 },
            SupportQueryRoute.Water => t_routes with { WaterSegments = t_routes.WaterSegments + 1 },
            SupportQueryRoute.RoundedRead => t_routes with { RoundedReads = t_routes.RoundedReads + 1 },
            SupportQueryRoute.MediumHandoff => t_routes with { MediumHandoffs = t_routes.MediumHandoffs + 1 },
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
        if (enabled) t_counts ??= new int[CounterCount];
        t_enabled = enabled;
    }

    const int CounterCount = 30;

    /// <summary>Every thread-local counter, observation and callback of one thread.</summary>
    internal readonly record struct ThreadState(bool Enabled, int[]? Counts, Action? Begin, Action? End,
        SupportGroundObservation Ground, Action<SupportFindObservation>? Find, SupportRouteCounts Routes)
    {
        /// <summary>An empty recorder with the given enablement and no callbacks.</summary>
        internal static ThreadState Fresh(bool enabled) =>
            new(enabled, enabled ? new int[CounterCount] : null, null, null, default, null, default);
    }

    /// <summary>Installs <paramref name="next"/> on this thread and returns the state it replaced.</summary>
    internal static ThreadState Exchange(in ThreadState next)
    {
        var prior = new ThreadState(t_enabled, t_counts, t_begin, t_end, t_ground, t_find, t_routes);
        t_enabled = next.Enabled;
        t_counts = next.Counts;
        t_begin = next.Begin;
        t_end = next.End;
        t_ground = next.Ground;
        t_find = next.Find;
        t_routes = next.Routes;
        return prior;
    }

    /// <summary>Adds another thread's counters and routes to this thread's recorder when it is enabled. Route
    /// counts add, and the maximum medium retry count keeps the larger value.</summary>
    internal static void Merge(in SupportQueryCounts counts, in SupportRouteCounts routes)
    {
        if (!t_enabled) return;
        int[] c = t_counts!;
        c[(int)SupportQueryCounter.Find] += counts.Find;
        c[(int)SupportQueryCounter.Proposals] += counts.Proposals;
        c[(int)SupportQueryCounter.Volumes] += counts.Volumes;
        c[(int)SupportQueryCounter.SelectedStatics] += counts.SelectedStatics;
        c[(int)SupportQueryCounter.MeshLeaves] += counts.MeshLeaves;
        c[(int)SupportQueryCounter.Polygons] += counts.Polygons;
        c[(int)SupportQueryCounter.Members] += counts.Members;
        c[(int)SupportQueryCounter.Extrema] += counts.Extrema;
        c[(int)SupportQueryCounter.IntervalFallbacks] += counts.IntervalFallbacks;
        c[(int)SupportQueryCounter.ExactFallbacks] += counts.ExactFallbacks;
        c[(int)SupportQueryCounter.JoinPairs] += counts.JoinPairs;
        c[(int)SupportQueryCounter.JoinsProved] += counts.JoinsProved;
        c[(int)SupportQueryCounter.JoinsPruned] += counts.JoinsPruned;
        c[(int)SupportQueryCounter.Unsupported] += counts.Unsupported;
        c[(int)SupportQueryCounter.Ambiguous] += counts.Ambiguous;
        c[(int)SupportQueryCounter.CapacityExceeded] += counts.CapacityExceeded;
        c[(int)SupportQueryCounter.AnalyticSamples] += counts.AnalyticSamples;
        c[(int)SupportQueryCounter.Bindings] += counts.Bindings;
        c[(int)SupportQueryCounter.PhysicsLeases] += counts.PhysicsLeases;
        c[(int)SupportQueryCounter.MovementLeases] += counts.MovementLeases;
        c[(int)SupportQueryCounter.ProducerPins] += counts.ProducerPins;
        c[(int)SupportQueryCounter.GroundSubsteps] += counts.GroundSubsteps;
        c[(int)SupportQueryCounter.MediumRetries] += counts.MediumRetries;
        c[(int)SupportQueryCounter.ModeSegments] += counts.ModeSegments;
        c[(int)SupportQueryCounter.WorldFormBuilds] += counts.WorldFormBuilds;
        c[(int)SupportQueryCounter.PolygonOperands] += counts.PolygonOperands;
        c[(int)SupportQueryCounter.PrefilterRejects] += counts.PrefilterRejects;
        c[(int)SupportQueryCounter.TopologyJoins] += counts.TopologyJoins;
        c[(int)SupportQueryCounter.TangentSamples] += counts.TangentSamples;
        c[(int)SupportQueryCounter.JoinsSkipped] += counts.JoinsSkipped;
        t_routes = new(t_routes.WallRetries + routes.WallRetries, t_routes.PushedPlacements + routes.PushedPlacements,
            t_routes.MediumResolves + routes.MediumResolves, Math.Max(t_routes.MaximumMediumRetries, routes.MaximumMediumRetries),
            t_routes.GroundSegments + routes.GroundSegments, t_routes.SlideSegments + routes.SlideSegments,
            t_routes.AirSegments + routes.AirSegments, t_routes.WaterSegments + routes.WaterSegments,
            t_routes.RoundedReads + routes.RoundedReads, t_routes.MediumHandoffs + routes.MediumHandoffs);
    }

    internal static void Reset()
    {
        if (t_counts is { } counts) Array.Clear(counts);
        t_routes = default;
    }

    internal static void Record(SupportQueryCounter counter, int count = 1)
    {
        if (t_enabled) t_counts![(int)counter] += count;
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
            c[29]);
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
