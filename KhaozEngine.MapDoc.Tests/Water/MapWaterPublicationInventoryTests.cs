using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public sealed class MapWaterPublicationInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneContourPublication_SettlesAllRetainedBackingSlots(bool cell)
    {
        var set = new MapSurfaceSet();
        var frame = MapLatticeFrame.ImportedMetreCentimetre;
        WaterFixtures.Surface(set, "floor", MapSurfaceRole.SupportFloor, frame, 0, 0, 1, 1, (_, _) => -200);
        // Auto topology picks SW-NE. Only the triangle containing SE touches level zero.
        WaterFixtures.Surface(set, "roof", MapSurfaceRole.Ceiling, frame, 0, 0, 1, 1,
            (x, z) => x == 1 && z == 1 ? 0 : 100);
        var floor = new MapPatchKey("floor", 0, 0);
        WaterFixtures.Footprints(set, "cave", MapSpaceKind.Cave, frame, 0, 0, 1, 1,
            new(MapBoundKind.SupportFloor, "floor", null), new(MapBoundKind.Ceiling, "roof", null), floor);
        var view = MapScopedSurfaces.CompleteView(set);
        var water = new MapWaterSet();
        var medium = new MapWaterMediumDoc("water", MapWaterMediumKind.Water, 1);
        water.Media.Add(medium);
        var source = new MapWaterBodyDoc("pond", 0, "water", "material", new(frame, [new(0, 0, 1, 1)], []),
            [new("cave", floor)]);
        water.Bodies.Add(source);
        var effective = new MapEffectiveWaterBody(source, MapWaterDomain.Compile(source.Domain), default,
            source.Id, source.Covers, "");
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1_000_000));
        using var active = admission.Enter();
        using var consumer = admission.BeginPreparation();
        var body = cell ? MapWaterCompiler.CompileCell(view, water, source, effective, new())
            : Assert.Single(MapWaterCompiler.Compile(view, water));
        Assert.Equal(MapWaterCompileStatus.Complete, body.Status);
        Assert.Single(body.ClosedContours);
        Assert.Equal(4, Backing(body.ClosedContours).Capacity);
        var region = Assert.Single(body.Regions);
        Assert.Equal(4, Backing(body.Regions).Capacity);
        Assert.Equal(2, region.Triangles.Count);
        Assert.Equal(4, Backing(region.Triangles).Capacity);
        Assert.Empty(region.WidePolygons);
        Assert.Single(body.Footprints["cave"]);
        Assert.Empty(body.OccupiedPieces);
        Assert.Empty(body.FragmentFootprints);
        Assert.Empty(body.FragmentBounds);
        Assert.Equal(4, Assert.Single(body.Domain!.Pieces).Ring.Count);
        // Independent inventory: body 256, region slots 4*8, contour slots 4*224,
        // dictionary 64, region 128, triangle slots 4*144, footprint row 8,
        // domain 128 + one piece*512 + four vertices*512 = 4648 bytes.
        // The full-set publication additionally retains its 128 + one slot*8 container.
        Assert.Equal(cell ? 4648 : 4784, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(4648, new MapWaterInventory().Body(body));
        consumer.Dispose();
        Assert.Equal(0, admission.RetainedBytes + admission.WorkspaceBytes);
    }

    [Fact]
    public void OccupiedPublication_SettlesCleanedPolygonSpareSlots()
    {
        // A convex ring has five vertices, with a straight vertex halfway along its bottom edge.
        // Emission removes that vertex, but the cleaned list retains all five backing slots.
        var domain = new MapWaterDomain([[Point(0, -1), Point(1, -1), Point(2, -1), Point(2, 0), Point(0, 0)]], ["domain"]);
        var view = MapScopedSurfaces.CompleteView(new MapSurfaceSet());
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1_000_000));
        using var active = admission.Enter();
        using var consumer = admission.BeginPreparation();
        using var preparation = admission.BeginPreparation();
        var builder = new MapWaterRegionBuilder(view, domain, default, new(), new(new()));
        builder.Add(new MapOccupiedPiece("cave", "piece", new("floor", "floor", ""), new("open", "open-top", ""),
            [Point(-1, -2), Point(3, -2), Point(3, 1), Point(-1, 1)], [], null, new(-2, 1), null));
        var regions = builder.Emit(default);
        var region = Assert.Single(regions);
        var polygon = Assert.Single(region.WidePolygons);
        Assert.Equal(4, polygon.Count);
        Assert.Equal(5, Backing(polygon).Capacity);
        Assert.Equal(2, region.Triangles.Count);
        Assert.Equal(4, Backing(region.Triangles).Capacity);
        Assert.Equal(4, Backing(regions).Capacity);
        var body = new MapCompiledWaterBody("pond", 0, new("water", MapWaterMediumKind.Water, 1), "material",
            regions, null, null, MapWaterCompileStatus.Complete, null, new(0, 0, 0, 0, 0, 0, 0, 0, 0), "")
        { FragmentBounds = builder.FragmentBounds };
        preparation.Complete(new MapWaterInventory().Body(body));
        preparation.Dispose();
        // Body 256 + four region slots*8 + region 128 + four triangle slots*144
        // + one polygon slot*8 + five wide point slots*128 + one fragment bound*64 = 1704.
        Assert.Equal(1704, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        consumer.Dispose();
        Assert.Equal(0, admission.RetainedBytes + admission.WorkspaceBytes);
    }

    static MapWideXz Point(long x, long z) => new(MapWideRational.From(x), MapWideRational.From(z));

    static List<T> Backing<T>(IReadOnlyList<T> rows) => Assert.IsType<List<T>>(
        typeof(ReadOnlyCollection<T>).GetProperty("Items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rows));
}
