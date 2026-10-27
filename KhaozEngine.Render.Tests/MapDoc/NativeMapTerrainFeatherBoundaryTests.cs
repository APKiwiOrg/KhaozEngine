using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapTerrainFeatherBoundaryTests
{
    [Theory]
    [InlineData(false, 11, 0, false)]
    [InlineData(true, 11, 0, false)]
    [InlineData(false, 10, 11, false)]
    [InlineData(true, 10, 11, false)]
    [InlineData(false, 12, 11, true)]
    [InlineData(true, 12, 11, true)]
    public void MatchingVisibleMaterial_ContinuesAcrossPatchJoin(bool tiled, int underlay, int overlay, bool feather)
    {
        using var fixture = Fixture((ushort)underlay, (ushort)overlay, feather);
        using var batch = Prepare(fixture, tiled);
        Assert.Equal(4, batch.CanonicalFaces.Count);
        var set = Record(batch);

        // Source-selected positions inside the left cell and on the storage seam.
        AssertOverlayAt(set, 63.90625, -.4375, 1);
        AssertOverlayAt(set, 64, -.4375, 1);
        if (feather) AssertOverlayAt(set, 64.09375, -.4375, 1);

        // The exposed west edge still has the released 0.2 m inward band.
        // Halfway between distances 1/16 and 2/16, interpolate their smoothstep values.
        AssertOverlayAt(set, 63.09375, -.4375, .457763671875);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentVisibleMaterial_KeepsFeatherAtPatchJoin(bool tiled)
    {
        using var fixture = Fixture(12, 0, false);
        using var batch = Prepare(fixture, tiled);
        var set = Record(batch);
        AssertOverlayAt(set, 63.90625, -.4375, .457763671875);
        AssertOverlayAt(set, 64, -.4375, 0);
    }

    static NativeSceneFixture Fixture(ushort underlay, ushort overlay, bool feather)
    {
        var (closure, _) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        var fixture = NativeSceneFixtures.Create("terrain-feather") with { Assets = closure };
        fixture.Document.NativeAssets = closure.Roots.ToList();
        var left = fixture.Document.Surfaces.Patches[new("ground", 0, 0)];
        left.CellMinX = 63;
        var right = left.Clone();
        right.Key = new("ground", 1, 0);
        right.CellMinX = 0;
        right.Cells[0] = new(underlay, overlay, MapOverlayCut.Full, 0,
            feather ? MapCellFlags.FeatherOverlay : MapCellFlags.None, MapCellTopology.ForceSwNe);
        fixture.Document.Surfaces.Patches.Add(right.Key, right);
        var surface = fixture.Document.Surfaces.Refs[0];
        fixture.Document.Surfaces.Refs[0] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, new[] { left, right }.Select(p =>
                KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p))))
        };
        return fixture;
    }

    static MapTerrainRenderBatch Prepare(NativeSceneFixture fixture, bool tiled)
    {
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(62, -2), new(66, 1),
            null, null, Enum.GetValues<MapSurfaceRole>(), null, new()), new(62, -1, -2, 66, 3, 1), MapSceneChannels.Terrain);
        var acquired = NativeSceneFixtures.Open(fixture, tiled).Acquire(scope);
        Assert.True(acquired.Witness.CompleteForScope, string.Join("\n", acquired.Findings));
        var closure = fixture.Assets;
        var proofs = MapRenderExtentValidator.Validate(closure, [], new(new())).Proofs!;
        var loaded = MapPreparedInputLease.LoadSelected(closure, [], ["mat-0", "mat-1", "mat-2"], proofs, new(new()));
        Assert.Equal(MapRenderAssetStatus.Complete, loaded.Status);
        using var input = loaded.Input!;
        return NativeMapTerrain.Prepare(Assert.IsType<MapScopedScene>(acquired.Scene), input,
            new Dictionary<ushort, string> { [10] = "mat-0", [11] = "mat-1", [12] = "mat-2" });
    }

    static MapDrawSet Record(MapTerrainRenderBatch batch)
    {
        var recorder = new MapDrawRecorder();
        batch.Register(new("closure", "source", "scope", "policy", 7), recorder);
        var point = new MapFramePoint(WorldFrame.Origin, new(64, 2, -.5f));
        batch.Record(new(point.Frame, point, MapRoofMode.Auto, 2, 17, point), recorder);
        return recorder.Snapshot();
    }

    static void AssertOverlayAt(MapDrawSet set, double x, double z, double expected)
    {
        var samples = new List<double>();
        foreach (var draw in set.Draws)
        {
            var resource = Assert.IsType<MapTerrainResource>(set.Resources.Single(r => r.Reference == draw.Resource).Payload);
            var mesh = resource.Mesh;
            for (int i = 0; i < mesh.Indices32.Length; i += 3)
            {
                var a = mesh.Vertices[mesh.Indices32[i]];
                var b = mesh.Vertices[mesh.Indices32[i + 1]];
                var c = mesh.Vertices[mesh.Indices32[i + 2]];
                if (a.Tangent.W == 0) continue;
                double px = x - draw.Anchor.X, pz = z - draw.Anchor.Z;
                double denominator = Cross(b.Position - a.Position, c.Position - a.Position);
                var offset = new Vector3((float)(px - a.Position.X), 0, (float)(pz - a.Position.Z));
                double wb = Cross(offset, c.Position - a.Position) / denominator;
                double wc = Cross(b.Position - a.Position, offset) / denominator;
                double wa = 1 - wb - wc;
                if (Math.Min(wa, Math.Min(wb, wc)) < -1e-8) continue;
                Assert.Equal((ushort)11, resource.Materials.Layers[(int)a.Tangent.W - 1].MaterialId);
                samples.Add(wa * (1 - a.Color.X) + wb * (1 - b.Color.X) + wc * (1 - c.Color.X));
            }
        }
        Assert.NotEmpty(samples);
        Assert.All(samples, actual => Assert.InRange(Math.Abs(actual - expected), 0, .000001));
    }

    static double Cross(Vector3 a, Vector3 b) => (double)a.X * b.Z - (double)a.Z * b.X;
}
