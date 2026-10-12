using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public class MapPrefabWaterTests
{
    [Theory]
    [InlineData(0.8f, 5.3999996f)]
    [InlineData(1.2f, 5.6000004f)]
    public void LocalLevelCoversContourAndBounds_TransformOnce(float scale, float surfaceY)
    {
        var f = PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f));
        var r = PrefabFixtures.Effective(f);
        var view = r.CompleteView();
        var body = Assert.Single(MapWaterCompiler.Compile(view, r.Water!));
        Assert.Equal(MapWaterCompileStatus.Complete, body.Status);
        Assert.Equal(surfaceY, body.SurfaceY);
        Assert.Equal("a/pool-water", body.BodyId);
        Assert.NotEmpty(body.Regions);
        Assert.All(body.Regions, region =>
        {
            Assert.Equal("a/pool", region.SpaceId);
            Assert.NotNull(region.Fragment);
            Assert.NotEmpty(region.Triangles);
            Assert.All(region.Triangles, t => Assert.True(MapWaterPolygons.Cross(MapWideXz.From(new(t.A.X, t.A.Z)),
                MapWideXz.From(new(t.B.X, t.B.Z)), MapWideXz.From(new(t.C.X, t.C.Z))).Sign < 0));
        });
        Assert.NotEmpty(body.FragmentBounds);
        Assert.All(body.FragmentBounds, b =>
        {
            Assert.Equal("a/pool", b.Fragment.SpaceId);
            Assert.Equal("a/pool-domain", b.Fragment.Lower.OwnerId);
            Assert.Equal("a/pool-domain", b.Fragment.Upper.OwnerId);
            Assert.True(b.Bounds.MinY < surfaceY);
            Assert.Equal(surfaceY, b.Bounds.MaxY);
        });
        var sample = new MapWaterQuery(view, r.Water!).Sample(PrefabFixtures.PoolProbe(r));
        Assert.Equal((MapWaterStatus.Wet, body.BodyId, body.SurfaceY), (sample.Status, sample.BodyId, sample.NominalSurfaceY));
        Assert.NotNull(sample.Fragment);
        Assert.Equal(MapWaterRegionKind.FreeSurface, body.ClassifyFragment(sample.Fragment.Id, sample.Point.ExactWorldXz()));
        Assert.True(MapWaterContainment.Validate(view, r.Water!).Complete);
        Assert.Empty(MapWaterContainment.Validate(view, r.Water!).Errors);
        var reloaded = PrefabFixtures.Effective(PrefabFixtures.ReloadPrisms(f));
        var again = Assert.Single(MapWaterCompiler.Compile(reloaded.CompleteView(), reloaded.Water!));
        Assert.Equal(body.Digest, again.Digest);
        Assert.Equal(body.FragmentBounds, again.FragmentBounds);
    }

    [Fact]
    public void ParentWater_ExcludesAnUncoveredNestedRoom()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.ExteriorWaterDryRoom());
        var view = r.CompleteView();
        var point = PrefabFixtures.Point(r, 2, 1, -2);
        var query = new MapWaterQuery(view, r.Water!);
        var dry = query.Sample(point);
        Assert.Equal(MapWaterStatus.Dry, dry.Status);
        Assert.Equal("a/room", dry.SpaceId);
        var body = Assert.Single(MapWaterCompiler.Compile(view, r.Water!));
        Assert.Equal(MapWaterCompileStatus.Complete, body.Status);
        Assert.Equal(MapWaterRegionKind.Unresolved, body.Classify("exterior", point.ExactWorldXz()));
        var parents = MapSpaceIntervals.Column(view, point).Where(x => x.SpaceId == "exterior").ToArray();
        Assert.Equal(2, parents.Length);
        Assert.NotEqual(parents[0].Fragment!.Id, parents[1].Fragment!.Id);
        Assert.Equal(MapWaterRegionKind.Closed, body.ClassifyFragment(parents[0].Fragment!.Id, point.ExactWorldXz()));
        Assert.Equal(MapWaterRegionKind.FreeSurface, body.ClassifyFragment(parents[1].Fragment!.Id, point.ExactWorldXz()));
        Assert.Equal(MapWaterStatus.Wet, query.Sample(PrefabFixtures.Point(r, 2, -1, -2)).Status);
        Assert.Equal(MapWaterStatus.Wet, query.Sample(PrefabFixtures.Point(r, 2, 3.5f, -2)).Status);
        Assert.Equal(MapWaterStatus.Dry, query.SampleExact(new(new(2, 1), default, new(-2, 1))).Status);
        var tie = query.SampleExact(new(new(2, 1), new(3, 1), new(-2, 1)));
        Assert.Equal(MapWaterStatus.Wet, tie.Status);
        Assert.Equal(parents[1].Fragment!.Id, tie.Fragment!.Id);
        var column = query.Column(point);
        Assert.Equal(2, column.Count);
        Assert.Equal((-2f, 0f, false), (column[0].LowerY, column[0].UpperY, column[0].UpperIsFreeSurface));
        Assert.Equal((3f, 4f, true), (column[1].LowerY, column[1].UpperY, column[1].UpperIsFreeSurface));
        Assert.All(body.Regions, x => Assert.NotNull(x.Fragment));
    }

    [Fact]
    public void DryAndFloodedCaveVolumes_DoNotLeakWaterOrIndoorState()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.WaterCaves());
        var query = new MapWaterQuery(r.CompleteView(), r.Water!);
        Assert.Equal(MapWaterStatus.Dry, query.Sample(PrefabFixtures.DryCavePoint(r)).Status);
        Assert.Equal(MapWaterStatus.Wet, query.Sample(PrefabFixtures.FloodedCavePoint(r)).Status);
        var membership = new MapSpaceMembership(r.CompleteView());
        Assert.Equal("a/dry", membership.Query(PrefabFixtures.DryCavePoint(r)).SpaceId);
        Assert.Equal("a/flooded", membership.Query(PrefabFixtures.FloodedCavePoint(r)).SpaceId);
        Assert.All(MapWaterCompiler.Compile(r.CompleteView(), r.Water!), b => Assert.Equal(MapWaterCompileStatus.Complete, b.Status));
        Assert.Equal(-35, query.Sample(PrefabFixtures.FloodedCavePoint(r)).NominalSurfaceY);
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.2f)]
    public void SharedFloorWaterAndRoomEdge_AgreeExactly(float scale)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f)));
        var body = Assert.Single(MapWaterCompiler.Compile(r.CompleteView(), r.Water!));
        var floor = PrefabFixtures.SharedFloorCorner(r);
        var water = PrefabFixtures.SharedWaterCorner(r);
        var room = PrefabFixtures.SharedRoomCorner(r);
        Assert.Equal((floor.X, floor.Z), (water.X, water.Z));
        Assert.Equal((floor.X, floor.Z), (room.X, room.Z));
        var exact = new MapExactPoint(water.X, new MapResourceTransform(r.Transforms["WaterBody:a/pool-water"]).Height(default), water.Z);
        var membership = new MapSpaceMembership(r.CompleteView()).QueryExact(exact);
        var sample = new MapWaterQuery(r.CompleteView(), r.Water!).SampleExact(exact);
        Assert.Equal(MapWaterCompileStatus.Complete, body.Status);
        Assert.Equal(membership.SpaceId, sample.SpaceId);
        Assert.Equal(exact, sample.ExactPoint);
        Assert.Equal(membership.SpaceId == "a/pool" ? MapWaterStatus.Wet : MapWaterStatus.Dry, sample.Status);
        // An exact midpoint of the frozen pool edge, not a rounded transform of a purported boundary point.
        var a = PrefabFixtures.WaterExact(r, body.BodyId, 4, 0, -4);
        var b = PrefabFixtures.WaterExact(r, body.BodyId, 8, 0, -4);
        var half = new MapExactValue(1, 2);
        exact = new(a.X.Add(b.X.Subtract(a.X).Multiply(half)), a.Y, a.Z.Add(b.Z.Subtract(a.Z).Multiply(half)));
        membership = new MapSpaceMembership(r.CompleteView()).QueryExact(exact);
        sample = new MapWaterQuery(r.CompleteView(), r.Water!).SampleExact(exact);
        Assert.Equal(membership.SpaceId, sample.SpaceId);
        if (sample.Status == MapWaterStatus.Wet)
            Assert.Equal(MapWaterRegionKind.FreeSurface, body.ClassifyFragment(sample.Fragment!.Id, new(exact.X, exact.Z)));
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.2f)]
    public void CeilingTie_RemainsClosedAfterTransform(float scale)
    {
        var f = PrefabFixtures.WaterLevel(PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f)), 3);
        var r = PrefabFixtures.Effective(f);
        var body = Assert.Single(MapWaterCompiler.Compile(r.CompleteView(), r.Water!));
        Assert.Equal(MapWaterCompileStatus.Complete, body.Status);
        var sample = new MapWaterQuery(r.CompleteView(), r.Water!).Sample(PrefabFixtures.PoolProbe(r));
        Assert.Equal(MapWaterStatus.Wet, sample.Status);
        Assert.False(sample.UpperIsFreeSurface);
        Assert.Equal("a/pool-domain", sample.UpperBoundaryId);
        Assert.Equal(MapWaterRegionKind.Closed, body.ClassifyFragment(sample.Fragment!.Id, sample.Point.ExactWorldXz()));
    }

    [Theory]
    [InlineData("local-cover")]
    [InlineData("prism")]
    [InlineData("parent-floor")]
    [InlineData("remote-anchor")]
    public void MissingLocalCoverOrParent_IsUnverifiedNeverDry(string fault)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.RoomWater(seam: true));
        var control = Assert.Single(MapWaterCompiler.Compile(r.CompleteView(), r.Water!));
        Assert.Equal(MapWaterCompileStatus.Complete, control.Status);
        var point = PrefabFixtures.Point(r, 2, 0.25f, -2);
        Assert.Equal(MapWaterStatus.Wet, new MapWaterQuery(r.CompleteView(), r.Water!).Sample(point).Status);
        var view = PrefabFixtures.AcquireWaterFault(r, fault);
        var expected = PrefabFixtures.WaterFaults.Single(x => x.Fault == fault);
        Assert.Contains(new MapUnavailable(expected.What, expected.Key, expected.Status), view.Witness.Unavailable);
        Assert.True(view.TryRecord(new("a/door-top", new("a/floor", 0, 0)), out _, out _));
        Assert.True(view.TryRecord(new("a/pool", new("a/pool-floor", 0, 0)), out var unrelated, out _));
        Assert.IsType<MapSpaceDoc>(unrelated);
        var body = Assert.Single(MapWaterCompiler.Compile(view, r.Water!));
        Assert.Equal(MapWaterCompileStatus.Incomplete, body.Status);
        Assert.Contains(expected.Detail, body.Detail);
        var sample = new MapWaterQuery(view, r.Water!).Sample(point);
        Assert.Equal(MapWaterStatus.MissingGeometry, sample.Status);
        Assert.Contains(expected.Detail, sample.Detail);
    }

    [Fact]
    public void UnavailableAperture_PreventsCompleteContainment()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.RoomWater(connected: true));
        Assert.Equal(MapWaterCompileStatus.Complete, Assert.Single(MapWaterCompiler.Compile(r.CompleteView(), r.Water!)).Status);
        var view = PrefabFixtures.AcquireWaterFault(r, "aperture");
        Assert.Contains(new MapUnavailable("a/door-bottom", new("a/floor", 0, 0), MapPatchStatus.Missing), view.Witness.Unavailable);
        foreach (var id in new[] { "a/room", "a/room-domain", "a/room-portal", "a/door-top" })
            Assert.True(view.TryRecord(new(id, new("a/floor", 0, 0)), out _, out _));
        Assert.True(view.TryRecord(new("exterior", new("exterior", 0, 0)), out _, out _));
        var containment = MapWaterContainment.Validate(view, r.Water!);
        Assert.False(containment.Complete);
        Assert.Contains(containment.Errors, e => e.Contains("portal 'a/room-portal'", StringComparison.Ordinal) &&
            e.Contains("record 'a/door-bottom'", StringComparison.Ordinal) && e.Contains(new MapPatchKey("a/floor", 0, 0).ToString(), StringComparison.Ordinal) &&
            e.Contains("(Missing)", StringComparison.Ordinal));
        Assert.False(view.TryRecord(new("a/door-bottom", new("a/floor", 0, 0)), out _, out var geometryStatus));
        Assert.Equal(MapPatchStatus.Missing, geometryStatus);
        var body = Assert.Single(MapWaterCompiler.Compile(view, r.Water!));
        Assert.Equal(MapWaterCompileStatus.Incomplete, body.Status);
        Assert.Contains("a/door-bottom", body.Detail);
    }

    [Fact]
    public void UnrelatedAssetChildEdit_KeepsBodyDigest()
    {
        var f = PrefabFixtures.Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f));
        var first = PrefabFixtures.Effective(f);
        var second = PrefabFixtures.Effective(PrefabFixtures.MarkerOnlyWaterEdit(f));
        Assert.NotEqual(first.Digest, second.Digest);
        var a = Assert.Single(MapWaterCompiler.Compile(first.CompleteView(), first.Water!));
        var b = Assert.Single(MapWaterCompiler.Compile(second.CompleteView(), second.Water!));
        Assert.Equal(MapWaterCompileStatus.Complete, a.Status);
        Assert.Equal(MapWaterCompileStatus.Complete, b.Status);
        Assert.Equal(a.Digest, b.Digest);
    }

    [Fact]
    public void TangentContour_PreservesFragmentAndExactBoundaryProvenance()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.TangentWater());
        var body = MapWaterCompiler.Compile(r.CompleteView(), r.Water!).Single(b => b.BodyId == "a/lake");
        Assert.Equal(MapWaterCompileStatus.Complete, body.Status);
        Assert.NotEmpty(body.ClosedContours);
        Assert.All(body.ClosedContours, c =>
        {
            Assert.NotNull(c.FragmentId);
            Assert.NotNull(c.WideA);
            Assert.NotNull(c.WideB);
            Assert.Equal("a/flooded", c.SpaceId);
            var fragment = body.FragmentBounds.Single(b => b.Fragment.Id == c.FragmentId).Fragment;
            Assert.Equal("a/flooded-floor", fragment.Lower.OwnerId);
            Assert.Equal("a/flooded-ceiling", fragment.Upper.OwnerId);
        });
        var a = PrefabFixtures.WaterExact(r, "a/lake", 2, -37, -1);
        var b = PrefabFixtures.WaterExact(r, "a/lake", 2, -37, -2);
        var half = new MapExactValue(1, 2);
        var point = new MapExactPoint(a.X.Add(b.X.Subtract(a.X).Multiply(half)), a.Y, a.Z.Add(b.Z.Subtract(a.Z).Multiply(half)));
        var sample = new MapWaterQuery(r.CompleteView(), r.Water!).SampleExact(point);
        Assert.Equal(MapWaterStatus.Wet, sample.Status);
        Assert.False(sample.UpperIsFreeSurface);
        Assert.Equal(MapWaterRegionKind.Closed, body.ClassifyFragment(sample.Fragment!.Id, new(point.X, point.Z)));
        Assert.DoesNotContain(body.Regions, region => region.Kind == MapWaterRegionKind.Closed);
    }

    [Fact]
    public void RepackingAndCapturedSource_PreserveWaterFragmentsAndDigest()
    {
        var f = PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f));
        var before = PrefabFixtures.Effective(f);
        var stored = PrefabFixtures.Stored(f.Document);
        var after = MapPrefabResolver.Expand(f.Document, f.Assets, new(), null, stored);
        var a = Assert.Single(MapWaterCompiler.Compile(before.CompleteView(), before.Water!));
        var b = Assert.Single(MapWaterCompiler.Compile(after.CompleteView(), after.Water!));
        Assert.Equal(MapWaterCompileStatus.Complete, b.Status);
        Assert.Equal(a.Digest, b.Digest);
        Assert.Equal(a.FragmentBounds, b.FragmentBounds);
    }

    [Fact]
    public void QueryAndCompiler_SnapshotLocalWaterAndPose()
    {
        var f = PrefabFixtures.Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f));
        var r = PrefabFixtures.Effective(f);
        var water = r.Water!;
        var query = new MapWaterQuery(r.CompleteView(), water);
        var body = Assert.Single(MapWaterCompiler.Compile(r.CompleteView(), water));
        water.Bodies.Clear();
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with
        { Transform = new(new(64, 0, -64), 0, 1) } }, []);
        var sample = query.Sample(PrefabFixtures.PoolProbe(r));
        Assert.Equal(MapWaterStatus.Wet, sample.Status);
        Assert.Equal(body.SurfaceY, sample.NominalSurfaceY);
        Assert.Equal(MapWaterRegionKind.FreeSurface, body.ClassifyFragment(sample.Fragment!.Id, sample.Point.ExactWorldXz()));
    }

    [Fact]
    public void EffectiveWaterValidation_RequiresHydraulicConnectionRatherThanParentage()
    {
        var valid = PrefabFixtures.RoomWater(connected: true);
        var control = PrefabFixtures.Effective(valid);
        MapBoundDocumentValidation.ValidateEffectiveWater(control, valid.Assets);
        var invalid = PrefabFixtures.DisconnectedWater();
        MapBoundDocumentValidation.Validate(invalid.Document, invalid.Assets);
        var r = PrefabFixtures.Effective(invalid);
        var body = Assert.Single(MapWaterCompiler.Compile(r.CompleteView(), r.Water!));
        Assert.Equal(MapWaterCompileStatus.Invalid, body.Status);
        Assert.Contains("not connected below its surface", body.Detail);
        Assert.Contains("not connected below its surface", Assert.Throws<MapDocumentException>(() =>
            MapBoundDocumentValidation.ValidateEffectiveWater(r, invalid.Assets)).Message);
    }

    [Fact]
    public void ConsumedApertureTransform_ChangesBodyDigestWithoutChangingRegions()
    {
        var before = PrefabFixtures.Effective(PrefabFixtures.ExternalApertureWater(0));
        var after = PrefabFixtures.Effective(PrefabFixtures.ExternalApertureWater(0.125f));
        var a = Assert.Single(MapWaterCompiler.Compile(before.CompleteView(), before.Water!));
        var b = Assert.Single(MapWaterCompiler.Compile(after.CompleteView(), after.Water!));
        Assert.Equal(MapWaterCompileStatus.Complete, a.Status);
        Assert.Equal(MapWaterCompileStatus.Complete, b.Status);
        Assert.Equal(a.Regions.SelectMany(r => r.Triangles), b.Regions.SelectMany(r => r.Triangles));
        Assert.NotEqual(a.Digest, b.Digest);
    }

    [Fact]
    public void MissingPrefabBytes_RefusesExpansionAndCapturedWaterNeverRereadsThem()
    {
        var (roots, source) = PrefabFixtures.WaterCaptureInputs();
        var f = PrefabFixtures.Create("all-resources");
        source.MissingPrefab = true;
        var error = Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(roots, source));
        Assert.Contains("hut", error.Message);
        Assert.Contains(f.Assets.GetResource("hut").Reference.Sha256, error.Message);
        source.MissingPrefab = false;
        var captured = MapAssetClosure.Load(roots, source);
        var r = MapPrefabResolver.Expand(f.Document, captured);
        int reads = source.PrefabReads;
        source.MissingPrefab = true;
        Assert.Equal(MapWaterCompileStatus.Complete, Assert.Single(MapWaterCompiler.Compile(r.CompleteView(), r.Water!)).Status);
        Assert.Equal(MapWaterStatus.Wet, new MapWaterQuery(r.CompleteView(), r.Water!).Sample(PrefabFixtures.PoolProbe(r)).Status);
        Assert.Equal(reads, source.PrefabReads);
    }

    [Fact]
    public void ConsumedParentBounds_EnterDigestEvenWhenThePrismKeepsItsFloor()
    {
        var f = PrefabFixtures.Create("all-resources");
        var before = PrefabFixtures.Effective(f);
        foreach (var patch in f.Document.Surfaces.Patches.Values) Array.Fill(patch.Heights, -150);
        PrefabFixtures.SealWorldParent(f.Document);
        var after = PrefabFixtures.Effective(f);
        var a = Assert.Single(MapWaterCompiler.Compile(before.CompleteView(), before.Water!));
        var b = Assert.Single(MapWaterCompiler.Compile(after.CompleteView(), after.Water!));
        Assert.Equal(MapWaterCompileStatus.Complete, a.Status);
        Assert.Equal(MapWaterCompileStatus.Complete, b.Status);
        Assert.Equal(-1, a.DeepestBedY);
        Assert.Equal(-1, b.DeepestBedY);
        Assert.Equal(a.Regions.SelectMany(r => r.Triangles), b.Regions.SelectMany(r => r.Triangles));
        Assert.NotEqual(a.Digest, b.Digest);
    }

    [Theory]
    [InlineData("scale")]
    [InlineData("yaw")]
    [InlineData("position")]
    public void ReleasedPlacementRefusal_KeepsItsDetail(string fault)
    {
        var placement = new MapWaterPlacement(new(10.25f, 5, -3.125f), 0.371f, 0.8f);
        placement = fault switch
        {
            "scale" => placement with { Scale = 0 },
            "yaw" => placement with { Yaw = float.NaN },
            _ => placement with { Position = new(10.25f, float.NaN, -3.125f) },
        };
        var error = Assert.Throws<MapDocumentException>(() => MapWaterDomain.Compile(PrefabFixtures.Domain(0, 0, 4, 4), placement));
        Assert.Equal("water domain: placement must be finite with a positive scale.", error.Message);
    }

    [Fact]
    public void UnrelatedWorldWater_KeepsReleasedRegionsDigestAndWork()
    {
        var f = PrefabFixtures.WorldAndLocalWater();
        var withPrefab = PrefabFixtures.Effective(f);
        f.Document.Prefabs = null;
        var without = PrefabFixtures.Effective(f);
        var a = MapWaterCompiler.Compile(withPrefab.CompleteView(), withPrefab.Water!).Single(b => b.BodyId == "world-ocean");
        var b = Assert.Single(MapWaterCompiler.Compile(without.CompleteView(), without.Water!));
        Assert.Equal(MapWaterCompileStatus.Complete, a.Status);
        Assert.Equal(MapWaterCompileStatus.Complete, b.Status);
        Assert.Equal(b.Digest, a.Digest);
        Assert.Equal(b.Work, a.Work);
        Assert.Equal(b.Bounds, a.Bounds);
        Assert.Equal(b.Regions.SelectMany(r => r.Triangles), a.Regions.SelectMany(r => r.Triangles));
        Assert.All(a.Regions, r => Assert.Null(r.Fragment));
    }

    [Fact]
    public void RefusedAcquisition_PreservesCapacityInCompilerAndQuery()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f)));
        var scope = r.CompleteView().Scope;
        var refused = r.Acquire(scope with { Limits = scope.Limits with { MaxCandidatePatches = 0 } });
        Assert.Equal(MapAcquireStatus.CapacityExceeded, refused.Status);
        Assert.Empty(refused.Witness.Unavailable);
        var body = Assert.Single(MapWaterCompiler.Compile(refused, r.Water!));
        Assert.Equal(MapWaterCompileStatus.CapacityExceeded, body.Status);
        Assert.Empty(body.Regions);
        Assert.Contains("capacity", body.Detail);
        var sample = new MapWaterQuery(refused, r.Water!).Sample(PrefabFixtures.PoolProbe(r));
        Assert.Equal(MapWaterStatus.CapacityExceeded, sample.Status);
    }

    [Theory]
    [InlineData("pieces")]
    [InlineData("vertices")]
    [InlineData("footprint")]
    [InlineData("faces")]
    [InlineData("pairs")]
    [InlineData("triangles")]
    public void ExistingCompileLimits_RefuseBeforeEffectiveWork(string kind)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f)));
        var limits = kind switch
        {
            "pieces" => new MapWaterCompileLimits(MaxPieces: 0),
            "vertices" => new(MaxPieceVertices: 0),
            "footprint" => new(MaxFootprintCells: 0),
            "faces" => new(MaxBoundFaces: 0),
            "pairs" => new(MaxPairChecks: 0),
            _ => new(MaxTriangles: 0),
        };
        var body = Assert.Single(MapWaterCompiler.Compile(r.CompleteView(), r.Water!, limits));
        Assert.Equal(MapWaterCompileStatus.CapacityExceeded, body.Status);
        Assert.Empty(body.Regions);
        Assert.Empty(body.ClosedContours);
        Assert.Empty(body.FragmentBounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingCompileLimits_ChargeRotatedAndAbsentBoundWork(bool absent)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f)));
        var view = absent ? PrefabFixtures.AcquireWaterFault(r, "parent-floor") : r.CompleteView();
        var body = Assert.Single(MapWaterCompiler.Compile(view, r.Water!, new(MaxBoundCells: 0)));
        Assert.Equal(MapWaterCompileStatus.CapacityExceeded, body.Status);
        Assert.Contains("water bound cells", body.Detail);
        Assert.True(body.Work.BoundCells + body.Work.BoundSlots > 0);
        Assert.Empty(body.Regions);
        body = Assert.Single(MapWaterCompiler.Compile(r.CompleteView(), r.Water!, new(MaxTriangles: 0)));
        Assert.Equal(MapWaterCompileStatus.CapacityExceeded, body.Status);
        Assert.Contains("water triangles", body.Detail);
    }
}
