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

// #1755. A face may reuse its parent's executed basis projections only under the exact key, through the
// capture-owned map, and only while the trace that published them is open. Every refusal runs the original path.
[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class WaterSharedProjectionTests(ITestOutputHelper output) : IDisposable
{
    // Sharing stays on here even in the corpus run that forces every other test onto the original path.
    readonly SharedProjectionBypass _sharing = new(false);

    public void Dispose() => _sharing.Dispose();

    static readonly V Start = V.Point(0.75, -0.5, -0.5), Drive = V.Point(0.25, 0.125, -0.125);
    static readonly I Core = I.Point(0.125);
    const float Radius = 3;
    // Exact normal binary fractions keep the one-ULP key rows away from subnormal operands.
    const double HeightError = 0.0078125, HorizontalError = 0.00390625;

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
    [InlineData("control-token")]
    [InlineData("owner")]
    public void AnyChangedKeyFieldMissesAndRunsTheOriginalExpressions(string field)
    {
        using var environment = Nearest();
        var capture = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        var other = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        var storage = new TileWaterPreparationStorage();
        Assert.NotEmpty(capture.Faces);
        for (int f = 0; f < capture.Faces.Length; f++)
        {
            var face = capture.Faces[f];
            Assert.True(environment.Set("nearest"));
            V start = Start, drive = Drive;
            I core = Core;
            float radius = Radius;
            double height = HeightError, horizontal = HorizontalError;
            switch (field)
            {
                case "start-x-low": start = start with { X = new(Math.BitDecrement(start.X.Low), start.X.High) }; break;
                case "start-z-high": start = start with { Z = new(start.Z.Low, Math.BitIncrement(start.Z.High)) }; break;
                case "drive-x-high": drive = drive with { X = new(drive.X.Low, Math.BitIncrement(drive.X.High)) }; break;
                case "drive-y-low": drive = drive with { Y = new(Math.BitDecrement(drive.Y.Low), drive.Y.High) }; break;
                case "core-low": core = new(Math.BitDecrement(core.Low), core.High); break;
                case "core-high": core = new(core.Low, Math.BitIncrement(core.High)); break;
                case "radius": radius = MathF.BitIncrement(radius); break;
                case "height-error": height = Math.BitIncrement(height); break;
                case "horizontal-error": horizontal = Math.BitIncrement(horizontal); break;
            }
            // The same geometry in another capture is a different owner, as after a replaced sampler.
            var parent = field == "owner" ? other.Prisms[face.Prism].Shape : capture.Prisms[face.Prism].Shape;
            storage.BeginTrace();
            try
            {
                Publish(storage, parent, borrowed: f % 2 == 1);
                if (field == "control-token") Assert.True(environment.Set("up"));
                var prepared = storage.Prepare(face.Shape, start, drive, core, radius, height, horizontal);
                Assert.Equal(field == "none", prepared.HasSharedSource);
                var expected = new FrozenTileWaterConvexShape(face.Shape).Intersect(start, drive, core, radius,
                    heightError: height, horizontalError: horizontal);
                FrozenWaterComparison.Range(expected, prepared.Evaluate());
                storage.DiscardMiss(ref prepared);
            }
            finally { storage.EndTrace(); }
            var stats = storage.Shared.Last;
            Assert.Equal(field == "none" ? 1 : 0, stats.Lends);
            Assert.Equal(field == "none" ? 0 : 1, stats.Misses);
            Assert.Equal(field == "none" ? Mapped(face.Shape) : 0, stats.Copies);
        }
    }

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
    [InlineData("wrong-control")]
    [InlineData("wrong-prefix-map")]
    [InlineData("stale-owner")]
    public void DefeatingOneSharingCheckBreaksFrozenParity(string fault)
    {
        using var environment = Nearest();
        var parent = fault == "wrong-control"
            // A normal 2^-530 operand has an exactly subnormal squared normal, 2^-1060. Its nearest-mode
            // projection resolves, while flush-to-zero must refuse it.
            ? new TileWaterConvexShape([P.Point(Math.ScaleB(1, -530), 0, 0, 0)])
            : new TileWaterConvexShape([P.Point(3, 0, 0, 1)]);
        // A parallel far plane adds one dependent fresh basis and leaves the face's range equal to its parent's.
        P appended = fault == "wrong-prefix-map" ? parent.Planes[0].Opposite : P.Point(1, 0, 0, 100);
        var face = new TileWaterConvexShape([.. parent.Planes.ToArray(), appended], null, parent);
        Assert.Same(parent, face.ProjectionParent);
        V start = fault == "wrong-control" ? V.Point(1, 0, 0) : V.Point(2, 0, 0);
        V drive = fault == "wrong-control" ? V.Point(-1, 0, 0) : V.Point(-3, 0, 0);
        var frozen = new FrozenTileWaterConvexShape(face);
        var storage = new TileWaterPreparationStorage();

        // Positive control: the real lend under the exact key hits and keeps parity.
        storage.BeginTrace();
        try
        {
            var owner = storage.Prepare(parent, start, drive, I.Point(0), 0.25f);
            Assert.True(owner.Evaluate().Resolved);
            storage.DiscardMiss(ref owner);
            var hit = storage.Prepare(face, start, drive, I.Point(0), 0.25f);
            Assert.True(hit.HasSharedSource);
            FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 0.25f), hit.Evaluate());
            storage.DiscardMiss(ref hit);
        }
        finally { storage.EndTrace(); }
        Assert.True(storage.Shared.Last.Copies > 0);

        var source = fault == "stale-owner" ? new TileWaterConvexShape([P.Point(3, 0, 0, -1)]) : parent;
        V oldDrive = fault == "old-drive" ? V.Point(3, 0, 0) : drive;
        var saved = new S[TileWaterPreparedQuery.MaxBases];
        var old = Direct(source, saved, start, oldDrive);
        Assert.True(old.Evaluate().Resolved);
        var rows = new Row[TileWaterPreparedQuery.MaxBases];
        TileWaterSharedProjections.Copy(saved.AsSpan(0, source.Bases.Length), rows);
        int[] map = face.ProjectionBasisMap.ToArray();
        if (fault == "wrong-prefix-map")
        {
            int active = face.Bases.IndexOf(new TileWaterConvexShape.Basis(0));
            Assert.Equal(1, map[active]);
            map[active] = 0; // The parent's zero-active projection.
        }
        // The real key refuses each published fault, so the original expressions keep parity.
        if (fault != "wrong-prefix-map")
        {
            storage.BeginTrace();
            try
            {
                var stale = storage.Prepare(source, start, oldDrive, I.Point(0), 0.25f);
                Assert.True(stale.Evaluate().Resolved);
                storage.DiscardMiss(ref stale);
                if (fault == "wrong-control") Assert.True(environment.Set("flush-output"));
                var refused = storage.Prepare(face, start, drive, I.Point(0), 0.25f);
                Assert.False(refused.HasSharedSource);
                FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 0.25f), refused.Evaluate());
                storage.DiscardMiss(ref refused);
            }
            finally { storage.EndTrace(); }
        }
        if (fault == "wrong-control")
        {
            Assert.True(environment.Set("flush-output"));
            Assert.False(frozen.Intersect(start, drive, I.Point(0), 0.25f).Resolved);
        }

        // Defeat only that check: the face's production evaluator copies the refused rows.
        var expected = frozen.Intersect(start, drive, I.Point(0), 0.25f);
        var broken = Direct(face, new S[TileWaterPreparedQuery.MaxBases], start, drive);
        broken.Share(new TileWaterProjectionSource(map, rows.AsSpan(0, source.Bases.Length), default));
        var actual = broken.Evaluate();
        output.WriteLine($"SHARED_PROJECTION_CONTROL {fault}: FrozenRangeBitsEqual");
        Assert.ThrowsAny<XunitException>(() => FrozenWaterComparison.Range(expected, actual));
    }

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

    static WaterFloatingPointEnvironment Nearest()
    {
        var environment = WaterFloatingPointEnvironment.Capture();
        Assert.NotNull(environment);
        Assert.True(environment.Set("nearest"));
        return environment;
    }

    static void Publish(TileWaterPreparationStorage storage, TileWaterConvexShape parent, bool borrowed)
    {
        var prepared = Prepare(storage, parent);
        Assert.True(prepared.Evaluate().Resolved);
        if (borrowed) Assert.True(storage.Retain(ref prepared) >= 0);
        else storage.DiscardMiss(ref prepared);
    }

    static TileWaterPreparedQuery Prepare(TileWaterPreparationStorage storage, TileWaterConvexShape shape) =>
        storage.Prepare(shape, Start, Drive, Core, Radius, HeightError, HorizontalError);

    static FrozenTileWaterConvexShape.Range Frozen(FrozenTileWaterConvexShape shape, double enter = 0, double exit = 1) =>
        shape.Intersect(Start, Drive, Core, Radius, enter, exit, HeightError, HorizontalError);

    static int Mapped(TileWaterConvexShape face)
    {
        int mapped = 0;
        foreach (int parent in face.ProjectionBasisMap) if (parent >= 0) mapped++;
        return mapped;
    }

    static TileWaterPreparedQuery Direct(TileWaterConvexShape shape, S[] states, V start, V drive) =>
        new(shape, states, new TileWaterPreparedQuery.ClipTerm[TileWaterPreparedQuery.MaxBases *
            (TileWaterPreparedQuery.MaxPlanes + 3)],
            new TileWaterPreparedQuery.PlaneState[TileWaterPreparedQuery.MaxPlanes], start, drive, I.Point(0), 0.25f);
}
