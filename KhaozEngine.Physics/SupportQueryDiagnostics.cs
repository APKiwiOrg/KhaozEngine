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
    int ModeSegments)
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
        a.ModeSegments - b.ModeSegments);
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

    internal static void Enable(bool enabled)
    {
        if (enabled) t_counts ??= new int[24];
        t_enabled = enabled;
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
            c[23]);
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
