using System;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileGroundOpticsBoundaryRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public void DefaultEmptyFourPlaneLoadRetainsNoOpticalSource()
    {
        var doc = new TileWorldDocument();
        var origin = new RegionCoord(0, 0);
        doc.GetOrCreateRegion(origin);
        using var view = new TileWorldView(new RecordingTileWorldScene(), doc, TileWorldCatalogs.Empty(),
            new GreyboxMeshResolver());
        view.LoadRegion(origin);
        long retained = 0;
        for (int plane = 0; plane < doc.PlaneCount; plane++)
            retained += view.AcceptedGroundSurface(origin, plane)?.RetainedBytes ?? 0;
        output.WriteLine($"default-empty-four-plane: retained={retained}");
        Assert.Equal(0, retained);
        for (int plane = 0; plane < doc.PlaneCount; plane++)
        {
            Assert.Null(view.AcceptedGroundSurface(origin, plane));
            // The same factory called by LoadRegion and Flush must not do discarded audit work either.
            TileGroundCpuBuild built = view.BuildGroundCpu(origin, plane);
            Assert.Null(built.Surface);
            Assert.Equal(0, built.AuditReadCount);
        }
    }

    [Theory]
    [InlineData(TileGroundLod.Full)]
    [InlineData(TileGroundLod.Coarse4)]
    public void OptedInEmptySurfaceRetainsBoundedGranularWitnesses(TileGroundLod lod)
    {
        var doc = new TileWorldDocument { PlaneCount = 1 };
        var origin = new RegionCoord(0, 0);
        doc.GetOrCreateRegion(origin);
        TileGroundCpuBuild build = TileGroundCpuBuild.Build(doc, TileWorldCatalogs.Empty(), origin, 0, lod,
            new TileGroundMesherOptions(), captureOpticalSurface: true);
        Assert.Null(build.Mesh);
        TileGroundSurfaceSnapshot surface = Assert.IsType<TileGroundSurfaceSnapshot>(build.Surface!);
        output.WriteLine($"opted-in-empty-{lod}: retained={surface.RetainedBytes}");
        Assert.True(surface.IsEmpty);
        Assert.Equal(lod == TileGroundLod.Full ? 4096 : 256, surface.SourceWitness.Ranges.Length);
        Assert.All(surface.SourceWitness.Ranges.ToArray(), range => Assert.Equal(0, range.IndexCount));
        Assert.True(surface.RetainedBytes <= 320_000, $"Repeated footprints retained {surface.RetainedBytes} bytes.");
        // Each Full tile has one witness. Coarse footprints share the 72 by 72 coordinate grid.
        Assert.Equal(lod == TileGroundLod.Full ? 4096 : 5184, build.AuditReadCount);
        Assert.Equal(0, surface.Payload.RetainedBytes);
        Assert.Equal(surface.SourceWitness.RetainedBytes, surface.RetainedBytes);
        int interior = RangeAt(surface, 10, 10), remote = RangeAt(surface, 50, 50);
        doc.SetUnderlay(10, 10, 0, 1);
        Assert.False(surface.SourceWitness.IsRangeCurrent(interior, doc, TileWorldCatalogs.Empty(), new TileGroundMesherOptions()));
        Assert.True(surface.SourceWitness.IsRangeCurrent(remote, doc, TileWorldCatalogs.Empty(), new TileGroundMesherOptions()));
    }

    [Theory]
    [InlineData(int.MinValue, 0, TileGroundLod.Full)]
    [InlineData(int.MaxValue, 0, TileGroundLod.Full)]
    [InlineData(0, int.MinValue, TileGroundLod.Full)]
    [InlineData(0, int.MaxValue, TileGroundLod.Full)]
    [InlineData(int.MinValue, 0, TileGroundLod.Coarse4)]
    [InlineData(int.MaxValue, 0, TileGroundLod.Coarse4)]
    [InlineData(0, int.MinValue, TileGroundLod.Coarse4)]
    [InlineData(0, int.MaxValue, TileGroundLod.Coarse4)]
    public void AuditAtSignedEdgesPreservesLegacyInteriorMesh(int x, int z, TileGroundLod lod)
    {
        RegionCoord region = RegionCoord.Of(x, z);
        var doc = new TileWorldDocument { PlaneCount = 1 };
        doc.GetOrCreateRegion(region);
        doc.SetUnderlay(region.OriginX + 10, region.OriginZ + 10, 0, 1);
        var catalogs = TileWorldCatalogs.Empty();
        var options = new TileGroundMesherOptions();
        GltfMesh legacy = Assert.IsType<GltfMesh>(TileGroundMesher.Build(doc, catalogs, region, 0, lod, options));
        Assert.Equal(6, legacy.Indices32.Length);
        TileGroundCpuBuild build = TileGroundCpuBuild.Build(doc, catalogs, region, 0, lod, options, captureOpticalSurface: true);
        Assert.True(MemoryMarshal.AsBytes(legacy.Vertices.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(build.Mesh!.Vertices.AsSpan())));
        Assert.Equal(legacy.Indices32, build.Mesh.Indices32);
        Assert.Equal(legacy.Indices32, build.Surface!.Payload.Indices.ToArray());
        Assert.True(build.Surface.SourceWitness.IsRangeAvailable(
            RangeAt(build.Surface, region.OriginX + 10, region.OriginZ + 10), out _));
    }

    [Theory]
    [InlineData(int.MinValue, 0, false)]
    [InlineData(int.MaxValue, 0, false)]
    [InlineData(0, int.MinValue, false)]
    [InlineData(0, int.MaxValue, false)]
    [InlineData(int.MinValue, 0, true)]
    [InlineData(int.MaxValue, 0, true)]
    [InlineData(0, int.MinValue, true)]
    [InlineData(0, int.MaxValue, true)]
    public void ViewCanLoadSignedEdgesWithOrdinaryInteriorGround(int x, int z, bool optical)
    {
        RegionCoord region = RegionCoord.Of(x, z);
        var doc = new TileWorldDocument { PlaneCount = 1 };
        doc.GetOrCreateRegion(region);
        doc.SetUnderlay(region.OriginX + 10, region.OriginZ + 10, 0, 1);
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileWorldCatalogs.Empty(), new GreyboxMeshResolver(),
            new TileWorldViewOptions { WaterDepthMode = optical ? TileWaterDepthMode.AuthoredBed : TileWaterDepthMode.SceneDepth });
        view.LoadRegion(region);
        Assert.Equal(6, Assert.Single(scene.GroundMeshes).Value.Indices32.Length);
        Assert.Equal(optical, view.AcceptedGroundSurface(region, 0) is not null);
        view.MarkDirty(region, 0);
        view.Flush(int.MaxValue);
        Assert.Equal(optical, view.AcceptedGroundSurface(region, 0) is not null);
    }

    [Theory]
    [InlineData(int.MinValue, 0, TileGroundLod.Full)]
    [InlineData(int.MaxValue, 0, TileGroundLod.Full)]
    [InlineData(0, int.MinValue, TileGroundLod.Full)]
    [InlineData(0, int.MaxValue, TileGroundLod.Full)]
    [InlineData(int.MinValue, 0, TileGroundLod.Coarse4)]
    [InlineData(int.MaxValue, 0, TileGroundLod.Coarse4)]
    [InlineData(0, int.MinValue, TileGroundLod.Coarse4)]
    [InlineData(0, int.MaxValue, TileGroundLod.Coarse4)]
    public void UnsupportedEmptyMarginDoesNotWrapOrRefuseValidInterior(int x, int z, TileGroundLod lod)
    {
        RegionCoord region = RegionCoord.Of(x, z);
        var doc = new TileWorldDocument { PlaneCount = 1 };
        doc.GetOrCreateRegion(region);
        int edgeX = x == 0 ? 10 : x, edgeZ = z == 0 ? 10 : z;
        // Even an empty feather decision must retain its entire two-ring footprint.
        doc.SetSettings(edgeX, edgeZ, 0, TileSettings.FeatherOverlay);
        doc.SetUnderlay(region.OriginX + 10, region.OriginZ + 10, 0, 1);
        var options = new TileGroundMesherOptions();
        var catalogs = TileWorldCatalogs.Empty();
        TileGroundSurfaceSnapshot surface = TileGroundCpuBuild.Build(doc, catalogs, region, 0, lod, options,
            captureOpticalSurface: true).Surface!;
        var audit = surface.SourceWitness;
        int edge = RangeAt(surface, edgeX, edgeZ);
        Assert.Equal(0, audit.Ranges[edge].IndexCount);
        Assert.False(audit.IsRangeRepresentable(edge));
        Assert.False(audit.IsRangeAvailable(edge, out _));
        TileGroundAuditExtent extent = audit.Ranges[edge].TopologyExtent;
        Assert.True(extent.X < int.MinValue || extent.Z < int.MinValue ||
            extent.X1 > (long)int.MaxValue + 1 || extent.Z1 > (long)int.MaxValue + 1);
        int interior = RangeAt(surface, region.OriginX + 10, region.OriginZ + 10);
        Assert.True(audit.IsRangeRepresentable(interior));
        Assert.True(audit.IsRangeAvailable(interior, out _));
        Assert.True(audit.IsCurrent(doc, catalogs, options));
        int oppositeX = x == 0 ? edgeX : x == int.MinValue ? int.MaxValue : int.MinValue;
        int oppositeZ = z == 0 ? edgeZ : z == int.MinValue ? int.MaxValue : int.MinValue;
        doc.GetOrCreateRegion(RegionCoord.Of(oppositeX, oppositeZ));
        doc.SetUnderlay(oppositeX, oppositeZ, 0, 1);
        Assert.True(audit.IsRangeCurrent(edge, doc, catalogs, options));
        doc.SetUnderlay(edgeX, edgeZ, 0, 1);
        Assert.False(audit.IsRangeCurrent(edge, doc, catalogs, options));
        Assert.True(audit.IsRangeCurrent(interior, doc, catalogs, options));
        Assert.DoesNotContain(audit.HeightReads.ToArray(), h => h.X == oppositeX && h.Z == oppositeZ);
    }

    [Theory]
    [InlineData(true, TileGroundLod.Full)]
    [InlineData(false, TileGroundLod.Full)]
    [InlineData(true, TileGroundLod.Coarse4)]
    [InlineData(false, TileGroundLod.Coarse4)]
    public void EmptyEdgeDecisionsRetainWidenedExclusiveEdge(bool xAxis, TileGroundLod lod)
    {
        RegionCoord region = RegionCoord.Of(xAxis ? int.MaxValue : 0, xAxis ? 0 : int.MaxValue);
        var doc = new TileWorldDocument { PlaneCount = 1 };
        doc.GetOrCreateRegion(region);
        TileGroundCpuBuild build = TileGroundCpuBuild.Build(doc, TileWorldCatalogs.Empty(), region, 0, lod,
            new TileGroundMesherOptions(), captureOpticalSurface: true);
        Assert.Null(build.Mesh);
        TileGroundSurfaceSnapshot surface = build.Surface!;
        int edge = RangeAt(surface, xAxis ? int.MaxValue : 10, xAxis ? 10 : int.MaxValue);
        TileGroundSurfaceExtent tiles = surface.SourceWitness.Ranges[edge].Tiles;
        Assert.Equal((long)int.MaxValue + 1, xAxis ? tiles.X1 : tiles.Z1);
        Assert.Equal(lod == TileGroundLod.Full, surface.SourceWitness.IsRangeAvailable(edge, out _));
        Assert.True(surface.SourceWitness.IsCurrent(doc, TileWorldCatalogs.Empty(), new TileGroundMesherOptions()));
    }

    [Theory]
    [InlineData(true, TileGroundLod.Full)]
    [InlineData(false, TileGroundLod.Full)]
    [InlineData(true, TileGroundLod.Coarse4)]
    [InlineData(false, TileGroundLod.Coarse4)]
    public void WrappedLegacyGeometryCornerCannotClaimOpticalProvenance(bool xAxis, TileGroundLod lod)
    {
        int x = xAxis ? int.MaxValue : 10, z = xAxis ? 10 : int.MaxValue;
        RegionCoord region = RegionCoord.Of(x, z);
        var doc = new TileWorldDocument { PlaneCount = 1 };
        doc.GetOrCreateRegion(region);
        doc.SetUnderlay(x, z, 0, 1);
        var catalogs = TileWorldCatalogs.Empty();
        var options = new TileGroundMesherOptions();
        GltfMesh legacy = TileGroundMesher.Build(doc, catalogs, region, 0, lod, options)!;
        TileGroundCpuBuild build = TileGroundCpuBuild.Build(doc, catalogs, region, 0, lod, options,
            captureOpticalSurface: true);
        Assert.Equal(legacy.Vertices, build.Mesh!.Vertices);
        Assert.Equal(legacy.Indices32, build.Mesh.Indices32);
        var audit = build.Surface!.SourceWitness;
        int edge = RangeAt(build.Surface, x, z);
        Assert.False(audit.IsRangeRepresentable(edge));
        Assert.False(audit.IsRangeAvailable(edge, out _));
        Assert.DoesNotContain(audit.HeightReads.ToArray(), h => h.X == int.MinValue || h.Z == int.MinValue);
    }

    static int RangeAt(TileGroundSurfaceSnapshot surface, int x, int z) =>
        Array.FindIndex(surface.SourceWitness.Ranges.ToArray(), r =>
            x >= r.Tiles.X && x < r.Tiles.X1 && z >= r.Tiles.Z && z < r.Tiles.Z1);
}
