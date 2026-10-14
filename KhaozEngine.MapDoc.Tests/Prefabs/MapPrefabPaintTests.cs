using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabPaintTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedWorldPaint_PublicResolutionDoesNotAddPriorityConflicts(bool unrelatedPrefab)
    {
        var f = PrefabFixtures.ReleasedWorldPaint(unrelatedPrefab: unrelatedPrefab);
        Assert.All(f.Document.Surfaces.Refs, s => Assert.Equal(0, s.PaintPriority));
        var authored = f.Document.Surfaces;
        var ground = authored.Refs.Single(s => s.Id == "ground");
        var original = MapSurfaceCompiler.Compile(ground, authored.Patches[new("ground", 0, 0)]);
        var first = authored.Refs.Single(s => s.Id == "priority-low");
        var last = authored.Refs.Single(s => s.Id == "priority-high");
        var whole = MapSurfaceCompiler.ApplyPaintOverride(MapSurfaceCompiler.ApplyPaintOverride(original,
            first, authored.Patches[new(first.Id, 0, 0)]), last, authored.Patches[new(last.Id, 0, 0)]);
        Assert.All(whole.Paint.Where(p => p.Face.Primitive == 1), p => Assert.Equal((ushort)8, p.Underlay));
        Assert.Equal(original.ExactVertices, whole.ExactVertices);
        Assert.Equal(original.Faces, whole.Faces);
        string before = MapDocumentFile.SaveText(f.Document);

        var resolved = MapNativeResolution.Resolve(f.Document, f.Assets, PrefabFixtures.Options());

        Assert.Contains(resolved.Placements, p => p.PlacementId == "released-item" &&
            p.Transform.Position == new System.Numerics.Vector3(0.25f, 2, -0.25f));
        Assert.Equal(unrelatedPrefab ? 2 : 1, resolved.Placements.Count);
        Assert.Empty(resolved.EffectiveResources!.PaintRegions);
        var actual = resolved.EffectiveResources.Surfaces;
        var physical = MapSurfaceCompiler.Compile(actual.Refs.Single(s => s.Id == "ground"), actual.Patches[new("ground", 0, 0)]);
        Assert.Equal(original.ExactVertices, physical.ExactVertices);
        Assert.Equal(original.Faces, physical.Faces);
        Assert.Equal(original.Paint, physical.Paint);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    }

    [Fact]
    public void ReleasedWorldPaint_PublicResolutionDoesNotChargePartialPaintBudgets()
    {
        var f = PrefabFixtures.ReleasedWorldPaint(large: true);
        Assert.Null(f.Document.Prefabs);
        long sourceFaces = f.Document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == "priority-low")
            .Sum(p => (long)MapSurfaceCompiler.Compile(f.Document.Surfaces.Refs.Single(s => s.Id == p.Key.SurfaceId)
                with
            { Role = MapSurfaceRole.SupportFloor }, p).Faces.Count);
        Assert.True(sourceFaces > new MapRefinementLimits().MaxSourceFacesPerBound);
        string before = MapDocumentFile.SaveText(f.Document);

        var resolved = MapNativeResolution.Resolve(f.Document, f.Assets, PrefabFixtures.Options());

        Assert.Equal("released-item", Assert.Single(resolved.Placements).PlacementId);
        Assert.Empty(resolved.EffectiveResources!.PaintRegions);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrefabPaint_ReleasedWorldPaintDoesNotJoinConflictsOrBudgetCharges(bool large)
    {
        var plain = PrefabFixtures.Effective(PrefabFixtures.Create("uneven-painted"));
        var mixed = PrefabFixtures.Create("uneven-painted");
        PrefabFixtures.AddReleasedWorldPaint(mixed, large, large ? 0 : 2);

        var resolved = MapNativeResolution.Resolve(mixed.Document, mixed.Assets, PrefabFixtures.Options());

        var regions = resolved.EffectiveResources!.PaintRegions["ground"];
        Assert.NotEmpty(regions.Regions);
        Assert.All(regions.Regions, r => Assert.Equal("a/paint", r.PaintId));
        Assert.Equal(plain.PaintRegions["ground"].Digest, regions.Digest);
    }

    [Fact]
    public void PartialPaint_LeavesFacesAndSupportByteIdentical()
    {
        var plain = PrefabFixtures.Effective(PrefabFixtures.Create("uneven-unpainted"));
        var painted = PrefabFixtures.Effective(PrefabFixtures.Create("uneven-painted"));
        var a = PrefabFixtures.CanonicalPatch(plain, "ground");
        var b = PrefabFixtures.CanonicalPatch(painted, "ground");
        var regions = MapPartialPaintCompiler.Compile(painted.CompleteView(), "ground");
        Assert.Equal(a.ExactVertices, b.ExactVertices);
        Assert.Equal(a.Faces, b.Faces);
        Assert.Equal(a.LegacyFallbackCells, b.LegacyFallbackCells);
        Assert.Equal(a.VertexIds, b.VertexIds);
        Assert.Equal(a.Offsets, b.Offsets);
        Assert.Equal(a.Paint, b.Paint);
        Assert.NotEmpty(regions.Regions);
        Assert.All(regions.Regions, r => Assert.Contains(b.Faces, face => face.Key == r.Face));
        Assert.Equal(PrefabFixtures.PaintDomainArea(painted), PrefabFixtures.PaintArea(regions));
        Assert.InRange(PrefabFixtures.PaintArea(regions).ToDouble(), 3.999996d, 4.000004d);
        Assert.Equal(regions.Digest, painted.PaintRegions["ground"].Digest);
    }

    [Fact]
    public void PaintConflict_RequiresExplicitPriority()
    {
        Assert.Contains("equal-priority", Assert.Throws<MapDocumentException>(() =>
            PrefabFixtures.Effective(PrefabFixtures.Create("equal-priority-paint"))).Message);
        var conflict = PrefabFixtures.Create("equal-priority-paint");
        Assert.Contains("equal-priority", Assert.Throws<MapDocumentException>(() =>
            MapNativeResolution.Resolve(conflict.Document, conflict.Assets, PrefabFixtures.Options())).Message);
        var ordered = PrefabFixtures.Effective(PrefabFixtures.Create("prioritized-paint"));
        var regions = ordered.PaintRegions["ground"].Regions;
        Assert.NotEmpty(regions);
        Assert.Equal(new[] { "priority-high", "priority-low" }, regions.Select(x => x.PaintId).Distinct().Order());
        Assert.True(PrefabFixtures.HighPriorityOwnsOverlap(ordered));
        Assert.Equal(new MapExactValue(8, 1), PrefabFixtures.PaintArea(ordered.PaintRegions["ground"]));
    }

    [Fact]
    public void EqualPriorityIdenticalMaterial_CoalescesInOrdinalIdOrder()
    {
        var f = PrefabFixtures.Create("identical-paint");
        var regions = PrefabFixtures.Effective(f).PaintRegions["ground"];
        Assert.Equal(new MapExactValue(8, 1), PrefabFixtures.PaintArea(regions));
        Assert.Equal(new MapExactValue(6, 1), PrefabFixtures.PaintArea(new(regions.Regions.Where(r => r.PaintId == "priority-high").ToArray(), "")));
        f.Document.Surfaces.Refs.Reverse();
        Assert.Equal(regions.Digest, PrefabFixtures.Effective(f).PaintRegions["ground"].Digest);
    }

    [Fact]
    public void MoveUndoReload_DoesNotWriteWorldMaterialArrays()
    {
        var f = PrefabFixtures.Create("uneven-painted");
        byte[] before = MapSurfacePatchCodec.Encode(f.Document.Surfaces.Patches[new("ground", 0, 0)]);
        string saved = MapDocumentFile.SaveText(f.Document);
        string digest = PrefabFixtures.Effective(f).PaintRegions["ground"].Digest;
        var instance = f.Document.Prefabs!.Instances.Single();
        f.Document.Prefabs = new(1, 1, new[] { instance with
        { Transform = instance.Transform with { Position = instance.Transform.Position + new System.Numerics.Vector3(1, 5, 0) } } }, []);
        Assert.NotEqual(digest, PrefabFixtures.Effective(f).PaintRegions["ground"].Digest);
        Assert.Equal(before, MapSurfacePatchCodec.Encode(f.Document.Surfaces.Patches[new("ground", 0, 0)]));
        var reloaded = new PrefabFixture(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets);
        Assert.Equal(before, MapSurfacePatchCodec.Encode(reloaded.Document.Surfaces.Patches[new("ground", 0, 0)]));
        var undone = new PrefabFixture(MapDocumentFile.LoadText(saved), f.Assets);
        Assert.Equal(digest, PrefabFixtures.Effective(undone).PaintRegions["ground"].Digest);
        f.Document.Prefabs = new(1, 1, new[] { instance with { Removed = true } }, []);
        Assert.Empty(PrefabFixtures.Effective(f).PaintRegions);
        Assert.Equal(before, MapSurfacePatchCodec.Encode(f.Document.Surfaces.Patches[new("ground", 0, 0)]));
    }

    [Fact]
    public void PaintAbsentNotch_LeavesUnderlyingMaterialVisible()
    {
        var resources = PrefabFixtures.Effective(PrefabFixtures.Create("paint-notch"));
        var transform = new MapResourceTransform(resources.Transforms["Surface:a/paint"]);
        var notch = transform.Point(new(new(3, 2), default, new(-3, 2)));
        Assert.DoesNotContain(resources.PaintRegions["ground"].Regions, r => PrefabFixtures.PaintContains(r.Polygon, notch));
        var ground = PrefabFixtures.CanonicalPatch(resources, "ground");
        Assert.Contains(ground.Faces, face => PrefabFixtures.PaintContains(new[]
        { ground.ExactTriangle(face).A, ground.ExactTriangle(face).B, ground.ExactTriangle(face).C }, notch));
        Assert.All(ground.Paint, material => Assert.Equal((ushort)3, material.Underlay));
        Assert.InRange(PrefabFixtures.PaintArea(resources.PaintRegions["ground"]).ToDouble(), 2.999996d, 3.000004d);
    }

    [Fact]
    public void PaintHole_PreservesTheExactOuterMinusInnerFootprint()
    {
        var resources = PrefabFixtures.Effective(PrefabFixtures.Create("paint-hole"));
        var transform = new MapResourceTransform(resources.Transforms["Surface:a/paint"]);
        MapExactPoint Point(int x, int z) => transform.Point(new(new(x, 1), default, new(z, 1)));
        var outer = PrefabFixtures.PaintPolygonArea(new[] { Point(0, 0), Point(3, 0), Point(3, -3), Point(0, -3) });
        var inner = PrefabFixtures.PaintPolygonArea(new[] { Point(1, -1), Point(2, -1), Point(2, -2), Point(1, -2) });
        Assert.True(outer.Subtract(inner).TryNarrow(out var expected));
        Assert.Equal(expected, PrefabFixtures.PaintArea(resources.PaintRegions["ground"]));
        var hole = transform.Point(new(new(3, 2), default, new(-3, 2)));
        Assert.DoesNotContain(resources.PaintRegions["ground"].Regions, r => PrefabFixtures.PaintContains(r.Polygon, hole));
    }

    [Fact]
    public void RenderSubdivisions_LieOnTheirParentTriangle()
    {
        var resources = PrefabFixtures.Effective(PrefabFixtures.Create("uneven-painted"));
        var patch = PrefabFixtures.CanonicalPatch(resources, "ground");
        foreach (var region in resources.PaintRegions["ground"].Regions)
        {
            var triangle = patch.ExactTriangle(patch.Faces.Single(f => f.Key == region.Face));
            foreach (var point in region.Polygon)
            {
                Assert.True(PrefabFixtures.PaintContains(new[] { triangle.A, triangle.B, triangle.C }, point));
                // The authored target is y = x / 4. Paint is at y = 99 and must never supply support.
                Assert.Equal(MapWideRational.From(point.X).Divide(MapWideRational.From(4)), MapWideRational.From(point.Y));
            }
        }
    }

    [Fact]
    public void PaintBudgetAndUnrepresentableIntersection_Refuse()
    {
        var resources = PrefabFixtures.Effective(PrefabFixtures.Create("uneven-painted"));
        foreach (var limits in new[] { new MapRefinementLimits(MaxPairChecks: 0), new(MaxRefinementFaces: 0),
            new(MaxRefinementVertices: 0), new(MaxContextPatches: 0), new(MaxContextFaces: 0), new(MaxSourceFacesPerBound: 0) })
            Assert.Contains("CapacityExceeded", Assert.Throws<MapDocumentException>(() =>
                MapPartialPaintCompiler.Compile(resources.CompleteView(), "ground", limits)).Message);
        var unrepresentable = PrefabFixtures.Create("uneven-painted");
        int index = unrepresentable.Document.Surfaces.Refs.FindIndex(s => s.Id == "ground");
        var surface = unrepresentable.Document.Surfaces.Refs[index];
        unrepresentable.Document.Surfaces.Refs[index] = surface with { Frame = surface.Frame with { HeightUnitMetres = new(1, int.MaxValue) } };
        Assert.Contains("NotRepresentable", Assert.Throws<MapDocumentException>(() => PrefabFixtures.Effective(unrepresentable)).Message);
    }

    [Fact]
    public void PaintPriority_RoundTripsAndDefaultKeepsReleasedDigest()
    {
        var f = PrefabFixtures.ReleasedWorldPaint();
        int paintIndex = f.Document.Surfaces.Refs.FindIndex(s => s.Id == "priority-high");
        f.Document.Surfaces.Refs[paintIndex] = f.Document.Surfaces.Refs[paintIndex] with { PaintPriority = 2 };
        string json = MapDocumentFile.SaveText(f.Document);
        var loaded = MapDocumentFile.LoadText(json);
        Assert.Equal(2, loaded.Surfaces.Refs.Single(s => s.Id == "priority-high").PaintPriority);
        Assert.Equal(2, loaded.Surfaces.Clone().Refs.Single(s => s.Id == "priority-high").PaintPriority);
        var surface = f.Document.Surfaces.Refs.Single(s => s.Id == "priority-high");
        Assert.NotEqual(MapSurfaceSemantics.SurfaceDigest(surface, []), MapSurfaceSemantics.SurfaceDigest(surface with { PaintPriority = 0 }, []));
        var ground = f.Document.Surfaces.Refs.Single(s => s.Id == "ground");
        Assert.Equal("9c38cc1e97c7e19376e1891bc58bf14f73dc2f07bc67de326cf2bc0aa4bb1808", MapSurfaceSemantics.SurfaceDigest(ground, []));
        Assert.DoesNotContain("paintPriority", JsonNode.Parse(json)!["surfaces"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == "ground")!.ToJsonString());
        int groundIndex = loaded.Surfaces.Refs.FindIndex(s => s.Id == "ground");
        loaded.Surfaces.Refs[groundIndex] = ground with { PaintPriority = 1 };
        Assert.Contains("paintPriority", Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(loaded)).Message);
        var local = PrefabFixtures.Create("uneven-painted");
        var asset = local.Assets.GetPrefab("hut");
        var paint = (MapPrefabSurfaceChild)asset.Children.Single(c => c.Key.Key == "paint");
        var bytes = MapPrefabFile.Write(asset with { Children = asset.Children.Select(c => c == paint ? paint with { Surface = paint.Surface with { PaintPriority = 2 } } : c).ToArray() });
        Assert.Equal(2, ((MapPrefabSurfaceChild)MapPrefabFile.Read(bytes, "hut").Children.Single(c => c.Key.Key == "paint")).Surface.PaintPriority);
        Assert.DoesNotContain("paintPriority", Encoding.UTF8.GetString(MapPrefabFile.Write(asset)));
    }

    [Fact]
    public void NonPaintPriority_RefusesAtBothChildCodecBoundaries()
    {
        var f = PrefabFixtures.Create("uneven-painted");
        var asset = f.Assets.GetPrefab("hut");
        var child = (MapPrefabSurfaceChild)asset.Children.Single(c => c.Key.Key == "paint");
        var invalid = child with { Surface = child.Surface with { Role = MapSurfaceRole.SupportFloor, PaintPriority = 2 } };
        Assert.Contains("paintPriority", Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(asset with
        { Children = asset.Children.Select(c => c == child ? invalid : c).ToArray() })).Message);
        var root = JsonNode.Parse(MapPrefabFile.Write(asset))!;
        var surface = root["children"]!.AsArray().Single(c => c!["key"]!["key"]!.GetValue<string>() == "paint")!["surface"]!;
        surface["role"] = "SupportFloor";
        surface["paintPriority"] = 2;
        Assert.Contains("paintPriority", Assert.Throws<MapDocumentException>(() =>
            MapPrefabFile.Read(Encoding.UTF8.GetBytes(root.ToJsonString()), "hut")).Message);
    }
}
