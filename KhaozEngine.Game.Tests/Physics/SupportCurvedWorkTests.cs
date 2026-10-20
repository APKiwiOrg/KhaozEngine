using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public class SupportCurvedWorkTests
{
    [Fact]
    public void CurvedWorkPolicyDerivesFromExistingQueryAndGraphCeilings()
    {
        Assert.Equal(256, SupportFootprintResult.MaximumElements);
        Assert.Equal(4096, SupportCurvedSourceArithmetic.MaximumNodes);
        Assert.Equal(256 * 4096, SupportCurvedWork.MaximumWork);
        Assert.Equal(1048576, SupportCurvedWork.MaximumWork);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SupportCurvedWork(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SupportCurvedWork(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SupportCurvedWork(1048577));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SupportFootprintCollector(0));
    }

    [Fact]
    public void CurvedWorkChargesEachCategoryAndLatchesBeforeTheNextEvent()
    {
        var work = new SupportCurvedWork(7);
        work.Begin();
        int executed = 0;
        foreach (SupportCurvedWorkKind kind in Enum.GetValues<SupportCurvedWorkKind>())
            if (work.TryCharge(kind)) executed++;
        if (work.TryCharge(SupportCurvedWorkKind.New)) executed++;
        var exhausted = work.Snapshot;
        Assert.Equal(7, executed);
        Assert.Equal(7, exhausted.Consumed);
        Assert.Equal(7, Sum(exhausted));
        Assert.Equal((1L, 1L, 1L, 1L, 1L, 1L, 1L),
            (exhausted.New, exhausted.Sign, exhausted.Split, exhausted.Compile, exhausted.RootCells,
                exhausted.Refine, exhausted.AdmissionCells));
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, exhausted.Failure);
        foreach (SupportCurvedWorkKind kind in Enum.GetValues<SupportCurvedWorkKind>())
            Assert.False(work.TryCharge(kind));
        work.Fail(SupportCurvedWorkFailure.RootStorage);
        Assert.Equal(exhausted, work.Snapshot);
        work.Begin();
        Assert.False(work.Failed);
        Assert.Equal(0, work.Snapshot.Consumed);
        Assert.True(work.TryCharge(SupportCurvedWorkKind.New));
    }

    [Fact]
    public void CurvedWorkExhaustionSurvivesAllSubsolvesWithDiagnosticsOff()
    {
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(false));
        try
        {
            var work = new SupportCurvedWork(7);
            work.Begin();
            var arithmetic = new SupportCurvedSourceArithmetic(work);
            arithmetic.Reset(Vector3.UnitY);
            int outer = arithmetic.Checkpoint;
            var value = arithmetic.Constant(2);
            int inner = arithmetic.Checkpoint;
            arithmetic.Constant(3);
            Assert.Equal(GeometrySign.Unresolved, arithmetic.Sign(value));
            var exhausted = work.Snapshot;
            Assert.Equal(7, exhausted.Consumed);
            Assert.Equal(SupportCurvedWorkFailure.WorkLimit, exhausted.Failure);
            arithmetic.Restore(inner);
            arithmetic.Restore(outer);
            Assert.False(arithmetic.Bounds(arithmetic.One).IsResolved);
            Assert.Equal(GeometrySign.Unresolved, arithmetic.Sign(arithmetic.Subtract(value, value)));
            Assert.Equal(GeometrySign.Unresolved, (GeometrySign)typeof(SupportCurvedSourceArithmetic)
                .GetMethod("PolynomialSign", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(arithmetic, [value])!);
            arithmetic.Reset(new(1, 0.5f, 0));
            Assert.Equal(GeometrySign.Unresolved, arithmetic.Sign(arithmetic.One));
            var regions = new SupportCurvedRootRegions(work);
            Assert.False(regions.Solve(new UnresolvedRoots(), -1, 1));
            Assert.Equal(0, regions.EvaluatedCells);
            var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(Vector3.Zero).Bounds,
                default, 0.25, 0.5, default, default, default, new(1, 0.5f, 0));
            var kernel = new SupportFootprintCurved(work);
            var request = SupportFootprintTests.Request(lower: -2, upper: 2);
            Assert.Equal(GeometrySign.Unresolved, kernel.Measure(new(1), leaf, 0, request, out _));
            Assert.Equal(GeometrySign.Unresolved, kernel.Measure(new(2), leaf, 1, request, out _));
            Assert.Equal(GeometrySign.Unresolved, kernel.Lowest(leaf, 0, request.AxisProposal, -2, 2, out _));
            Assert.Equal(GeometrySign.Unresolved, kernel.Lowest(leaf, 0, request.FootProposal, -2, 2, out _));
            Assert.Equal(exhausted, work.Snapshot);
            var nested = new SupportCurvedWork(7);
            nested.Begin();
            var independent = new SupportCurvedSourceArithmetic(nested);
            independent.Reset(Vector3.UnitY);
            Assert.Equal(GeometrySign.Positive, independent.Sign(independent.One));
            Assert.Equal(6, nested.Snapshot.Consumed);
            Assert.Equal(exhausted, work.Snapshot);
            work.Begin();
            arithmetic.Reset(Vector3.UnitY);
            Assert.Equal(GeometrySign.Positive, arithmetic.Sign(arithmetic.One));
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void CurvedRootStorageFailureReportsItsOwnReason()
    {
        var work = new SupportCurvedWork();
        work.Begin();
        var regions = new SupportCurvedRootRegions(work);
        Assert.False(regions.Solve(new UnresolvedRoots(), -1, 1));
        Assert.True(regions.CapacityRefused);
        Assert.Equal(0, regions.Count);
        var storage = work.Snapshot;
        Assert.Equal(1024, storage.RootStorageHighWater);
        Assert.Equal(SupportCurvedWorkFailure.RootStorage, storage.Failure);
        Assert.True(storage.Consumed < storage.Limit);
        Assert.Equal(storage.Consumed, Sum(storage));
        var arithmetic = new SupportCurvedSourceArithmetic(work);
        arithmetic.Restore(0);
        Assert.False(work.TryCharge(SupportCurvedWorkKind.Sign));
        Assert.Equal(storage, work.Snapshot);
    }

    [Fact]
    public void CurvedWorkTerminalRootsKeepTheFirstFailure()
    {
        var work = new SupportCurvedWork();
        work.Begin();
        var roots = new SupportCurvedRootRegions(work);
        Assert.False(roots.Solve(new TwoRoots(), -1, 1, maximumRoots: 1));
        Assert.Equal(0, roots.Count);
        var storage = work.Snapshot;
        Assert.Equal(SupportCurvedWorkFailure.TerminalRoots, storage.Failure);
        Assert.True(storage.Consumed < storage.Limit);
        work.Fail(SupportCurvedWorkFailure.WorkLimit);
        Assert.False(roots.Solve(new UnresolvedRoots(), -1, 1));
        Assert.Equal(storage, work.Snapshot);
    }

    [Fact]
    public void CurvedWorkAdmissionStorageReportsItsOwnReason()
    {
        var work = new SupportCurvedWork();
        work.Begin();
        var geometry = new SupportTiltedCurvedGeometry(work);
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(Vector3.Zero).Bounds,
            default, 0.25, 0.5, default, default, default, new(1, 0.5f, 0));
        Assert.True(geometry.Begin(leaf, 0, 0, 0.2));
        var admission = new SupportCurvedSurfaceAdmission(work);
        // A closed zero-height slice intersects a curve, but has no uniformly attained cell sample.
        Assert.Equal(GeometrySign.Unresolved, admission.Measure(geometry, SupportCurvedShape.Cylinder, -1,
            0, 0, GeometryInterval.Exact(2), 0.123456789, 0.123456789));
        var storage = work.Snapshot;
        Assert.Equal(1024, storage.AdmissionStorageHighWater);
        Assert.Equal(SupportCurvedWorkFailure.AdmissionStorage, storage.Failure);
        Assert.True(storage.Consumed < storage.Limit);
        Assert.Equal(storage.Consumed, Sum(storage));
        Assert.False(work.TryCharge(SupportCurvedWorkKind.New));
        Assert.Equal(storage, work.Snapshot);
    }

    [Fact]
    public void CurvedWorkCachedGeometryCannotSucceedAfterASiblingFailure()
    {
        var work = new SupportCurvedWork();
        work.Begin();
        var geometry = new SupportTiltedCurvedGeometry(work);
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(Vector3.Zero).Bounds,
            default, 0.25, 0.5, default, default, default, new(1, 0.5f, 0));
        Assert.True(geometry.Begin(leaf, 0, 0, 0.2));
        work.Fail(SupportCurvedWorkFailure.WorkLimit);
        var exhausted = work.Snapshot;
        Assert.Equal(GeometrySign.Unresolved, geometry.Side(0, out _, out _));
        Assert.Equal(GeometrySign.Unresolved, geometry.Axis(true, 1, out _));
        Assert.Equal(GeometrySign.Unresolved, geometry.Cap(1, false, out _, out _));
        Assert.Equal(GeometrySign.Unresolved, geometry.Round(false, 0, out _, out _));
        Assert.Equal(GeometrySign.Unresolved, geometry.Contact(SupportCurvedShape.Capsule, 0, 0.2, -2, 2, out _));
        Assert.False(geometry.SurfacePointInside(0, 0, 0, 0, 0, 2, -2, 2));
        Assert.False(geometry.CapUniformlyUp(2));
        Assert.Equal(exhausted, work.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedWorkPredicateRecursionChargesBeforeEachEntry(bool radical)
    {
        const int limit = 64;
        var work = new SupportCurvedWork(limit);
        work.Begin();
        var arithmetic = new SupportCurvedSourceArithmetic(work);
        SupportCurvedSourceArithmetic.Number Expression()
        {
            arithmetic.Reset(Vector3.UnitY);
            return radical ? arithmetic.Subtract(arithmetic.Multiply(arithmetic.Root(arithmetic.Constant(2)),
                arithmetic.Root(arithmetic.Constant(8))), arithmetic.Constant(4)) :
                arithmetic.Subtract(arithmetic.Divide(arithmetic.One, arithmetic.Constant(3)),
                    arithmetic.Divide(arithmetic.Constant(2), arithmetic.Constant(6)));
        }
        var expression = Expression();
        Assert.True(arithmetic.Bounds(expression).IsResolved);
        // Leave exactly one predicate entry and one recursive entry. The next recursive event must not run.
        long used = work.Snapshot.Consumed;
        for (long i = used; i < limit - 2; i++) Assert.True(work.TryCharge(SupportCurvedWorkKind.New));
        var before = work.Snapshot;
        Assert.Equal(GeometrySign.Unresolved, arithmetic.Sign(expression));
        var refused = work.Snapshot;
        Assert.Equal(limit, refused.Consumed);
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, refused.Failure);
        Assert.Equal(1, refused.Sign - before.Sign);
        Assert.Equal(radical ? 1 : 0, refused.Split - before.Split);
        Assert.Equal(radical ? 0 : 1, refused.Compile - before.Compile);
        Assert.Equal(refused.Consumed, Sum(refused));
        arithmetic.Restore(arithmetic.Checkpoint);
        Assert.Equal(GeometrySign.Unresolved, arithmetic.Sign(expression));
        Assert.Equal(refused, work.Snapshot);
        work.Begin();
        expression = Expression();
        Assert.Equal(GeometrySign.Zero, arithmetic.Sign(expression));
        Assert.True(radical ? work.Snapshot.Split > 0 : work.Snapshot.Compile > 0);
    }

    [Fact]
    public void CurvedWorkUpwardShortcutCannotIgnoreItsDeniedSign()
    {
        var work = new SupportCurvedWork(256);
        work.Begin();
        var geometry = new SupportTiltedCurvedGeometry(work);
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(Vector3.Zero).Bounds,
            default, 0.25, 0.5, default, default, default, new(1, 0.5f, 0));
        Assert.True(geometry.Begin(leaf, 0, 0, 0.2));
        Assert.True(geometry.CapUpward(2).Lower > 0);
        long used = work.Snapshot.Consumed;
        for (long i = used; i < 255; i++) Assert.True(work.TryCharge(SupportCurvedWorkKind.New));
        Assert.False(geometry.CapUniformlyUp(2));
        Assert.Equal(256, work.Snapshot.Consumed);
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, work.Snapshot.Failure);
    }

    internal static long Sum(SupportCurvedWorkObservation o) =>
        o.New + o.Sign + o.Split + o.Compile + o.RootCells + o.Refine + o.AdmissionCells;

    sealed class UnresolvedRoots : ISupportCurvedRootFunction
    {
        public GeometryInterval Evaluate(GeometryInterval parameter, bool derivative) => GeometryInterval.Enclose(-1, 1);
        public GeometrySign Sign(double parameter) => GeometrySign.Unresolved;
        public GeometrySign DerivativeSign(GeometryInterval parameter) => GeometrySign.Unresolved;
    }

    sealed class TwoRoots : ISupportCurvedRootFunction
    {
        public GeometryInterval Evaluate(GeometryInterval parameter, bool derivative) => derivative ?
            parameter.Multiply(GeometryInterval.Exact(2)) : parameter.Square().Subtract(GeometryInterval.Exact(0.25));
        public GeometrySign Sign(double parameter) => parameter * parameter == 0.25 ? GeometrySign.Zero :
            parameter * parameter < 0.25 ? GeometrySign.Negative : GeometrySign.Positive;
        public GeometrySign DerivativeSign(GeometryInterval parameter) => GeometrySign.Unresolved;
    }
}
