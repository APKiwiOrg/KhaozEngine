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
// Shared by KhaozEngine.TileWorld.Physics.Tests and KhaozEngine.TileWorld.Physics.FloatingPoint.Tests, which runs
// the control-token and wrong-control rows.
public sealed partial class WaterSharedProjectionTests : IDisposable
{
    // Sharing stays on here even in the corpus run that forces every other test onto the original path.
    readonly SharedProjectionBypass _sharing = new(false);

    public void Dispose() => _sharing.Dispose();

    static readonly V Start = V.Point(0.75, -0.5, -0.5), Drive = V.Point(0.25, 0.125, -0.125);
    static readonly I Core = I.Point(0.125);
    const float Radius = 3;
    // Exact normal binary fractions keep the one-ULP key rows away from subnormal operands.
    const double HeightError = 0.0078125, HorizontalError = 0.00390625;

    static void AnyChangedKeyField(string field, Func<WaterFloatingPointEnvironment, string, bool> change)
    {
        using var environment = Nearest();
        var capture = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        var other = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        var storage = new TileWaterPreparationStorage();
        Assert.NotEmpty(capture.Faces);
        for (int f = 0; f < capture.Faces.Length; f++)
        {
            var face = capture.Faces[f];
            Assert.True(environment.SetNearest());
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
                case "radius": radius = FrozenTileWaterUlp.Up(radius); break;
                case "height-error": height = Math.BitIncrement(height); break;
                case "horizontal-error": horizontal = Math.BitIncrement(horizontal); break;
            }
            // The same geometry in another capture is a different owner, as after a replaced sampler.
            var parent = field == "owner" ? other.Prisms[face.Prism].Shape : capture.Prisms[face.Prism].Shape;
            storage.BeginTrace();
            try
            {
                Publish(storage, parent, borrowed: f % 2 == 1);
                if (field == "control-token") Assert.True(change(environment, "up"));
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

    static void DefeatingOneSharingCheck(string fault, ITestOutputHelper output,
        Func<WaterFloatingPointEnvironment, string, bool> change)
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
                if (fault == "wrong-control") Assert.True(change(environment, "flush-output"));
                var refused = storage.Prepare(face, start, drive, I.Point(0), 0.25f);
                Assert.False(refused.HasSharedSource);
                FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 0.25f), refused.Evaluate());
                storage.DiscardMiss(ref refused);
            }
            finally { storage.EndTrace(); }
        }
        if (fault == "wrong-control")
        {
            Assert.True(change(environment, "flush-output"));
            Assert.False(frozen.Intersect(start, drive, I.Point(0), 0.25f).Resolved);
        }

        // Defeat only that check: the face's production evaluator copies the refused rows.
        var expected = frozen.Intersect(start, drive, I.Point(0), 0.25f);
        var broken = Direct(face, new S[TileWaterPreparedQuery.MaxBases], start, drive);
        broken.Share(new TileWaterProjectionSource(map, rows.AsSpan(0, source.Bases.Length), default));
        var actual = broken.Evaluate();
        // Test output runs runner code on this thread, so it is written only under the default controls.
        Assert.True(environment.SetNearest());
        output.WriteLine($"SHARED_PROJECTION_CONTROL {fault}: FrozenRangeBitsEqual");
        Assert.ThrowsAny<XunitException>(() => FrozenWaterComparison.Range(expected, actual));
    }

    static WaterFloatingPointEnvironment Nearest()
    {
        var environment = WaterFloatingPointEnvironment.Capture();
        Assert.NotNull(environment);
        Assert.True(environment.SetNearest());
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
