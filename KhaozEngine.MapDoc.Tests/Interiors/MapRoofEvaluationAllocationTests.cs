using System;
using System.Numerics;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class MapRoofAllocationCollection { }

[Collection("AllocSensitive")]
public sealed class MapRoofEvaluationAllocationTests
{
    [Fact]
    public void MovingObserver_WithinAcrossAndOutsideRooms_AllocatesZeroAfterWarmup()
    {
        var fixture = PrefabFixtures.LinkedBuildings();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scope = new MapSceneScope(
            new(WorldFrame.Origin, new(-32, -32), new(32, 32), null, null,
                Enum.GetValues<MapSurfaceRole>(), null, new()),
            new(-32, -32, -32, 32, 32, 32), MapSceneChannels.All);
        var acquired = NativeSceneFixtures.Open(native, false).Acquire(scope);
        Assert.True(acquired.Witness.CompleteForScope, string.Join("\n", acquired.Findings));
        var scene = acquired.Scene!;
        var preparation = MapRoofEvaluationBuffer.Prepare(scene);
        Assert.True(preparation.Buffer is not null, preparation.Detail);
        var buffer = preparation.Buffer!;
        var frame = scene.SurfaceView.Scope.Frame;
        buffer.Evaluate(new(frame, new(.5f, 1, -.5f)), MapRoofMode.Auto);
        buffer.Evaluate(new(frame, new(8.5f, 1, -.5f)), MapRoofMode.Auto);
        buffer.Evaluate(new(frame, new(20, 1, -20)), MapRoofMode.Auto);
        buffer.Evaluate(new(frame, new(0, 1, -.5f)), MapRoofMode.Auto);
        buffer.Evaluate(new(frame, new(1, 1, -4)), MapRoofMode.Auto);
        buffer.Evaluate(new(frame, new(1.75f, 1, 0)), MapRoofMode.Auto);
        buffer.Evaluate(new(frame, new(.75f, 1, -.75f)), MapRoofMode.AlwaysVisible);
        buffer.Evaluate(new(frame, new(.875f, 1, -.875f)), MapRoofMode.AlwaysHidden);

        // Measured points were not visited during warm-up. A cache of exact observers cannot satisfy this proof.
        Vector3[] positions = [new(1.234567f, 1, -1.345678f), new(1.75f, 1, -1.75f), new(9.25f, 1, -1.25f),
            new(0, 1, -1), new(2, 1, -4), new(2, 1, 0), new(20.5f, 1, -20.5f),
            new(1.5f, 1, -1.5f), new(1.625f, 1, -1.5f), new(20.75f, 1, -20.75f),
            new(float.Epsilon, 1, -1), new(1, float.Epsilon, -1)];
        MapRoofMode[] modes = [MapRoofMode.Auto, MapRoofMode.Auto, MapRoofMode.Auto, MapRoofMode.Auto, MapRoofMode.Auto,
            MapRoofMode.Auto, MapRoofMode.Auto, MapRoofMode.AlwaysVisible, MapRoofMode.AlwaysHidden, MapRoofMode.AlwaysHidden,
            MapRoofMode.Auto, MapRoofMode.Auto];
        var membership = new MapSpaceMembership(scene.SurfaceView);
        var expected = positions.Select(p => membership.Query(new(frame, p))).ToArray();
        var status = MapMembershipPreparation.Measure(scene, out var demand, out _);
        Assert.Equal(MapSceneStatus.CompleteForScope, status);
        var admission = new MapSceneAdmission(scene.Source.Data.Limits);
        Assert.True(admission.TryReservePreparation(demand.Bytes, demand.Faces));
        MapSpaceMembershipScratch scratch;
        using (MapSceneAdmission.Suspend()) scratch = new(scene.SurfaceView, demand);
        for (int i = 0; i < positions.Length; i++)
        {
            var observed = membership.Query(new(frame, positions[i]), scratch);
            Assert.Equal(expected[i].Status, observed.Status);
            Assert.Equal(expected[i].SpaceId, observed.SpaceId);
            Assert.Equal(expected[i].ExactPoint, observed.ExactPoint);
            Assert.Same(expected[i].Witness, observed.Witness);
        }
        var statuses = new MapMembershipStatus[positions.Length];
        var spaces = new string?[positions.Length];
        var colour = new bool[positions.Length];
        long allocated = AllocationMeasurement.Measure("moving prepared roof membership", () =>
        {
            for (int i = 0; i < positions.Length; i++)
            {
                buffer.Evaluate(new(frame, positions[i]), modes[i]);
                statuses[i] = buffer.Status;
                spaces[i] = buffer.SpaceId;
                colour[i] = buffer.Roofs[0].ColourVisible;
            }
        });

        Assert.Equal(expected.Select(p => p.SpaceId), spaces);
        Assert.Equal(expected.Select(p => p.Status), statuses);
        Assert.Equal(new[] { false, false, false, false, false, true, true, true, false, false, true, true }, colour);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PreparedExactPredicates_MatchReleasedAtInteriorAndBoundaryWithoutAllocation()
    {
        var triangle = new MapExactTriangle(new(new(-3, 7), new(2, 3), new(-2, 5)),
            new(new(9, 5), new(7, 11), new(-1, 7)), new(new(2, 9), new(13, 17), new(9, 7)));
        var prepared = new MapMembershipTriangle(triangle);
        var exact = new MapMembershipExact();
        MapExactXz[] points = [new(triangle.A.X, triangle.A.Z), new(triangle.B.X, triangle.B.Z),
            new(triangle.C.X, triangle.C.Z), new(new(1, 3), new(1, 5)), new(new(20, 1), new(-20, 1))];
        var expected = new MapExactValue?[points.Length];
        var actual = new MapExactValue?[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            expected[i] = MapSubdividedTriangle.Height(triangle, points[i].X, points[i].Z);
            actual[i] = prepared.Height(points[i], exact);
        }
        Assert.Equal(expected, actual);
        long bytes = AllocationMeasurement.Measure("prepared exact predicates", () =>
        {
            for (int i = 0; i < points.Length; i++) actual[i] = prepared.Height(points[i], exact);
        });
        Assert.Equal(expected, actual);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void PreparedWideConversion_PreservesNormalizedReleasedBinary64Operations()
    {
        var exact = new MapMembershipExact();
        BigInteger x = (BigInteger.One << 180) + 999, z = -(BigInteger.One << 119) - 123;
        BigInteger c = (BigInteger.One << 129) + 17, d = (BigInteger.One << 167) + 39;
        var affine = new MapMembershipAffine(x, z, c, d);
        MapExactXz[] points = [new(new(123456789, 313), new(-18923, 337)), new(new(2, 7), new(5, 11)), new(default, default)];
        var expected = points.Select(p =>
        {
            BigInteger n = x * p.X.Numerator * p.Z.Denominator + z * p.Z.Numerator * p.X.Denominator + c * p.X.Denominator * p.Z.Denominator;
            BigInteger denominator = d * p.X.Denominator * p.Z.Denominator;
            var gcd = BigInteger.GreatestCommonDivisor(n, denominator);
            return (double)(n / gcd) / (double)(denominator / gcd);
        }).ToArray();
        var actual = new double[points.Length];
        for (int i = 0; i < points.Length; i++) actual[i] = exact.ValueDouble(affine, points[i]);
        Assert.Equal(expected, actual);
        long bytes = AllocationMeasurement.Measure("normalized prepared binary64 conversion", () =>
        {
            for (int i = 0; i < points.Length; i++) actual[i] = exact.ValueDouble(affine, points[i]);
        });
        Assert.Equal(expected, actual);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void PreparedExactPrecisionRefusal_DoesNotAllocateAnException()
    {
        var triangle = new MapExactTriangle(new(default, default, default),
            new(new(1, 1), new(1, long.MaxValue), default), new(default, default, new(1, 1)));
        var point = new MapExactXz(new(1, 2), new(1, 4));
        Assert.Throws<MapExactOverflowException>(() => MapSubdividedTriangle.Height(triangle, point.X, point.Z));
        var prepared = new MapMembershipTriangle(triangle);
        var exact = new MapMembershipExact();
        Assert.Null(prepared.Height(point, exact));
        MapExactValue? actual = null;
        long bytes = AllocationMeasurement.Measure("prepared exact precision refusal", () => actual = prepared.Height(point, exact));
        Assert.Null(actual);
        Assert.True(exact.NotRepresentable);
        Assert.Equal(0, bytes);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("reflex-storeys")]
    [InlineData("peer-overlap")]
    [InlineData("uneven-parent")]
    [InlineData("nested")]
    [InlineData("transformed")]
    [InlineData("west-portal")]
    public void PreparedScratch_MatchesColdQueryAcrossRoomsAndExactBoundaries(string variant)
    {
        var fixture = variant == "basic" ? PrefabFixtures.Create() : PrefabFixtures.Cave(variant);
        var resources = PrefabFixtures.Effective(fixture);
        var full = resources.CompleteView();
        var rectangle = full.Scope.WorldRectangle();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scope = new MapSceneScope(full.Scope, new(rectangle.MinX.ToDouble(), -500, rectangle.MinZ.ToDouble(),
            rectangle.MaxX.ToDouble(), 500, rectangle.MaxZ.ToDouble()), MapSceneChannels.All);
        using var documentSource = MapDocumentSource.FromDocument(native.Document);
        var captured = MapSceneSource.Capture(documentSource, native.Assets, new(MaxPreparedBytes: 536870912));
        Assert.NotNull(captured.Source);
        var acquired = captured.Source!.Acquire(scope);
        Assert.True(acquired.Witness.CompleteForScope, string.Join("\n", acquired.Findings));
        var scene = acquired.Scene!;
        Assert.Equal(MapSceneStatus.CompleteForScope, MapMembershipPreparation.Measure(scene, out var demand, out _));
        var admission = new MapSceneAdmission(scene.Source.Data.Limits);
        Assert.True(admission.TryReservePreparation(demand.Bytes, demand.Faces), $"Membership demand: {demand}");
        MapSpaceMembershipScratch scratch;
        using (MapSceneAdmission.Suspend()) scratch = new(scene.SurfaceView, demand);
        var membership = new MapSpaceMembership(scene.SurfaceView);
        var preparation = MapRoofEvaluationBuffer.Prepare(scene);
        Assert.True(preparation.Buffer is not null, preparation.Detail);
        var buffer = preparation.Buffer!;
        Vector3[] local = [new(1.234567f, 1, -1.345678f), new(2, 1, -2), new(0, 1, -1), new(4, 1, -2),
            new(2, 1, 0), new(2, 1, -4), new(0, 1, 0), new(4, 3, -4), new(1.25f, 4, -1.75f), new(9, 1, -9)];
        var points = new MapFramePoint[local.Length];
        var observations = new MapMembershipObservation[local.Length];
        for (int i = 0; i < local.Length; i++)
        {
            var world = PrefabFixtures.RoomProbe(resources, local[i].X, local[i].Y, local[i].Z);
            var point = MapEffectiveGeometry.Presentation(scene.SurfaceView, world);
            points[i] = point;
            var expected = membership.Query(point);
            var actual = membership.Query(point, scratch);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.SpaceId, actual.SpaceId);
            Assert.Equal(expected.ExactPoint, actual.ExactPoint);
            Assert.Same(expected.Witness, actual.Witness);
            buffer.Evaluate(point, MapRoofMode.Auto);
            Assert.Equal(expected.Status, buffer.Status);
            Assert.Equal(expected.SpaceId, buffer.SpaceId);
        }
        long bytes = AllocationMeasurement.Measure("moving prepared room variants", () =>
        {
            for (int i = 0; i < points.Length; i++)
            {
                observations[i] = membership.Query(points[i], scratch);
                buffer.Evaluate(points[i], MapRoofMode.Auto);
            }
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Prepare_AdmissionRefusalAllocatesOnlyItsTypedResult()
    {
        var fixture = PrefabFixtures.LinkedBuildings();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-32, -32), new(32, 32), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-32, -32, -32, 32, 32, 32), MapSceneChannels.All);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(scope).Scene!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 0));
        _ = MapRoofEvaluationBuffer.Prepare(scene, admission);
        MapRoofEvaluationBufferResult? result = null;
        long resultBytes = AllocationMeasurement.Measure("typed preparation result", () =>
            result = new(MapSceneStatus.CapacityExceeded, null, "Membership preparation exceeds the scene allowance."));
        long refusalBytes = AllocationMeasurement.Measure("refused membership preparation", () =>
            result = MapRoofEvaluationBuffer.Prepare(scene, admission));
        Assert.Equal(MapSceneStatus.CapacityExceeded, result!.Status);
        Assert.Null(result.Buffer);
        Assert.Equal(resultBytes, refusalBytes);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Throws<ArgumentNullException>(() => MapRoofEvaluationBuffer.Prepare(null!));
    }

    [Fact]
    public void Prepare_SharedAdmissionChargesAggregateBeforeCreatingAnotherBuffer()
    {
        var fixture = PrefabFixtures.LinkedBuildings();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-32, -32), new(32, 32), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-32, -32, -32, 32, 32, 32), MapSceneChannels.All);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(scope).Scene!;
        Assert.Equal(MapSceneStatus.CompleteForScope, MapMembershipPreparation.Measure(scene, out var demand, out _));
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: demand.Bytes, MaxPreparedFaces: (int)demand.Faces));
        var prepared = MapRoofEvaluationBuffer.Prepare(scene, admission);
        Assert.NotNull(prepared.Buffer);
        Assert.Equal(demand.Bytes, admission.RetainedBytes);
        Assert.Equal(demand.Faces, admission.PreparedFaces);
        MapRoofEvaluationBufferResult? second = null;
        long resultBytes = AllocationMeasurement.Measure("aggregate refusal result", () => second = new(MapSceneStatus.CapacityExceeded, null, ""));
        long bytes = AllocationMeasurement.Measure("aggregate membership admission", () => second = MapRoofEvaluationBuffer.Prepare(scene, admission));
        Assert.Equal(MapSceneStatus.CapacityExceeded, second!.Status);
        Assert.Null(second.Buffer);
        Assert.Equal(demand.Bytes, admission.RetainedBytes);
        Assert.Equal(demand.Faces, admission.PreparedFaces);
        Assert.Equal(resultBytes, bytes);
    }

    [Theory]
    [InlineData(false, MapSceneStatus.Invalid)]
    [InlineData(true, MapSceneStatus.NotRepresentable)]
    public void Prepare_InvalidOrUnrepresentableBorrowedInputAllocatesOnlyItsTypedResult(bool precision, MapSceneStatus expected)
    {
        var fixture = PrefabFixtures.LinkedBuildings();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-32, -32), new(32, 32), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-32, -32, -32, 32, 32, 32), MapSceneChannels.All);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(scope).Scene!;
        // Deliberately corrupt an internally borrowed test payload. Public scene consumers cannot mutate it.
        var reference = scene.SurfaceView.Witness.Records.First(r => r.Id == "room-a");
        Assert.True(scene.SurfaceView.TryAcquiredPatch(reference.Anchor, out var patch, out _));
        if (precision) Array.Fill(patch!.Heights, int.MaxValue);
        else
        {
            int index = patch!.Records.FindIndex(r => r.Id == reference.Id);
            patch.Records[index] = ((MapSpaceDoc)patch.Records[index]) with { AliasOf = reference };
        }
        var warm = MapRoofEvaluationBuffer.Prepare(scene);
        Assert.Equal(expected, warm.Status);
        MapRoofEvaluationBufferResult? result = null;
        long resultBytes = AllocationMeasurement.Measure("typed refused result", () => result = new(expected, null, warm.Detail));
        long refusedBytes = AllocationMeasurement.Measure("refused prepared metadata", () => result = MapRoofEvaluationBuffer.Prepare(scene));
        Assert.Equal(expected, result!.Status);
        Assert.Null(result.Buffer);
        Assert.Equal(resultBytes, refusedBytes);
    }
}
