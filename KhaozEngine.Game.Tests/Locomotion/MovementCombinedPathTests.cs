using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public class MovementCombinedPathTests
{
    static readonly Vector3 Sentinel = new(91, 92, 93);
    static readonly AnalyticBox Bounds = new(new(-16), new(16));
    static MovementBodyQuery Body(float height = 0.75f) => new(new(0, 1, 0), 0.25f, height, Space("room"), null);

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.75f)]
    public void KnownDryMovementProvesBothGeometriesForBothBodyProfiles(float halfHeight)
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", Bounds)], []);
        using var lease = scene.Acquire("room");
        var traces = Observe(scene);
        var body = Body(halfHeight);
        var result = Resolve(body, new(3, 0, 1), lease);
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.Equal(new Vector3(3, 1, 1), result.Path[result.Count - 1]);
        Assert.Contains(traces, query => query.Delta == Vector3.Zero);
        AssertTraced(body, result, traces);
    }

    [Fact]
    public void SolidSlideRetracesEveryAcceptedPrefixAndRemainderAgainstWater()
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", Bounds)], []);
        scene.Physics.AddStatic(new BoxShape(new(0.03125f, 3, 8)), Pose.At(new(2, 0, 0)));
        using var lease = scene.Acquire("room");
        var traces = Observe(scene);
        var result = Resolve(Body(), new(4, 0, 2), lease);
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.True(result.Blocked);
        Assert.True(result.Count >= 2);
        Assert.InRange(result.Path[result.Count - 1].Z, 1.999f, 2.001f);
        AssertTraced(Body(), result, traces);
    }

    [Fact]
    public void MissingMediumAfterASolidPrefixDiscardsTheEntireTentativePath()
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", Bounds)], []);
        scene.Physics.AddStatic(new BoxShape(new(0.03125f, 3, 8)), Pose.At(new(2, 0, 0)));
        using var lease = scene.Acquire("room");
        var real = scene.Acquisition.OnCoverage!;
        bool reachedCorrection = false;
        scene.Acquisition.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            if (query.Body.Centre.X > 0.1f)
            {
                reachedCorrection = true;
                return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, Identity);
            }
            return real(query, spans, contacts);
        };
        var result = Resolve(Body(), new(4, 0, 2), lease);
        Assert.True(reachedCorrection);
        AssertRefused(result, MovementAvailability.Unresolved);
    }

    [Fact]
    public void DryEndpointsCannotHideAThinInteriorWetInterval()
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", Bounds)],
            [new Water("thin", "room", new(new(2, -2, -8), new(2.05f, 1, 8)), 1)]);
        using var lease = scene.Acquire("room");
        Assert.False(lease.SampleCentreWater(Body()).InWater);
        var end = new MovementBodyQuery(new(4, 1, 0), 0.25f, 0.75f, Space("room"), null);
        Assert.False(lease.SampleCentreWater(end).InWater);
        // A local interval alone cannot grant a whole-region wet policy. Refuse without a prefix.
        AssertWetRefused(scene, lease, new(4, 0, 0));
    }

    [Fact]
    public void WetOuterCoverageIsNotOverriddenByADryCentre()
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", Bounds)],
            [new Water("edge", "room", new(new(0.2f, -2, -8), new(8, 1, 8)), 1)]);
        using var lease = scene.Acquire("room");
        Assert.False(lease.SampleCentreWater(Body()).InWater);
        AssertWetRefused(scene, lease, Vector3.UnitZ);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(2f)]
    public void InitialWetCoverageIsNotInventedAsASafeZeroTimePlacement(float distance)
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", Bounds)],
            [new Water("lake", "room", new(new(-8, -8, -8), new(8, 2, 8)), 2)]);
        using var lease = scene.Acquire("room");
        AssertWetRefused(scene, lease, Vector3.UnitX * distance);
    }

    [Fact]
    public void WaterBelowADryBridgeDoesNotRefuseVerticalTravelAboveIt()
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", Bounds)],
            [new Water("lake", "room", new(new(-8, -8, -8), new(8, -1, 8)), -1)]);
        scene.Physics.AddStatic(new BoxShape(new(8, 0.125f, 8)), Pose.At(new(0, -0.125f, 0)));
        using var lease = scene.Acquire("room");
        var result = Resolve(Body(), new(1, 2, 0), lease);
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.Equal(new Vector3(1, 3, 0), result.Path[result.Count - 1]);
    }

    [Theory]
    [InlineData(MovementAvailability.Invalid)]
    [InlineData(MovementAvailability.CapacityExceeded)]
    [InlineData(MovementAvailability.Stale)]
    public void EveryMediumRefusalPreservesCallerStorage(MovementAvailability status)
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", Bounds)], []);
        using var lease = scene.Acquire("room");
        scene.Acquisition.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> _,
            Span<MovementDomainContact> _) => new(status, 0, 0, 0, 0, 0, Identity);
        AssertRefused(Resolve(Body(), Vector3.UnitX, lease), status);
    }

    static void AssertWetRefused(AnalyticMovementEnvironment scene, MovementQueryLease lease, Vector3 delta)
    {
        var trace = scene.Acquisition.OnCoverage!;
        bool sawKnownWetCoverage = false;
        scene.Acquisition.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            var coverage = trace(query, spans, contacts);
            sawKnownWetCoverage |= coverage.Availability == MovementAvailability.Known && coverage.ContactsWritten > 0;
            return coverage;
        };
        AssertRefused(Resolve(Body(), delta, lease), MovementAvailability.Unresolved);
        Assert.True(sawKnownWetCoverage, "The refusal must consume actual wet coverage, not a fixture-domain refusal.");
    }

    static List<MovementMediumSweepQuery> Observe(AnalyticMovementEnvironment scene)
    {
        var result = new List<MovementMediumSweepQuery>();
        var trace = scene.Acquisition.OnCoverage!;
        scene.Acquisition.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            Assert.Throws<InvalidOperationException>(() => scene.Physics.Step(1f / 30));
            result.Add(query);
            return trace(query, spans, contacts);
        };
        return result;
    }

    static void AssertTraced(MovementBodyQuery body, Capture result, List<MovementMediumSweepQuery> traces)
    {
        Vector3 start = body.Centre;
        foreach (Vector3 end in result.Path.AsSpan(0, result.Count))
        {
            Vector3 origin = start;
            Assert.Contains(traces, query => query.Body.Centre == origin && query.Body.Centre + query.Delta == end &&
                query.Body.Radius >= body.Radius && query.Body.HalfHeight >= body.HalfHeight);
            start = end;
        }
    }

    static void AssertRefused(Capture result, MovementAvailability status)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(0, result.Count);
        Assert.False(result.Blocked);
        Assert.All(result.Path, point => Assert.Equal(Sentinel, point));
    }

    delegate MovementAvailability ResolveDelegate(in MovementBodyQuery body, Vector3 delta,
        MovementQueryLease lease, Span<Vector3> path, out int written, out bool blocked);
    sealed record Capture(MovementAvailability Status, Vector3[] Path, int Count, bool Blocked);
    static Capture Resolve(MovementBodyQuery body, Vector3 delta, MovementQueryLease lease)
    {
        Type type = typeof(MovementQueryLease).Assembly.GetType("KhaozEngine.Locomotion.MovementCapsuleResolver")!;
        MethodInfo? method = type.GetMethod("TryResolve", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var resolve = method.CreateDelegate<ResolveDelegate>();
        Vector3[] path = new Vector3[9];
        Array.Fill(path, Sentinel);
        var status = resolve(body, delta, lease, path, out int count, out bool blocked);
        return new(status, path, count, blocked);
    }
}
