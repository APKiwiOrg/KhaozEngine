using System;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;
using S = KhaozEngine.TileWorld.Physics.TileWaterPreparedQuery.BasisState;
using Row = KhaozEngine.TileWorld.Physics.TileWaterSharedProjections.Row;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed partial class WaterSharedProjectionTests(ITestOutputHelper output)
{
    // The control-token and wrong-control rows run in KhaozEngine.TileWorld.Physics.FloatingPoint.Tests.
    [Theory]
    [InlineData("copied")]
    [InlineData("borrowed")]
    public void FaceReusesItsParentsResolvedProjectionsAndKeepsEveryBit(string path)
    {
        using var environment = Nearest();
        var capture = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        var storage = new TileWaterPreparationStorage();
        Assert.NotEmpty(capture.Faces);
        foreach (var face in capture.Faces)
        {
            var parent = capture.Prisms[face.Prism].Shape;
            var frozen = new FrozenTileWaterConvexShape(face.Shape);
            storage.BeginTrace();
            try
            {
                Publish(storage, parent, path == "borrowed");
                var prepared = Prepare(storage, face.Shape);
                Assert.True(prepared.HasSharedSource);
                FrozenWaterComparison.Range(Frozen(frozen), prepared.Evaluate());
                // Later windows read the face's own copied state, never the parent's storage.
                FrozenWaterComparison.Range(Frozen(frozen, 0.25, 0.75), prepared.Evaluate(0.25, 0.75));
                FrozenWaterComparison.Range(Frozen(frozen, 0.5, 0.5), prepared.Evaluate(0.5, 0.5));
                storage.DiscardMiss(ref prepared);
            }
            finally { storage.EndTrace(); }
            var stats = storage.Shared.Last;
            int mapped = Mapped(face.Shape);
            Assert.True(mapped > 0);
            Assert.Equal(1, stats.Lends);
            Assert.Equal(mapped, stats.Copies);
            Assert.Equal(face.Shape.Bases.Length - mapped, stats.Fresh);
            Assert.Equal(path == "borrowed" ? 1 : 0, stats.Borrowed);
            Assert.Equal(path == "copied" ? 1 : 0, stats.Published);
        }
        output.WriteLine($"SHARED_PROJECTION {path}: faces={capture.Faces.Length}");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("start-x-low")]
    [InlineData("start-z-high")]
    [InlineData("drive-x-high")]
    [InlineData("drive-y-low")]
    [InlineData("core-low")]
    [InlineData("core-high")]
    [InlineData("radius")]
    [InlineData("height-error")]
    [InlineData("horizontal-error")]
    [InlineData("owner")]
    public void AnyChangedKeyFieldMissesAndRunsTheOriginalExpressions(string field) =>
        AnyChangedKeyField(field, WaterFloatingPointEnvironment.NearestOnly);

    [Fact]
    public void AKeyWithoutAControlTokenIsUnsupported()
    {
        using var environment = Nearest();
        var capture = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        var face = capture.Faces[0];
        var storage = new TileWaterPreparationStorage();
        storage.BeginTrace();
        try
        {
            Publish(storage, capture.Prisms[face.Prism].Shape, borrowed: false);
            var prepared = Prepare(storage, face.Shape);
            Assert.True(prepared.HasSharedSource);
            var key = prepared.SharingKey;
            storage.DiscardMiss(ref prepared);
            key.HasControlToken = false;
            Assert.True(storage.Shared.Lend(face.Shape, key, ReadOnlySpan<byte>.Empty).IsEmpty);
        }
        finally { storage.EndTrace(); }
        Assert.Equal(1, storage.Shared.Last.Lends);
        Assert.Equal(1, storage.Shared.Last.Unsupported);
    }

    [Theory]
    [InlineData("old-drive")]
    [InlineData("wrong-prefix-map")]
    [InlineData("stale-owner")]
    public void DefeatingOneSharingCheckBreaksFrozenParity(string fault) =>
        DefeatingOneSharingCheck(fault, output, WaterFloatingPointEnvironment.NearestOnly);

    [Theory]
    [InlineData("copied")]
    [InlineData("borrowed")]
    public void OnlyReachedResolvedRowsAreReused(string path)
    {
        var states = new S[4];
        states[0] = new S { Mask = 1, Projected = true, Q = V.Point(1, 2, 3), V = V.Point(4, 5, 6) };
        states[0].Lambda[0] = I.Point(7);
        states[0].Velocity[2] = I.Point(8);
        states[1] = new S { Mask = 1, Projected = false, Q = V.Point(9, 10, 11) };
        states[2] = new S { Projected = true, Q = V.Point(12, 13, 14) }; // Never reached.
        states[3] = states[0];
        var rows = new Row[states.Length];
        TileWaterSharedProjections.Copy(states, rows);
        int[] map = [0, 1, 2, -1];
        var source = path == "copied" ? new TileWaterProjectionSource(map, rows, default)
            : new TileWaterProjectionSource(map, default, states);
        var target = new S();
        Assert.True(source.TryCopy(0, ref target));
        Assert.True(target.Projected);
        Assert.Equal(states[0].Q, target.Q);
        Assert.Equal(states[0].V, target.V);
        Assert.Equal(states[0].Lambda[0], target.Lambda[0]);
        Assert.Equal(states[0].Velocity[2], target.Velocity[2]);
        for (int ordinal = 1; ordinal < map.Length; ordinal++)
        {
            target = new S();
            Assert.False(source.TryCopy(ordinal, ref target));
            Assert.Equal(new S().Q, target.Q);
            Assert.False(target.Projected);
        }
    }

    [Theory]
    [InlineData("copied")]
    [InlineData("borrowed")]
    public void AnUnevaluatedParentPublishesNothingReusable(string path)
    {
        using var environment = Nearest();
        var capture = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        var face = capture.Faces[0];
        var storage = new TileWaterPreparationStorage();
        storage.BeginTrace();
        try
        {
            var parent = Prepare(storage, capture.Prisms[face.Prism].Shape);
            if (path == "borrowed") Assert.True(storage.Retain(ref parent) >= 0);
            else storage.DiscardMiss(ref parent);
            var prepared = Prepare(storage, face.Shape);
            Assert.True(prepared.HasSharedSource);
            FrozenWaterComparison.Range(Frozen(new FrozenTileWaterConvexShape(face.Shape)), prepared.Evaluate());
            storage.DiscardMiss(ref prepared);
        }
        finally { storage.EndTrace(); }
        Assert.Equal(1, storage.Shared.Last.Lends);
        Assert.Equal(0, storage.Shared.Last.Copies);
        Assert.Equal(face.Shape.Bases.Length, storage.Shared.Last.Fresh);
    }

    [Fact]
    public void TraceEndForgetsEveryOwnerAndEntry()
    {
        using var environment = Nearest();
        var capture = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        var face = capture.Faces[0];
        var storage = new TileWaterPreparationStorage();
        storage.BeginTrace();
        try
        {
            Publish(storage, capture.Prisms[face.Prism].Shape, borrowed: false);
            Assert.Equal(1, storage.Shared.HeldOwnersForTests);
        }
        finally { storage.EndTrace(); }
        Assert.Equal(0, storage.Shared.HeldOwnersForTests);
        Assert.Equal(1, storage.Shared.Last.Published);
        storage.BeginTrace();
        try
        {
            var prepared = Prepare(storage, face.Shape);
            Assert.False(prepared.HasSharedSource);
            FrozenWaterComparison.Range(Frozen(new FrozenTileWaterConvexShape(face.Shape)), prepared.Evaluate());
            storage.DiscardMiss(ref prepared);
        }
        finally { storage.EndTrace(); }
        Assert.Equal(1, storage.Shared.Last.Misses);
    }

    [Fact]
    public void NestedLendsAndReentrantTracesFallBack()
    {
        using var environment = Nearest();
        var capture = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        var face = capture.Faces[0];
        var parent = capture.Prisms[face.Prism].Shape;
        var frozen = new FrozenTileWaterConvexShape(face.Shape);
        var storage = new TileWaterPreparationStorage();
        storage.BeginTrace();
        try
        {
            Publish(storage, parent, borrowed: false);
            var outstanding = Prepare(storage, face.Shape);
            Assert.True(outstanding.HasSharedSource);
            // A second borrow before the first face settles never receives the owner's rows.
            var nested = Prepare(storage, face.Shape);
            Assert.False(nested.HasSharedSource);
            FrozenWaterComparison.Range(Frozen(frozen), nested.Evaluate());
            storage.DiscardMiss(ref nested);
            var settled = Prepare(storage, face.Shape);
            Assert.True(settled.HasSharedSource);
            storage.DiscardMiss(ref settled);

            // A reentrant trace suspends sharing until the outermost trace ends.
            storage.BeginTrace();
            Publish(storage, capture.Prisms[1 - face.Prism].Shape, borrowed: false);
            var inner = Prepare(storage, face.Shape);
            Assert.False(inner.HasSharedSource);
            FrozenWaterComparison.Range(Frozen(frozen), inner.Evaluate());
            storage.DiscardMiss(ref inner);
            storage.EndTrace();
            var after = Prepare(storage, face.Shape);
            Assert.False(after.HasSharedSource);
            FrozenWaterComparison.Range(Frozen(frozen), after.Evaluate());
            storage.DiscardMiss(ref after);
        }
        finally { storage.EndTrace(); }
        var stats = storage.Shared.Last;
        Assert.Equal(1, stats.Published);
        Assert.Equal(2, stats.Lends);
        Assert.Equal(3, stats.Nested);
        Assert.Equal(0, storage.Shared.HeldOwnersForTests);
        storage.BeginTrace();
        try
        {
            Publish(storage, parent, borrowed: false);
            var again = Prepare(storage, face.Shape);
            Assert.True(again.HasSharedSource);
            storage.DiscardMiss(ref again);
        }
        finally { storage.EndTrace(); }
    }

    // #1755 review R1. A nested trace resets the slab and may retain another parent with the same raw inputs where
    // an outstanding borrowed source still points. A source must never supply a value its lend no longer owns.
    [Theory]
    [InlineData("borrowed")]
    [InlineData("copied")]
    public void OutstandingBorrowIsInvalidatedByReentrantTrace(string path)
    {
        using var environment = Nearest();
        var (parent, other, face) = Opposed();
        bool borrowed = path == "borrowed";
        var storage = new TileWaterPreparationStorage();
        storage.BeginTrace();
        PublishShifted(storage, parent, borrowed);
        var outstanding = Shifted(storage, face);
        Assert.True(outstanding.HasSharedSource);
        storage.BeginTrace();
        PublishElsewhere(storage, other, borrowed);
        storage.EndTrace();
        var actual = outstanding.Evaluate();
        storage.EndTrace();
        FrozenWaterComparison.Range(ShiftedFrozen(face), actual);
        Assert.Equal(0, storage.Shared.Last.Copies);
        Assert.Equal(face.Bases.Length, storage.Shared.Last.Stale);
    }

    [Theory]
    [InlineData("borrowed", "between-traces")]
    [InlineData("copied", "between-traces")]
    [InlineData("borrowed", "next-trace")]
    [InlineData("copied", "next-trace")]
    [InlineData("borrowed", "republished")]
    [InlineData("copied", "republished")]
    public void ASourceOutlivingItsTraceCopiesNothing(string path, string when)
    {
        using var environment = Nearest();
        var (parent, other, face) = Opposed();
        bool borrowed = path == "borrowed";
        var storage = new TileWaterPreparationStorage();
        storage.BeginTrace();
        PublishShifted(storage, parent, borrowed);
        var stale = Shifted(storage, face);
        Assert.True(stale.HasSharedSource);
        storage.EndTrace();
        TileWaterConvexShape.Range actual = default;
        if (when == "between-traces") actual = stale.Evaluate();
        storage.BeginTrace();
        // A later trace republishes another parent with the same raw inputs into the stale source's row block or slab offset.
        if (when == "republished") PublishElsewhere(storage, other, borrowed);
        if (when != "between-traces") actual = stale.Evaluate();
        storage.EndTrace();
        FrozenWaterComparison.Range(ShiftedFrozen(face), actual);
        Assert.Equal(0, storage.Shared.Last.Copies);
        Assert.Equal(face.Bases.Length, storage.Shared.Last.Stale);
    }

    [Theory]
    [InlineData("borrowed")]
    [InlineData("copied")]
    public void ASettledSourceCopiesNothingAfterAnotherLend(string path)
    {
        using var environment = Nearest();
        var (parent, _, face) = Opposed();
        var storage = new TileWaterPreparationStorage();
        storage.BeginTrace();
        PublishShifted(storage, parent, path == "borrowed");
        var settled = Shifted(storage, face);
        Assert.True(settled.HasSharedSource);
        storage.DiscardMiss(ref settled);
        var current = Shifted(storage, face);
        Assert.True(current.HasSharedSource);
        var actual = settled.Evaluate();
        storage.DiscardMiss(ref current);
        storage.EndTrace();
        FrozenWaterComparison.Range(ShiftedFrozen(face), actual);
        Assert.Equal(2, storage.Shared.Last.Lends);
        Assert.Equal(0, storage.Shared.Last.Copies);
        Assert.Equal(face.Bases.Length, storage.Shared.Last.Stale);
    }

    [Fact]
    public void AFullTableRefusesFurtherParentsWithIdenticalOutput()
    {
        using var environment = Nearest();
        var pieces = Enumerable.Range(0, TileWaterSharedProjections.Slots + 1)
            .Select(_ => WaterKernelTraceFixture.Piece()).ToArray();
        var faces = pieces.Select(piece => new TileWaterConvexShape([.. piece.Planes, piece.Planes[4].Opposite],
            null, piece.Shape)).ToArray();
        var storage = new TileWaterPreparationStorage();
        storage.BeginTrace();
        try
        {
            for (int i = 0; i < pieces.Length; i++) Publish(storage, pieces[i].Shape, borrowed: i % 2 == 0);
            var first = Prepare(storage, faces[0]);
            Assert.True(first.HasSharedSource);
            FrozenWaterComparison.Range(Frozen(new FrozenTileWaterConvexShape(faces[0])), first.Evaluate());
            storage.DiscardMiss(ref first);
            var last = Prepare(storage, faces[^1]);
            Assert.False(last.HasSharedSource);
            FrozenWaterComparison.Range(Frozen(new FrozenTileWaterConvexShape(faces[^1])), last.Evaluate());
            storage.DiscardMiss(ref last);
        }
        finally { storage.EndTrace(); }
        var stats = storage.Shared.Last;
        Assert.Equal(TileWaterSharedProjections.Slots, stats.Published + stats.Borrowed);
        Assert.Equal(1, stats.Exhausted);
        Assert.Equal(1, stats.Lends);
        Assert.Equal(1, stats.Misses);
    }

    [Fact]
    public void ReservedPayloadIsTheProjectionOnlyLayout()
    {
        TileWaterSharedProjections? owner = null;
        long bytes = AllocationMeasurement.Measure("shared projection owner", () => owner = new TileWaterSharedProjections());
        GC.KeepAlive(owner);
        int payload = TileWaterSharedProjections.Slots * TileWaterPreparedQuery.MaxBases * Unsafe.SizeOf<Row>();
        Assert.Equal(payload, TileWaterSharedProjections.ReservedPayloadBytes);
        // The design bound is about 0.54 MiB of rows per table before keys and array headers.
        Assert.InRange(payload, 1, (int)(0.54 * 1024 * 1024));
        Assert.InRange(bytes, payload, payload + 16 * 1024);
        output.WriteLine($"SHARED_PROJECTION_MEMORY row={Unsafe.SizeOf<Row>()}, payload={payload}, allocated={bytes}");
    }

    static readonly V ShiftStart = V.Point(2, 0, 0), ShiftDrive = V.Point(-3, 0, 0);

    // Parents on opposite planes x = 1/3 and x = -1/3, and a face of the first with a far parallel plane. With the
    // same raw inputs their headers match, so only storage ownership can tell their projections apart.
    static (TileWaterConvexShape Parent, TileWaterConvexShape Other, TileWaterConvexShape Face) Opposed()
    {
        var parent = new TileWaterConvexShape([P.Point(3, 0, 0, 1)]);
        var face = new TileWaterConvexShape([.. parent.Planes.ToArray(), P.Point(1, 0, 0, 100)], null, parent);
        Assert.Same(parent, face.ProjectionParent);
        return (parent, new TileWaterConvexShape([P.Point(3, 0, 0, -1)]), face);
    }

    static TileWaterPreparedQuery Shifted(TileWaterPreparationStorage storage, TileWaterConvexShape shape) =>
        storage.Prepare(shape, ShiftStart, ShiftDrive, I.Point(0), 0.25f);

    static FrozenTileWaterConvexShape.Range ShiftedFrozen(TileWaterConvexShape shape) =>
        new FrozenTileWaterConvexShape(shape).Intersect(ShiftStart, ShiftDrive, I.Point(0), 0.25f);

    static void PublishShifted(TileWaterPreparationStorage storage, TileWaterConvexShape parent, bool borrowed)
    {
        var prepared = Shifted(storage, parent);
        Assert.True(prepared.Evaluate().Resolved);
        if (borrowed) Assert.Equal(0, storage.Retain(ref prepared));
        else storage.DiscardMiss(ref prepared);
    }

    // Prepared on its own workspace, so only the storage's slab and rows change under an outstanding face.
    static void PublishElsewhere(TileWaterPreparationStorage storage, TileWaterConvexShape parent, bool borrowed)
    {
        var prepared = new TileWaterPreparationWorkspace().Prepare(parent, ShiftStart, ShiftDrive, I.Point(0), 0.25f);
        Assert.True(prepared.Evaluate().Resolved);
        if (borrowed) Assert.Equal(0, storage.Retain(ref prepared));
        else storage.DiscardMiss(ref prepared);
    }

    static FrozenTileWaterConvexShape.Range Frozen(FrozenTileWaterConvexShape shape, double enter = 0, double exit = 1) =>
        shape.Intersect(Start, Drive, Core, Radius, enter, exit, HeightError, HorizontalError);
}
