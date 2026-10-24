using System;
using System.Linq;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileGroundOpticsSurfaceParityTests
{
    static readonly RegionCoord Region = new(0, 0);
    readonly ITestOutputHelper _output;

    public TileGroundOpticsSurfaceParityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CompletedMeshPositionsAndOrderedIndicesAreDefensivelyOwned()
    {
        TileWorldDocument doc = World();
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Grass);
        doc.SetCornerHeightCm(11, 11, 0, 123);
        TileGroundCpuBuild build = Build(doc);
        GltfMesh mesh = Assert.IsType<GltfMesh>(build.Mesh);
        AssertParity(mesh, build.Surface!);
        Vector3[] positions = build.Surface!.Payload.Positions.ToArray();
        uint[] indices = build.Surface!.Payload.Indices.ToArray();
        mesh.Vertices[0].Position = new Vector3(999);
        mesh.Indices32[0] = 5;
        Assert.Equal(positions, build.Surface!.Payload.Positions.ToArray());
        Assert.Equal(indices, build.Surface!.Payload.Indices.ToArray());
        Assert.Equal(positions.LongLength * 12 + indices.LongLength * 4, build.Surface!.Payload.RetainedBytes);
    }

    [Fact]
    public void FeatherOutputPreservesOriginalIndicesAbove65535()
    {
        TileWorldDocument doc = World();
        for (int z = 8; z < 24; z += 2)
            for (int x = 8; x < 24; x += 2)
            {
                doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
                doc.SetOverlay(x, z, 0, TileRenderTestData.Road);
                doc.SetSettings(x, z, 0, TileSettings.FeatherOverlay);
            }
        TileGroundCpuBuild build = Build(doc);
        GltfMesh mesh = Assert.IsType<GltfMesh>(build.Mesh);
        Assert.True(mesh.Indices32.Max() > ushort.MaxValue);
        AssertParity(mesh, build.Surface!);
        ReportBytes("feather64", build.Surface!);
    }

    [Theory]
    [InlineData(0, 100, 100, 0, 0, 0, true)]
    [InlineData(0, 100, 200, 50, 0, 0, true)]
    [InlineData(0, 100, 100, 200, 0, 0, false)]
    [InlineData(0, 100, 100, 0, 1, 0, true)]
    [InlineData(0, 100, 100, 0, 1, 1, false)]
    public void BothHeightDiagonalsTiesAndAbsentOverlayRotationFollowActualMesh(
        short sw, short se, short nw, short ne, int shape, int rotation, bool highDiagonal)
    {
        TileWorldDocument doc = World();
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Grass);
        doc.SetOverlayShape(10, 10, 0, (TileOverlayShape)shape);
        doc.SetOverlayRotation(10, 10, 0, rotation);
        doc.SetCornerHeightCm(10, 10, 0, sw);
        doc.SetCornerHeightCm(11, 10, 0, se);
        doc.SetCornerHeightCm(10, 11, 0, nw);
        doc.SetCornerHeightCm(11, 11, 0, ne);
        TileGroundCpuBuild build = Build(doc);
        AssertParity(Assert.IsType<GltfMesh>(build.Mesh), build.Surface!);
        Vector3[] points = build.Surface!.Payload.Positions.ToArray();
        Vector3 diagonal = highDiagonal ? new(11, ne * 0.01f, -11) : new(10, nw * 0.01f, -11);
        Assert.Contains(diagonal, points[..3]);
        Assert.Contains(diagonal, points[3..]);
        Assert.Equal(6, points.Length);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    public void UndefinedNonzeroOverlayKeepsAllCornerCutRotations(int shape, int rotation)
    {
        TileWorldDocument doc = World();
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Grass);
        doc.SetOverlay(10, 10, 0, 43);
        doc.SetOverlayShape(10, 10, 0, (TileOverlayShape)shape);
        doc.SetOverlayRotation(10, 10, 0, rotation);
        TileGroundCpuBuild build = Build(doc);
        AssertParity(Assert.IsType<GltfMesh>(build.Mesh), build.Surface!);
        Assert.Equal(12, build.Surface!.Payload.Indices.Length);
        int range = RangeAt(build.Surface!, 10, 10);
        Assert.True(build.Surface!.SourceWitness.IsRangeAvailable(range, out _));
    }

    [Fact]
    public void CoarseInteriorsAndTransitionFansKeepTheirActualIndexRanges()
    {
        TileWorldDocument doc = World();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        TileGroundCpuBuild build = Build(doc, TileGroundLod.Coarse4);
        AssertParity(Assert.IsType<GltfMesh>(build.Mesh), build.Surface!);
        var audit = build.Surface!.SourceWitness;
        Assert.Equal(6, audit.Ranges[RangeAt(build.Surface!, 8, 8)].IndexCount);
        Assert.Equal(30, audit.Ranges[RangeAt(build.Surface!, 0, 0)].IndexCount);
        Assert.Equal(build.Mesh!.Indices32.Length, audit.Ranges.ToArray().Sum(r => r.IndexCount));
        Assert.Equal(TileGroundLod.Coarse4, build.Surface!.Lod);
        ReportBytes("coarse-flat", build.Surface!);
    }

    [Fact]
    public void KnownUnloadedEarlierOwnerSurvivesDetachedCaptureWithoutPoisoningInterior()
    {
        using var tmp = new TempDir();
        TileWorldDocument saved = World();
        var east = new RegionCoord(1, 0);
        saved.GetOrCreateRegion(east);
        saved.SetUnderlay(10, 10, 0, TileRenderTestData.Water);
        saved.SetUnderlay(63, 10, 0, TileRenderTestData.Grass);
        saved.SetCornerHeightCm(63, 10, 0, 125);
        saved.SetCornerHeightCm(64, 10, 0, 975);
        string directory = tmp.Sub("known-owner");
        TileWorldFile.Save(saved, directory);
        TileWorldSource source = TileWorldSource.Open(directory);
        source.EnsureLoaded(Region);
        TileWorldDocument detached = TileWorldBuildSnapshot.CaptureGround(source.Document, Region, captureOpticalProvenance: true);
        Directory.Delete(directory, recursive: true);
        Assert.Null(detached.Source);
        TileGroundCpuBuild build = Build(detached);
        Assert.Equal(1.25f, detached.CornerHeight(64, 10, 0));
        Assert.Equal(source.Document.HeightAt(63.5f, -10.5f, 0), detached.HeightAt(63.5f, -10.5f, 0));
        AssertParity(Assert.IsType<GltfMesh>(build.Mesh), build.Surface!);
        var audit = build.Surface!.SourceWitness;
        Assert.False(audit.IsRangeAvailable(RangeAt(build.Surface!, 63, 10), out RegionCoord missing));
        Assert.Equal(east, missing);
        Assert.True(audit.IsRangeAvailable(RangeAt(build.Surface!, 10, 10), out _));
        Assert.True(audit.IsRangeCurrent(RangeAt(build.Surface!, 10, 10), source.Document,
            TileRenderTestData.Catalogs, new TileGroundMesherOptions()));
    }

    [Fact]
    public void CachedCornersRemainAssociatedWithEveryEmittingTileAndFeatherChild()
    {
        TileWorldDocument doc = World();
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Grass);
        doc.SetUnderlay(11, 10, 0, TileRenderTestData.Grass);
        doc.SetOverlay(11, 10, 0, TileRenderTestData.Road);
        doc.SetSettings(11, 10, 0, TileSettings.FeatherOverlay);
        TileGroundCpuBuild build = Build(doc);
        var audit = build.Surface!.SourceWitness;
        int first = RangeAt(build.Surface!, 10, 10), second = RangeAt(build.Surface!, 11, 10);
        Assert.True(audit.Ranges[second].IndexCount > 6);
        doc.SetCornerHeightCm(11, 10, 0, 100);
        Assert.False(audit.IsRangeCurrent(first, doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions()));
        Assert.False(audit.IsRangeCurrent(second, doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions()));
    }

    [Fact]
    public void DerivedSaturatedReadsRetainBaseAndRequestedPlaneProvenance()
    {
        TileWorldDocument doc = World();
        doc.PlaneHeight = 100;
        doc.SetUnderlay(10, 10, 1, TileRenderTestData.Grass);
        doc.SetCornerHeightCm(10, 10, 0, 30000);
        TileGroundCpuBuild build = Build(doc, plane: 1);
        Assert.Equal(327.66998f, build.Mesh!.Vertices[0].Position.Y);
        var audit = build.Surface!.SourceWitness;
        int range = RangeAt(build.Surface!, 10, 10);
        Assert.Contains(audit.HeightReads.ToArray(), h => h.X == 10 && h.Z == 10 &&
            h.RequestedPlane == 1 && h.Value.SourcePlane == 0 && h.SourceHeightCm == 30000);
        Assert.True(audit.IsRangeCurrent(range, doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions()));
        // A changed saturated input is still a changed source, even when rendered Y stays saturated.
        doc.SetCornerHeightCm(10, 10, 0, 31000);
        Assert.False(audit.IsRangeCurrent(range, doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions()));
        build = Build(doc, plane: 1);
        doc.FillDerivedHeights(doc.GetRegion(Region)!, 1);
        Assert.False(build.Surface!.SourceWitness.IsRangeCurrent(range, doc,
            TileRenderTestData.Catalogs, new TileGroundMesherOptions()));
    }

    [Fact]
    public void NegativeAvailabilityLookupsAndOwnerPriorityAreValidated()
    {
        TileWorldDocument doc = World();
        doc.SetUnderlay(63, 63, 0, TileRenderTestData.Grass);
        TileGroundCpuBuild build = Build(doc);
        var audit = build.Surface!.SourceWitness;
        int range = RangeAt(build.Surface!, 63, 63);
        Assert.True(audit.IsRangeAvailable(range, out _));
        var read = Assert.Single(audit.HeightReads.ToArray(), h => h.X == 64 && h.Z == 64);
        Assert.Equal(new[] { new RegionCoord(1, 1), new(0, 1), new(1, 0), Region },
            read.InspectedRegions.ToArray());
        Assert.Equal(Region, read.Value.Owner);
        doc.GetOrCreateRegion(new RegionCoord(0, 1));
        Assert.False(audit.IsRangeCurrent(range, doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions()));
    }

    [Theory]
    [InlineData("mask")]
    [InlineData("shape")]
    [InlineData("rotation")]
    [InlineData("bridge")]
    [InlineData("feather")]
    [InlineData("kind")]
    [InlineData("width")]
    public void TopologyAndMesherInputsInvalidateTheRange(string change)
    {
        TileWorldDocument doc = World();
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Grass);
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var options = new TileGroundMesherOptions();
        TileGroundCpuBuild build = Build(doc, catalogs: catalogs, options: options);
        switch (change)
        {
            case "mask": doc.SetSettings(10, 10, 0, TileSettings.NoDraw); break;
            case "shape": doc.SetOverlayShape(10, 10, 0, TileOverlayShape.DiagonalHalf); break;
            case "rotation": doc.SetOverlayRotation(10, 10, 0, 1); break;
            case "bridge": doc.SetSettings(10, 10, 0, TileSettings.Bridge); break;
            case "feather": doc.SetSettings(10, 10, 0, TileSettings.FeatherOverlay); break;
            case "kind": catalogs.Material(TileRenderTestData.Grass)!.Kind = GroundMaterialKind.Water; break;
            case "width": options.OverlayFeatherWidthMetres = 0.1f; break;
        }
        Assert.False(build.Surface!.SourceWitness.IsRangeCurrent(RangeAt(build.Surface!, 10, 10), doc, catalogs, options));
    }

    [Fact]
    public void ColourChangesKeepSourceValidityAndBedBytes()
    {
        TileWorldDocument doc = World();
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Water);
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        TileGroundCpuBuild before = Build(doc, catalogs: catalogs);
        catalogs.Material(TileRenderTestData.Water)!.Color = "#eeddcc";
        Assert.True(before.Surface!.SourceWitness.IsRangeCurrent(RangeAt(before.Surface!, 10, 10),
            doc, catalogs, new TileGroundMesherOptions()));
        TileGroundCpuBuild after = Build(doc, catalogs: catalogs);
        Assert.Equal(before.Surface!.Payload.Positions.ToArray(), after.Surface!.Payload.Positions.ToArray());
        Assert.Equal(before.Surface!.Payload.Indices.ToArray(), after.Surface!.Payload.Indices.ToArray());
        Assert.True(after.Surface!.Id.Generation > before.Surface!.Id.Generation);
    }

    [Fact]
    public void NullMeshIsKnownEmptyAndTransformPreservesActualFloatPlacement()
    {
        TileWorldDocument doc = World();
        TileGroundCpuBuild empty = Build(doc);
        Assert.Null(empty.Mesh);
        Assert.True(empty.Surface!.IsEmpty);
        Assert.Equal(0, empty.Surface!.Payload.RetainedBytes);
        var distant = new RegionCoord(-12345, 23456);
        doc.TileSize = 1.37f;
        doc.GetOrCreateRegion(distant);
        doc.SetUnderlay(distant.OriginX + 10, distant.OriginZ + 10, 0, TileRenderTestData.Grass);
        TileGroundCpuBuild build = TileGroundCpuBuild.Build(doc, TileRenderTestData.Catalogs, distant, 0,
            TileGroundLod.Full, new TileGroundMesherOptions(), captureOpticalSurface: true);
        Assert.Equal(TileGroundMesher.WorldMatrix(doc, distant), build.Surface!.WorldTransform);
        Assert.Equal(doc.TileSize, build.Surface!.TileSize);
        Assert.Equal(distant, build.Surface!.Id.Region);
        AssertParity(build.Mesh!, build.Surface!);
        Assert.Equal(build.Surface!.Payload.RetainedBytes + build.Surface!.SourceWitness.RetainedBytes,
            build.Surface!.RetainedBytes);
    }

    static int RangeAt(TileGroundSurfaceSnapshot surface, int x, int z)
    {
        for (int i = 0; i < surface.SourceWitness.Ranges.Length; i++)
        {
            var r = surface.SourceWitness.Ranges[i].Tiles;
            if (x >= r.X && x < r.X1 && z >= r.Z && z < r.Z1) return i;
        }
        throw new InvalidOperationException("No emitted tile/block range.");
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(1, 0, 3)]
    public void DetachedManifestPreservesUnloadedWestAndSouthBeforeSouthwest(int rx, int rz, int count)
    {
        using var tmp = new TempDir();
        TileWorldDocument saved = World();
        var missing = new RegionCoord(rx, rz);
        saved.GetOrCreateRegion(missing);
        saved.SetUnderlay(63, 63, 0, TileRenderTestData.Grass);
        string directory = tmp.Sub("fallback-priority");
        TileWorldFile.Save(saved, directory);
        TileWorldSource source = TileWorldSource.Open(directory);
        source.EnsureLoaded(Region);
        TileWorldDocument detached = TileWorldBuildSnapshot.CaptureGround(source.Document, Region, captureOpticalProvenance: true);
        TileGroundCpuBuild build = Build(detached);
        var read = Assert.Single(build.Surface!.SourceWitness.HeightReads.ToArray(), h => h.X == 64 && h.Z == 64);
        Assert.False(read.Available);
        Assert.Equal(missing, read.MissingRegion);
        Assert.Equal(count, read.InspectedRegions.Length);
        Assert.Equal(missing, read.InspectedRegions[^1]);
        Assert.Equal(0f, detached.CornerHeight(64, 64, 0));
    }

    [Fact]
    public void UnavailableStrictOwnerStillAuditsActualLegacyFallbackGeometry()
    {
        using var tmp = new TempDir();
        string directory = TileRenderTestData.SaveGrid(tmp, 2, 1);
        TileWorldSource source = TileWorldSource.Open(directory);
        source.EnsureLoaded(Region);
        source.Document.SetCornerHeightCm(63, 2, 0, 125);
        TileGroundCpuBuild build = Build(TileWorldBuildSnapshot.CaptureGround(source.Document, Region,
            captureOpticalProvenance: true), TileGroundLod.Coarse4);
        var audit = build.Surface!.SourceWitness;
        int range = RangeAt(build.Surface!, 60, 0);
        Assert.False(audit.IsRangeAvailable(range, out _));
        Assert.True(audit.IsRangeCurrent(range, source.Document, TileRenderTestData.Catalogs, new TileGroundMesherOptions()));
        // The east fan reads (64, 2), whose strict owner is unloaded. Legacy geometry uses (63, 2).
        source.Document.SetCornerHeightCm(63, 2, 0, 250);
        Assert.False(audit.IsRangeCurrent(range, source.Document, TileRenderTestData.Catalogs, new TileGroundMesherOptions()));
    }

    void ReportBytes(string label, TileGroundSurfaceSnapshot surface) => _output.WriteLine(
        $"{label}: vertices={surface.Payload.Positions.Length} indices={surface.Payload.Indices.Length} " +
        $"payload={surface.Payload.RetainedBytes} witness={surface.SourceWitness.RetainedBytes} total={surface.RetainedBytes}");

    static TileWorldDocument World()
    {
        var doc = new TileWorldDocument { Id = "surface-parity", DisplayName = "Surface parity" };
        doc.GetOrCreateRegion(Region);
        return doc;
    }

    static TileGroundCpuBuild Build(TileWorldDocument doc, TileGroundLod lod = TileGroundLod.Full,
        int plane = 0, TileWorldCatalogs? catalogs = null, TileGroundMesherOptions? options = null)
    {
        catalogs ??= TileRenderTestData.Catalogs;
        options ??= new TileGroundMesherOptions();
        GltfMesh? legacy = TileGroundMesher.Build(doc, catalogs, Region, plane, lod, options);
        TileGroundCpuBuild build = TileGroundCpuBuild.Build(doc, catalogs, Region, plane, lod, options, captureOpticalSurface: true);
        Assert.Equal(legacy is null, build.Mesh is null);
        if (legacy is not null)
        {
            Assert.True(MemoryMarshal.AsBytes(legacy.Vertices.AsSpan()).SequenceEqual(
                MemoryMarshal.AsBytes(build.Mesh!.Vertices.AsSpan())));
            Assert.Equal(legacy.Indices32, build.Mesh.Indices32);
        }
        return build;
    }

    static void AssertParity(GltfMesh mesh, TileGroundSurfaceSnapshot surface)
    {
        Vector3[] positions = mesh.Vertices.Select(v => v.Position).ToArray();
        Assert.True(MemoryMarshal.AsBytes(positions.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(surface.Payload.Positions)));
        Assert.True(MemoryMarshal.AsBytes(mesh.Indices32.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(surface.Payload.Indices)));
        for (int i = 0; i < mesh.Indices32.Length; i++)
            Assert.Equal(mesh.Vertices[mesh.Indices32[i]].Position,
                surface.Payload.Positions[(int)surface.Payload.Indices[i]]);
    }
}
