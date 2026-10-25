using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public sealed class MapWaterRegionStorageTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FinerCeiling_KeepsEarlierPolygonsChargedAcrossFootprints(bool duringNextRead, bool tangent)
    {
        var set = new MapSurfaceSet();
        var frame = MapLatticeFrame.ImportedMetreCentimetre;
        var roofFrame = frame with { CellUnitMetres = new(1, 8) };
        WaterFixtures.Surface(set, "floor", MapSurfaceRole.SupportFloor, frame, 0, 0, 2, 1, (_, _) => -200);
        WaterFixtures.Surface(set, "roof", MapSurfaceRole.Ceiling, roofFrame, 0, 0, 16, 8,
            (x, z) => tangent && (x + z) % 2 == 0 ? 0 : 100);
        var floorKey = new MapPatchKey("floor", 0, 0);
        WaterFixtures.Footprints(set, "cave", MapSpaceKind.Cave, frame, 0, 0, 2, 1,
            new(MapBoundKind.SupportFloor, "floor", null), new(MapBoundKind.Ceiling, "roof", null), floorKey);
        var view = MapScopedSurfaces.CompleteView(set);
        var roof = set.Patches[new("roof", 0, 0)];
        // Each full roof cell contributes two triangles. Half the source coverage belongs to
        // each metre-wide footprint, and every triangle keeps three 128-byte wide points.
        long facesPerFootprint = (long)(roof.Width / 2) * roof.Depth * 2;
        long polygonBytes = facesPerFootprint * 3 * 128;
        long contourBytes = tangent ? facesPerFootprint * Unsafe.SizeOf<MapWaterClosedContour>() : 0;
        long priorDemand = polygonBytes + contourBytes;
        bool nextFootprint = false, observed = false;
        MapSceneAdmission? admission = null;
        admission = new(new(MaxPreparedBytes: 4_000_000), stage =>
        {
            if (!duringNextRead || !nextFootprint || observed || stage != "compile") return;
            observed = true; // The next lower read has only four source faces, far less than the roof inventory.
            Assert.True(admission!.WorkspaceBytes + admission.RetainedBytes >= priorDemand,
                $"Earlier polygons need {priorDemand} bytes while the next footprint prepares.");
        });
        using var active = admission.Enter();
        using var body = admission.BeginPreparation();
        var meter = new MapWaterWorkMeter(new());
        var domainDoc = new MapWaterDomainDoc(frame, [new(0, 0, 2, 1)], []);
        meter.ChargeDomain(domainDoc);
        var domain = MapWaterDomain.Compile(domainDoc);
        var rect = Assert.Single(MapWaterFootprints.Of(view, "cave", domain.Min, domain.Max,
            out _, out _, out _, meter));
        var builder = new MapWaterRegionBuilder(view, domain, default, new(), meter);
        Assert.Null(builder.Add(rect with { CellMaxX = 1, Max = new(new(1, 1), default) }));
        if (!duringNextRead)
            Assert.True(admission.WorkspaceBytes + admission.RetainedBytes >= priorDemand,
                $"Earlier polygons need {priorDemand} bytes after their footprint returns.");
        nextFootprint = true;
        Assert.Null(builder.Add(rect with { CellMinX = 1, Min = new(new(1, 1), new(-1, 1)) }));
        if (duringNextRead) Assert.True(observed);
        Assert.Equal(2 * facesPerFootprint, Assert.Single(builder.Emit(default)).Triangles.Count);
        Assert.Equal(tangent ? 2 * facesPerFootprint : 0, builder.ClosedContours.Count);
        body.Dispose();
        Assert.Equal(0, admission.WorkspaceBytes + admission.RetainedBytes);
    }
}
