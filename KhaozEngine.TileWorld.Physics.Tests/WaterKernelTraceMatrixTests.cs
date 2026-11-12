using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
[Trait("Category", "Diagnostic")]
public sealed class WaterKernelTraceMatrixTests
{
    static readonly Lazy<TileMediumSampler> s_precisionField = new(() => MovementWaterFieldTests.SingleWaterTile(1000000, -1, 1));
    [Theory]
    [InlineData("candidate", 256, MovementAvailability.Known)]
    [InlineData("candidate", 257, MovementAvailability.CapacityExceeded)]
    [InlineData("piece", 256, MovementAvailability.Known)]
    [InlineData("piece", 257, MovementAvailability.CapacityExceeded)]
    [InlineData("contact", 256, MovementAvailability.Known)]
    [InlineData("contact", 257, MovementAvailability.CapacityExceeded)]
    public void PrismStorageBoundariesMatchTheOriginalRefusalAndAtomicBuffers(string kind, int count,
        MovementAvailability availability)
    {
        // #1755. Flat-column capacity tests do not cover the prism scratch's independent limits.
        var snapshot = kind is "candidate" or "piece"
            ? WaterKernelTraceFixture.RepeatedPieces(count, candidates: kind == "candidate")
            : WaterKernelTraceFixture.RepeatedFaces(count, wet: kind == "contact");
        var result = FrozenWaterComparison.CheckPrisms(snapshot, WaterKernelTraceFixture.Query());
        Assert.Equal(availability, result.Availability);
        if (availability == MovementAvailability.Known)
            Assert.Equal(kind == "face-hit-defensive" ? 1 : 256, result.ContactsWritten);
    }

    [Theory]
    [Trait("Category", "Diagnostic")]
    [InlineData(2048, MovementAvailability.Known)]
    [InlineData(2049, MovementAvailability.CapacityExceeded)]
    public void DefensiveFaceHitStorageBoundaryRunsInTheExplicitContractMatrix(int count, MovementAvailability status)
    {
        // Distinct retained hit shapes pressure the next stage's optional preparation storage too.
        var result = FrozenWaterComparison.CheckPrisms(WaterKernelTraceFixture.RepeatedFaces(count, wet: false),
            WaterKernelTraceFixture.Query());
        Assert.Equal(status, result.Availability);
        if (status == MovementAvailability.Known) Assert.Equal(1, result.ContactsWritten);
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void SelectedMissesAndAnotherWorldDoNotReuseDirtyHitScratch()
    {
        var busy = WaterKernelTraceFixture.RepeatedFaces(256, wet: true);
        var misses = WaterKernelTraceFixture.SelectedMisses(1024);
        _ = FrozenWaterComparison.CheckPrisms(busy, WaterKernelTraceFixture.Query());
        var dry = FrozenWaterComparison.CheckPrisms(misses, WaterKernelTraceFixture.Query());
        Assert.Equal(MovementAvailability.Known, dry.Availability);
        Assert.Equal(0, dry.ContactsWritten);
        _ = FrozenWaterComparison.CheckPrisms(busy, WaterKernelTraceFixture.Query());
    }

    [Fact]
    public void APointContactCanBeTangentWhileItsOwningSpanOverlaps()
    {
        var piece = WaterKernelTraceFixture.Piece();
        var faces = TileWaterPrismFaces.Build([piece]);
        var geometry = WaterKernelTraceFixture.Snapshot([piece.Column], [piece], faces, WaterKernelTraceFixture.Ground());
        var query = new MovementMediumSweepQuery(new(new(0.75f, piece.Column.Maximum.Y + 0.75f, -0.25f),
            0.25f, 0.75f, MovementWaterFieldTests.Space, null), new(0, -0.5f, 0));
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var identity = MovementWaterFieldTests.Identity;
        var result = TileWaterPrismCoverage.Trace(geometry.Prisms, geometry.Faces, geometry.Ground, geometry.PointIndex,
            geometry.PrismStarts, geometry.FaceStarts, query, Vector3.Zero, identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Contains(contacts.Take(result.ContactsWritten), contact => contact.Overlap == MovementContactOverlap.Tangent &&
            contact.SpanOverlap == MovementContactOverlap.Overlapping);
        _ = FrozenWaterComparison.CheckPrisms(geometry, query);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    public void AnUnresolvedEarlierPiecePrecedesLaterRetainedHitPressure(int count)
    {
        // Defensive inconsistent plane data reaches the Project failure before piece-hit exhaustion.
        var snapshot = WaterKernelTraceFixture.RepeatedPieces(count, unresolved: true);
        var result = FrozenWaterComparison.CheckPrisms(snapshot, WaterKernelTraceFixture.Query());
        Assert.Equal(MovementAvailability.Unresolved, result.Availability);
    }

    [Theory]
    [InlineData(255, MovementAvailability.Unresolved)]
    [InlineData(256, MovementAvailability.CapacityExceeded)]
    public void WitnessFailureAndContactPressureKeepTheirOriginalPrecedence(int goodContacts, MovementAvailability availability)
    {
        // Defensive face multiplicity approaches capacity, then the next contact projects into a captured ground hole.
        var snapshot = WaterKernelTraceFixture.WitnessPressure(goodContacts);
        var query = new MovementMediumSweepQuery(new(new(0.95f, -0.5f, -0.5f), 0.3f, 0.75f,
            MovementWaterFieldTests.Space, null), Vector3.Zero);
        var result = FrozenWaterComparison.CheckPrisms(snapshot, query);
        Assert.Equal(availability, result.Availability);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 256)]
    [InlineData(64, 0)]
    public void LocatedPrecisionRefusalPrecedesShortCallerBuffers(int spans, int contacts)
    {
        const int x = 1000000;
        var field = s_precisionField.Value;
        var query = new MovementMediumSweepQuery(new(new(x + 0.25f, 1, 0.5f), 0.1f, 0.25f,
            MovementWaterFieldTests.Space, null), new(0.2f, -4, 0));
        var reference = new FrozenWaterReference(field.CaptureTraceForTests());
        var result = FrozenWaterComparison.Check(field, reference, query, spanCapacity: spans, contactCapacity: contacts);
        Assert.Equal(MovementAvailability.PrecisionExceeded, result.Availability);
    }

    [Theory]
    [InlineData(false, MovementAvailability.Known, 64)]
    [InlineData(true, MovementAvailability.CapacityExceeded, 65)]
    public void PrismSpanBoundaryIncludesBothOutwardCutsAndExactCuts(bool oneMore, MovementAvailability status, int required)
    {
        var query = new MovementMediumSweepQuery(new(new(0.125f, -0.5f, -0.75f), 0.125f, 0.25f,
            MovementWaterFieldTests.Space, null), new(0.5f, 0, 0));
        var result = FrozenWaterComparison.CheckPrisms(WaterKernelTraceFixture.SpanPressure(oneMore), query);
        Assert.Equal(status, result.Availability);
        Assert.Equal(required, result.RequiredSpanCapacity);
    }

    [Fact]
    public void EveryFaceKindIncludingSplitAndLowerClippedSidesMatchesTheFrozenEvaluator()
    {
        var geometry = WaterKernelTraceFixture.RealFaceTypes();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, geometry.Faces.Select(face => face.Plane).Distinct().Order());
        Assert.Contains(geometry.Faces, face => face.Shape.CaptureGeometryForTests().Planes.Length == 9);
        Assert.Contains(geometry.Faces, face => face.Shape.CaptureGeometryForTests().Planes.Length == 10);
        var query = new MovementMediumSweepQuery(new(new(0.75f, -0.5f, -0.5f), 3, 3,
            MovementWaterFieldTests.Space, null), new(0.25f, 0.125f, -0.125f));
        _ = FrozenWaterComparison.CheckPrisms(geometry, query);
        Assert.NotEmpty(geometry.Faces);
        foreach (var face in geometry.Faces)
        {
            var piece = geometry.Prisms[face.Prism];
            var frozen = new FrozenTileWaterConvexShape(face.Shape);
            var start = piece.Start(query, Vector3.Zero);
            var drive = TileWaterConvexShape.Vector.Point(query.Delta.X, query.Delta.Y, query.Delta.Z);
            foreach (var window in new[] { (0d, 1d), (0.5d, 0.5d), (0.25d, 0.75d) })
                FrozenWaterComparison.Range(frozen.Intersect(start, drive, TileWaterArithmetic.Point(0), 3, window.Item1,
                    window.Item2, piece.HeightError, piece.HorizontalError), face.Shape.Intersect(start, drive,
                    TileWaterArithmetic.Point(0), 3, window.Item1, window.Item2, piece.HeightError, piece.HorizontalError));
        }
    }

    [Fact]
    public void DuplicateAdjacentAndOutwardCutsKeepBitsAndCapacity()
    {
        float[] expected = new float[66], actual = new float[66];
        expected[0] = actual[0] = 0;
        expected[1] = actual[1] = 1;
        int ec = 2, ac = 2;
        double ee = 0, ae = 0;
        foreach (double root in new[] { 1d / 3, 1d / 3, (double)MathF.BitIncrement(1f / 3), 0.5, Math.BitIncrement(0.5) })
        {
            bool a = FrozenTileWaterPrismCoverage.AddRangeForTests(root, root, expected, ref ec, Vector3.UnitX, ref ee);
            bool b = TileWaterPrismCoverage.AddRangeForTests(root, root, actual, ref ac, Vector3.UnitX, ref ae);
            Assert.Equal(a, b);
            Assert.Equal(ec, ac);
            Assert.Equal(expected, actual);
            Assert.Equal(BitConverter.DoubleToInt64Bits(ee), BitConverter.DoubleToInt64Bits(ae));
        }
        Assert.Contains((float)(1d / 3), actual[..ac]);
        Assert.Contains(MathF.BitIncrement((float)(1d / 3)), actual[..ac]);
        // Defensive assembly seam. Fill the exact cut storage boundary without constructing a large world.
        for (int i = 0; i < actual.Length; i++) expected[i] = actual[i] = (float)i / 128;
        ec = ac = actual.Length;
        Assert.True(FrozenTileWaterPrismCoverage.InsertForTests(0.5f, expected, ref ec));
        Assert.True(TileWaterPrismCoverage.InsertForTests(0.5f, actual, ref ac));
        Assert.Equal(FrozenTileWaterPrismCoverage.InsertForTests(0.9375f, expected, ref ec),
            TileWaterPrismCoverage.InsertForTests(0.9375f, actual, ref ac));
        Assert.Equal(ec, ac);
        Assert.Equal(expected, actual);
    }
}
