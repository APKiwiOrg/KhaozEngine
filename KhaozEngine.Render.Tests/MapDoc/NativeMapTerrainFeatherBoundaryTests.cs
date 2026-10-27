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
using KhaozEngine.MapDoc.Storage;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CardinalHardFills_DoNotInventBoundariesAtDiagonalGaps(bool tiled)
    {
        using var fixture = Fixture(11, 0, false);
        fixture.Document.Surfaces.Patches.Remove(new("ground", 1, 0));
        var patch = fixture.Document.Surfaces.Patches[new("ground", 0, 0)];
        patch.CellMinX = 61; patch.Width = patch.Depth = 3;
        patch.Heights = Enumerable.Repeat(125, 16).ToArray();
        patch.Cells = Enumerable.Range(0, 9).Select(i => i == 4
            ? new MapSurfaceCell(10, 11, MapOverlayCut.Full, 0, MapCellFlags.FeatherOverlay, MapCellTopology.ForceSwNe)
            : new MapSurfaceCell((ushort)(i % 2 == 1 ? 11 : 10), 0, MapOverlayCut.Full, 0, 0, MapCellTopology.ForceSwNe)).ToArray();
        patch.Presence = [511];
        Refresh(fixture);
        using var batch = Prepare(fixture, tiled);
        var set = Record(batch);
        // Only the central overlay supplies candidate edges. All four are covered by bare material 11.
        AssertOverlayAt(set, 62.0625, -1.0625, 1);
        AssertOverlayAt(set, 62.9375, -1.0625, 1);
        AssertOverlayAt(set, 62.0625, -1.9375, 1);
        AssertOverlayAt(set, 62.9375, -1.9375, 1);
    }

    [Theory]
    [InlineData(MapOverlayCut.DiagonalHalf, 0)]
    [InlineData(MapOverlayCut.DiagonalHalf, 1)]
    [InlineData(MapOverlayCut.DiagonalHalf, 2)]
    [InlineData(MapOverlayCut.DiagonalHalf, 3)]
    [InlineData(MapOverlayCut.CornerQuarter, 0)]
    [InlineData(MapOverlayCut.CornerQuarter, 1)]
    [InlineData(MapOverlayCut.CornerQuarter, 2)]
    [InlineData(MapOverlayCut.CornerQuarter, 3)]
    public void DifferentCutOverlay_DoesNotQualifyThroughUnderlay(MapOverlayCut cut, byte rotation)
    {
        using var fixture = Fixture(11, 12, false);
        var right = fixture.Document.Surfaces.Patches[new("ground", 1, 0)];
        right.Cells[0] = right.Cells[0] with { Cut = cut, Rotation = rotation, Topology = MapCellTopology.Auto };
        Refresh(fixture);
        using var batch = Prepare(fixture, false);
        // Legacy Covered rejects a different nonzero overlay, including the uncovered part of its cut.
        // smoothstep((1/16) / .2) = 0.23193359375 at this exact source-selected render vertex.
        AssertOverlayAt(Record(batch), 63.9375, -.5, .23193359375);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ValidatedOwnerSeam_ContinuesMatchingMaterial(bool tiled, bool overlay)
    {
        using var fixture = OwnerFixture(overlay, 125, MapSurfaceRole.SupportFloor, seam: true);
        using var batch = Prepare(fixture, tiled);
        AssertOverlayAt(Record(batch), 63.9375, -.5, 1);
    }

    [Theory]
    [InlineData(false, 225, MapSurfaceRole.SupportFloor, false)]
    [InlineData(true, 225, MapSurfaceRole.SupportFloor, false)]
    [InlineData(false, 125, MapSurfaceRole.Ceiling, true)]
    [InlineData(true, 125, MapSurfaceRole.Ceiling, true)]
    [InlineData(false, 125, MapSurfaceRole.SupportFloor, false)]
    [InlineData(true, 125, MapSurfaceRole.SupportFloor, false)]
    public void UnrelatedOwner_DoesNotSuppressBoundary(bool tiled, int height, MapSurfaceRole role, bool seam)
    {
        using var fixture = OwnerFixture(false, height, role, seam);
        using var batch = Prepare(fixture, tiled);
        AssertOverlayAt(Record(batch), 63.9375, -.5, .23193359375);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentHeightSameOwner_DoesNotSuppressBoundary(bool tiled)
    {
        using var fixture = Fixture(11, 0, false);
        fixture.Document.Surfaces.Patches[new("ground", 1, 0)].Heights = [225, 225, 225, 225];
        Refresh(fixture);
        using var batch = Prepare(fixture, tiled);
        AssertOverlayAt(Record(batch), 63.9375, -.5, .23193359375);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JoinedOwnerStackedPatch_DoesNotFeatherLowerSheet(bool tiled)
    {
        using var fixture = OwnerFixture(false, 125, MapSurfaceRole.SupportFloor, seam: true);
        var surfaces = fixture.Document.Surfaces;
        var left = surfaces.Patches[new("ground", 0, 0)];
        left.CellMinX = 61; left.Width = 3;
        left.Heights = Enumerable.Repeat(125, 8).ToArray();
        left.Cells = Enumerable.Repeat(left.Cells[0], 3).ToArray();
        left.Presence = [7];
        var upper = surfaces.Patches[new("adjoining", 1, 0)].Clone();
        upper.Key = new("adjoining", 0, 0); upper.CellMinX = 62;
        upper.Heights = [225, 225, 225, 225];
        upper.Cells[0] = new(12, 11, MapOverlayCut.Full, 0, 0, MapCellTopology.ForceSwNe);
        surfaces.Patches.Add(upper.Key, upper);
        Refresh(fixture);
        using var batch = Prepare(fixture, tiled);
        // The upper west edge at x=62 is unrelated to the lower sheet's exposed outline.
        AssertOverlayAt(Record(batch), 62.0625, -.5, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StackedSlotsSameOwner_KeepTheirOwnHardOverlayOutline(bool tiled)
    {
        using var fixture = Fixture(12, 11, false);
        fixture.Document.Surfaces.Patches[new("ground", 1, 0)].Heights = [225, 225, 225, 225];
        Refresh(fixture);
        using var batch = Prepare(fixture, tiled);
        AssertOverlayAt(Record(batch), 63.9375, -.5, .23193359375);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOwnerSeam_KeepsOwnersSeparate(bool tiled)
    {
        using var fixture = OwnerFixture(false, 125, MapSurfaceRole.SupportFloor, seam: true);
        var left = fixture.Document.Surfaces.Patches[new("ground", 0, 0)];
        var seam = Assert.IsType<MapSurfaceSeam>(Assert.Single(left.Records));
        left.Records[0] = seam with { Pairs = seam.Pairs.Take(1).ToArray() };
        Refresh(fixture);
        using var batch = Prepare(fixture, tiled);
        AssertOverlayAt(Record(batch), 63.9375, -.5, .23193359375);
    }

    static NativeSceneFixture OwnerFixture(bool overlay, int height, MapSurfaceRole role, bool seam)
    {
        var fixture = Fixture(overlay ? (ushort)12 : (ushort)11, overlay ? (ushort)11 : (ushort)0, overlay);
        var surfaces = fixture.Document.Surfaces;
        var right = surfaces.Patches[new("ground", 1, 0)];
        surfaces.Patches.Remove(right.Key);
        right.Key = new("adjoining", 1, 0);
        right.Heights = Enumerable.Repeat(height, 4).ToArray();
        surfaces.Patches.Add(right.Key, right);
        surfaces.Refs.Add(surfaces.Refs[0] with { Id = "adjoining", Role = role });
        if (seam)
        {
            var left = surfaces.Patches[new("ground", 0, 0)];
            var pairs = new[] { 0, 1 }.Select(z => (First: new MapLatticeVertex("ground", MapLatticeAddress.Corner(64, z)),
                Second: new MapLatticeVertex("adjoining", MapLatticeAddress.Corner(64, z)))).ToArray();
            var join = new MapSurfaceSeam("terrain-seam", new(left.Key, pairs[0].First, pairs[1].First),
                new(right.Key, pairs[0].Second, pairs[1].Second), pairs);
            left.Records.Add(join);
            Assert.Empty(MapSeamValidator.Validate(join, MapScopedSurfaces.CompleteView(surfaces)));
        }
        Refresh(fixture);
        return fixture;
    }

    static void Refresh(NativeSceneFixture fixture)
    {
        var surfaces = fixture.Document.Surfaces;
        for (int i = 0; i < surfaces.Refs.Count; i++)
        {
            var surface = surfaces.Refs[i];
            surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
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
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(60, -3), new(66, 1),
            null, null, Enum.GetValues<MapSurfaceRole>(), null, new()), new(60, -1, -3, 66, 3, 1), MapSceneChannels.Terrain);
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
