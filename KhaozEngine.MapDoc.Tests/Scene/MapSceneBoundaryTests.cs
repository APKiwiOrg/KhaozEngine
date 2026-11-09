using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportHeightOutsideDrawY_IsDiscoveredBeforeSeating(bool tiled)
    {
        var fixture = PrefabFixtures.Create();
        fixture.Document.Placements.Add(new()
        {
            Id = "seat",
            Kind = "cosmetic",
            AssetId = "item",
            NumericId = ++fixture.Document.NumericIdHighWaterMark,
            X = 1,
            Z = -1,
            Y = null,
            SupportBinding = new(MapSupportBindingKind.Surface, "exterior", null, null, null, null),
        });
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var source = NativeSceneFixtures.Open(native, tiled);
        var result = source.Acquire(MapSceneScopeTests.Window(-2, 2, 100, 110));
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        Assert.Equal(-2, result.Scene!.Placements.Single(p => p.PlacementId == "seat").Transform.Position.Y);
        Assert.Contains(result.Scene.Surfaces.Refs, s => s.Id == "a/floor");
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("attached")]
    [InlineData("materialized")]
    [InlineData("all-resources")]
    [InlineData("world-owned-light")]
    public void SharedKernel_PreservesReleasedEffectiveResources(string variant)
    {
        var fixture = variant == "world-owned-light" ? PrefabFixtures.WorldOwnedLight() : PrefabFixtures.Create(variant);
        var full = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        var result = NativeSceneFixtures.Open(new(fixture.Document, fixture.Assets), false).Acquire(MapSceneScopeTests.Window(-64, 64));
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        var scene = result.Scene!;
        Assert.Equal(full.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)), scene.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)));
        Assert.Equal(full.Transforms.OrderBy(p => p.Key), scene.Transforms.OrderBy(p => p.Key));
        Assert.Equal(MapEffectiveAnnotations.Lights(full), MapEffectiveAnnotations.Lights(scene));
        Assert.Equal(MapEffectiveAnnotations.Markers(full).Select(m => (m.Id, m.Name, m.Position, m.YawRadians, m.Role, m.Enabled, m.InstanceId, Tags: string.Join("|", m.Tags))),
            MapEffectiveAnnotations.Markers(scene).Select(m => (m.Id, m.Name, m.Position, m.YawRadians, m.Role, m.Enabled, m.InstanceId, Tags: string.Join("|", m.Tags))));
        Assert.Equal(full.PaintRegions.Keys, scene.PaintRegions.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FoliageModelReach_FindsDensityOutsideTheWindow(bool tiled)
    {
        var fixture = FoliageFixtures.Create();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var source = NativeSceneFixtures.Open(native, tiled);
        // The second chunk begins at X=61. The model sphere reaches X=60 from that chunk.
        var result = source.Acquire(MapSceneScopeTests.Window(60, 60.5));
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        Assert.Contains(result.Witness.Required, k => k.Kind == "Foliage" && k.Id == "cover-a/1/0");
        Assert.NotEmpty(result.Scene!.Foliage!.Layers.Single().Chunks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnpackedAttachments_KeepCapturedSupportDescriptors(bool tiled)
    {
        var fixture = PrefabFixtures.Create("attached");
        var unpacked = KhaozEngine.MapDoc.Editing.MapPrefabEdits.Prepare(fixture.Document, fixture.Assets,
            new KhaozEngine.MapDoc.Editing.MapUnpackPrefab("a")).After;
        var full = MapPrefabResolver.Expand(unpacked, fixture.Assets);
        using var native = new NativeSceneFixture(unpacked, fixture.Assets);
        var source = NativeSceneFixtures.Open(native, tiled);
        var result = source.Acquire(MapSceneScopeTests.Window(-64, 64));
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        Assert.Equal(full.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)),
            result.Scene!.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)));
        Assert.Equal(full.Transforms.OrderBy(p => p.Key), result.Scene.Transforms.OrderBy(p => p.Key));
        Assert.Equal(MapEffectiveAnnotations.Lights(full), MapEffectiveAnnotations.Lights(result.Scene));
    }

    [Fact]
    public void NativeSurfaceFrame_IsDiscoveredAtItsEffectiveLocation()
    {
        var fixture = PrefabFixtures.Create("roomless");
        var patch = PrefabFixtures.Patch("native-floor", 2, 2, 500);
        var surface = PrefabFixtures.Surface("native-floor", MapSurfaceRole.SupportFloor);
        fixture.Document.Surfaces.Refs.Add(surface);
        fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        fixture.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Surface,
            surface.Id, new(-65, 0, 0, 0, 1), new string('a', 64)) };
        var source = NativeSceneFixtures.Open(new(fixture.Document, fixture.Assets), false);
        var result = source.Acquire(MapSceneScopeTests.Window(-64, 0));
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        Assert.Contains(result.Scene!.Surfaces.Patches.Keys, k => k.SurfaceId == "native-floor");
        Assert.Equal(-65, result.Scene.Transforms["Surface:native-floor"].X);
    }

    [Fact]
    public void WholeWorldWitness_RequiresCheckedSourceCoverage()
    {
        var source = NativeSceneFixtures.Open(NativeSceneFixtures.Create("spanning"), false);
        var near = source.Acquire(MapSceneScopeTests.Window());
        Assert.False(near.Witness.WholeWorldComplete);
        var whole = source.Acquire(MapSceneScopeTests.Window(-32000, 32000) with
        {
            Surfaces = MapSceneScopeTests.Window(-32000, 32000).Surfaces with { LocalMin = new(-32000, -32000), LocalMax = new(32000, 32000) },
            DrawBounds = new(-32000, -500, -32000, 32000, 500, 32000),
        });
        Assert.Equal(MapSceneStatus.WholeWorldComplete, whole.Status);
        Assert.True(whole.Witness.WholeWorldComplete);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("space")]
    [InlineData("empty-space")]
    [InlineData("height-min")]
    [InlineData("height-max")]
    public void FilteredWholeBounds_NeverClaimWholeWorldCompleteness(string filter)
    {
        using var fixture = NativeSceneFixtures.Create("spanning");
        var source = NativeSceneFixtures.Open(fixture, false);
        var scope = MapSceneScopeTests.Window(-32000, 32000);
        scope = scope with
        {
            Surfaces = scope.Surfaces with { LocalMin = new(-32000, -32000), LocalMax = new(32000, 32000) },
            DrawBounds = new(-32000, -500, -32000, 32000, 500, 32000),
        };
        scope = scope with
        {
            Surfaces = filter switch
            {
                "role" => scope.Surfaces with { Roles = new[] { MapSurfaceRole.SupportFloor } },
                "space" => scope.Surfaces with { SpaceIds = new[] { "outside" } },
                "empty-space" => scope.Surfaces with { SpaceIds = Array.Empty<string>() },
                "height-min" => scope.Surfaces with { MinY = 0 },
                _ => scope.Surfaces with { MaxY = 0 },
            }
        };
        var result = source.Acquire(scope);
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        Assert.False(result.Witness.WholeWorldComplete);
        Assert.Equal(MapSceneStatus.CompleteForScope, result.Status);
    }

    [Fact]
    public void SourceMutation_DoesNotChangeCapturedOwnerRecords()
    {
        using var fixture = NativeSceneFixtures.Create("spanning");
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var captured = MapSceneSource.Capture(document, fixture.Assets).Source!;
        var instance = document.Manifest.Prefabs!.Instances.Single();
        document.Manifest.Prefabs = new(1, 1, new[] { instance with { Transform = new(new(500, 0, 500), 0, 1) } }, []);
        var result = captured.Acquire(MapSceneScopeTests.Window());
        Assert.True(result.Witness.CompleteForScope);
        Assert.Equal(-65, result.Scene!.Transforms["Placement:spanning-leaf"].X);
    }

    [Fact]
    public void TiledSculpt_IsHashedWithoutRetainingItsPayload()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        fixture.Document.TerrainOverrides = new(MapTerrainOverrides.DefaultCellSize);
        int samples = TerrainSculpt.TileSize * TerrainSculpt.TileSize;
        fixture.Document.TerrainOverrides.PutTile(new(0, 0, Enumerable.Repeat(.25f, samples).ToArray()));
        using var document = NativeSceneFixtures.OpenTiled(fixture);
        string directory = document.Tiles.SourceDirectory!;
        try
        {
            var result = MapSceneSource.Capture(document, fixture.Assets);
            Assert.Equal(MapSceneSourceStatus.Ready, result.Status);
            Assert.Equal(0, result.Work.MetadataRows);
            Assert.Equal(0, result.Work.RetainedBytes);
            Assert.True(result.Work.ScannedBytes > new FileInfo(Directory.GetFiles(Path.Combine(directory, "tiles"), "*.json", SearchOption.AllDirectories).Single()).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SelectedAggregateBudget_RefusesAtomically()
    {
        using var fixture = NativeSceneFixtures.Create("spanning");
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var captured = MapSceneSource.Capture(document, fixture.Assets, new(MaxPreparedBytes: 0)).Source!;
        var result = captured.Acquire(MapSceneScopeTests.Window());
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Scene);
        Assert.False(result.Witness.CompleteForScope);
        Assert.Equal(0, result.Work.RetainedBytes);
    }
}
