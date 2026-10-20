using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;
using static KhaozEngine.Tests.MapDoc.Foliage.FoliageFixtures;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>OA9 on the released stacked-support and prefab-cave fixtures: foliage edits never reach statics, navigation
/// captures or world bounds, independent heads in both storage forms produce identical owned foliage, and the collider
/// builder has no edge to foliage samples.</summary>
public sealed class MapFoliageWorldTests
{
    static readonly MapNavTileOptions Nav = new(2f, "foliage-profile", "foliage-controller");
    const float BeaconY = 120;

    [Fact]
    public void DensitySeedRuleAndMask_NeverChangeStaticsNavOrWorldBounds()
    {
        foreach (var (name, fixture, layerId, beacon, seat, asset) in Cases())
        {
            // A high marker owns nothing yet. It stays in the document on both sides of every edit.
            MapBuildingIntent? intent = fixture.Document.BuildingIntent;
            var high = new MapLocalMarkerDoc(1, "beacon", "Beacon", new(beacon.X, BeaconY, beacon.Y), 0, "beacon", true, []);
            fixture.Document.BuildingIntent = new(1, intent?.RoofLinks ?? [], intent?.RoofGroups ?? [], intent?.Lights ?? [],
                (intent?.Markers ?? []).Append(high).ToArray())
            { Roofs = intent?.Roofs ?? [], Storeys = intent?.Storeys ?? [] };
            MapBuiltWorld before = Build(fixture);
            string beforeStatics = Statics(before);
            string beforeNavCapture = NavCapture(before);
            MapBox3 beforeWorldBounds = before.Bounds;
            // Density extents, padded model bounds and the high marker never enter the world or tile height range: the
            // same world built without any foliage has the same statics, captures and bounds.
            MapBuiltWorld bare = Build(new(Without(fixture.Document), fixture.Assets));
            Assert.Equal((beforeStatics, beforeNavCapture, beforeWorldBounds), (Statics(bare), NavCapture(bare), bare.Bounds));
            Assert.True(before.Bounds.MaxY < BeaconY, $"{name}: {before.Bounds}");
            Assert.All(MapNavTiling.Partition(before, Grids(before), Nav),
                tile => Assert.True(tile.Bounds.MaxY < BeaconY && tile.CaptureBounds.MaxY < BeaconY));

            MapFoliageLayer target = fixture.Document.Foliage!.Layers.Single(l => l.Id == layerId);
            MapPrefabReference owner = Owner("beacon", MapPrefabChildKind.Marker);
            var edits = new (string Kind, MapFoliageEdit Edit, bool Rows)[]
            {
                ("density", new MapSetFoliageDensity(layerId, 0, 0, 2, 1, [7, 250]), true),
                ("seed", new MapSetFoliageLayer(FoliageStorageFixtures.Change(target, l => l["seed"] = target.Seed + 1)), false),
                ("rule", new MapSetFoliageLayer(ClearanceLayer(target, rules: target.ClearanceRules
                    .Append(new("added-solid", asset, null, MapFoliageClearanceKind.Solid, new(-2, -2, 2, 2))).ToArray())), false),
                ("mask", new MapSetFoliageLayer(ClearanceLayer(target,
                    masks: target.ExceptionMasks
                        .Append(new("beacon-solid", owner, Surface(seat), MapFoliageClearanceKind.Solid, new(-1, -1, 1, 1))).ToArray(),
                    owners: target.OwnerSupports.Append(new(owner, Surface(seat))).ToArray())), true),
            };
            foreach (var (kind, edit, rows) in edits)
            {
                MapPrefabEditResult result = MapFoliageEdits.Prepare(fixture.Document, fixture.Assets, edit);
                Assert.True(result.WriteSet.Foliage, name + " " + kind);
                Assert.Equal(MapNativeInvalidation.Foliage, result.Effects.Invalidates);
                Assert.Null(result.Effects.OldBounds);
                Assert.Null(result.Effects.NewBounds);
                // Localized density and mask changes publish batch rows. Seed and rule changes publish one layer entry.
                Assert.Equal(rows, result.Effects.FoliageChunks.Count != 0);
                Assert.Equal(rows ? 0 : 1, result.Effects.FoliageLayers.Count);
                MapBuiltWorld after = Build(new(result.After, fixture.Assets));
                string afterDensitySeedRuleAndMaskStatics = Statics(after);
                string afterDensitySeedRuleAndMaskNavCapture = NavCapture(after);
                MapBox3 afterDensitySeedRuleAndMaskWorldBounds = after.Bounds;
                Assert.Equal(beforeStatics, afterDensitySeedRuleAndMaskStatics);
                Assert.Equal(beforeNavCapture, afterDensitySeedRuleAndMaskNavCapture);
                Assert.Equal(beforeWorldBounds, afterDensitySeedRuleAndMaskWorldBounds);
                Assert.Equal(before.Document.EffectiveResources!.AnnotationDigest, after.Document.EffectiveResources!.AnnotationDigest);
                Assert.Empty(MapNavTiling.AffectedTiles(after, Grids(after), Nav, result.Effects));
            }

            // Physical and pick queries hold no generated blade: a pick down through every generated sample meets what the
            // world without foliage meets, and every explicit placement keeps its own pick.
            var queries = new MapWorldQueries(before);
            var bareQueries = new MapWorldQueries(bare);
            foreach (MapFoliageSample sample in Batches(fixture).SelectMany(b => b.Samples))
            {
                var ray = new MapPickRay(sample.Position + new Vector3(0, 0.5f, 0), -Vector3.UnitY, 1);
                Assert.Equal(bareQueries.Pick(ray)?.PlacementId, queries.Pick(ray)?.PlacementId);
            }
            foreach (MapPlacementGeometry placement in before.Placements)
            {
                MapBox3 envelope = placement.Envelope.Bounds;
                var top = new Vector3((float)((envelope.MinX + envelope.MaxX) / 2), (float)envelope.MaxY + 1, (float)((envelope.MinZ + envelope.MaxZ) / 2));
                var down = new MapPickRay(top, -Vector3.UnitY, (float)(envelope.MaxY - envelope.MinY) + 2);
                Assert.Equal(bareQueries.Pick(down)?.PlacementId, queries.Pick(down)?.PlacementId);
                Assert.NotNull(queries.Pick(down));
            }
        }
    }

    [Fact]
    public void IndependentHeadsAndStorageForms_ProduceIdenticalOwnedFoliage()
    {
        var owned = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stack-low"] = "floor-0",
            ["stack-mid"] = "floor-3",
            ["stack-top"] = "floor-7",
            [SurfaceLayer] = SurfaceFloor,
            [BridgeLayer] = BridgeFloor,
            [RoomLayer] = BridgeFloor,
            [CaveLayer] = DeepFloor,
        };
        foreach (var (name, fixture, _, _, _, _) in Cases())
        {
            IReadOnlyList<FoliageFixture> heads = Heads(fixture);
            Assert.Equal(4, heads.Count);
            Assert.Equal(4, heads.Select(h => h.Document).Distinct(ReferenceEqualityComparer.Instance).Count());
            Assert.Equal(4, heads.Select(h => h.Assets).Distinct(ReferenceEqualityComparer.Instance).Count());
            MapBuiltWorld[] worlds = heads.Select(Build).ToArray();
            Assert.Single(worlds.Select(w => w.BuildHash).Distinct());
            Assert.Single(worlds.Select(Statics).Distinct());
            Assert.Single(worlds.Select(NavCapture).Distinct());
            string[] foliage = heads.Select(h => Canonical(Batches(h))).ToArray();
            Assert.Single(foliage.Distinct());
            foreach (MapFoliageSampleBatch batch in Batches(heads[3]))
            {
                Assert.Equal(MapFoliageStatus.Complete, batch.Status);
                Assert.NotEmpty(batch.Samples);
                Assert.All(batch.Samples, s => Assert.Equal(owned[batch.LayerId], s.Key.Address.SupportOwnerId));
            }
            // Each head's world holds no foliage collider: its statics equal those of the same document without foliage.
            Assert.Equal(Statics(Build(new(Without(fixture.Document), fixture.Assets))), Statics(worlds[0]));
            Assert.True(worlds[0].Statics.All(s => s.Kind is MapStaticKind.Placement or MapStaticKind.TerrainChunk), name);
        }
    }

    [Fact]
    public void ColliderBuilder_ReferencesNoFoliageType()
    {
        // The metadata of the physics consumer, not its source text: no type it references is a foliage type, so no
        // collider or static can be built from a generated sample.
        using FileStream stream = File.OpenRead(typeof(MapWorldBuilder).Assembly.Location);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        string[] referenced = metadata.TypeReferences.Select(h => metadata.GetTypeReference(h))
            .Select(t => metadata.GetString(t.Namespace) + "." + metadata.GetString(t.Name)).ToArray();
        Assert.Contains(referenced, name => name.StartsWith("KhaozEngine.MapDoc.Surfaces.", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => name.StartsWith("KhaozEngine.MapDoc.Foliage.", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => name.Contains("Foliage", StringComparison.Ordinal));
        string[] members = metadata.MemberReferences.Select(h => metadata.GetString(metadata.GetMemberReference(h).Name)).ToArray();
        Assert.DoesNotContain(members, name => name.Contains("Foliage", StringComparison.Ordinal));
    }

    // The released stacked-support stack and the stacked prefab-cave topology, each with the layer the edits change, a point
    // inside its floor for the beacon, the floor that seats the beacon's mask and an asset of its closure for the added rule.
    // The prefab water caves are left out: their released caves have no walls, so their coverage builds no world.
    static IEnumerable<(string Name, FoliageFixture Fixture, string Layer, Vector2 Beacon, string Seat, string Asset)> Cases() =>
    [
        ("stacked", Stacked(), "stack-mid", new(1, 1), "floor-3", "tree"),
        ("cave", Cave(), CaveLayer, new(1.5f, -2.5f), DeepFloor, "item"),
    ];

    static MapBuiltWorld Build(FoliageFixture f) =>
        MapWorldBuilder.Build(f.Document, f.Assets, new(FoliageFixtures.Options(), "foliage-world/1", new()));

    static MapDocument Without(MapDocument document)
    {
        MapDocument copy = MapDocumentFile.LoadText(MapDocumentFile.SaveText(document));
        copy.Foliage = null;
        return copy;
    }

    // Every static and every placement's interaction envelope, so a sample-driven collider or pick target would show.
    static string Statics(MapBuiltWorld world) =>
        string.Join("\n", world.Statics.Select(s => s.OwnerId + "|" + s.Kind + "|" + s.Digest + "|" + s.Bounds)) + "\n" +
        string.Join("\n", world.Placements.Select(p => p.PlacementId + "|" + p.Digest + "|" + p.Envelope.Bounds));

    // One navigation tile per storage tile, aligned to the world's own storage tiles.
    static MapWorldGrids Grids(MapBuiltWorld world) => new(world.TileSize, 1, 4, Vector2.Zero);

    static string NavCapture(MapBuiltWorld world) => string.Join("\n", MapNavTiling.Partition(world, Grids(world), Nav).Select(t =>
        t.Coord + "|" + t.Bounds + "|" + t.CaptureBounds + "|" + t.GeometryDigest + "|" + t.MediumDigest + "|" + t.CaptureIdentity));
}
