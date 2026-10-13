using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal sealed record PrefabMaterializedExtractionFixture(MapDocument Document, MapAssetClosure Assets,
    IReadOnlyList<MapPrefabReference> Selection, MapTransform Origin);

internal static partial class PrefabFixtures
{
    internal static string SemanticIntent(MapEffectiveResources resources)
    {
        var intent = resources.BuildingIntent;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Roofs = intent.Roofs.OrderBy(r => r.Id).ToArray(),
            Links = intent.RoofLinks.OrderBy(r => r.Id).ToArray(),
            Groups = intent.RoofGroups.OrderBy(r => r.Id).ToArray(),
            Lights = MapEffectiveAnnotations.Lights(resources),
            Markers = MapEffectiveAnnotations.Markers(resources).Select(m => new
            { m.Id, m.Name, m.Position, m.YawRadians, m.Role, m.Enabled, m.Tags }),
            Storeys = intent.Storeys.OrderBy(s => s.Id).Select(s => new { s.Id, s.Name }),
        });
    }

    internal static PrefabFixture SpacelessRoomless()
    {
        var f = Create("roomless");
        f.Document.Prefabs = new(1, 1, [], []);
        var ground = Patch("ground", 16, 16, -200);
        f.Document.Surfaces.Refs.Add(Surface("ground", MapSurfaceRole.SupportFloor));
        f.Document.Surfaces.Patches.Add(ground.Key, ground);
        return f;
    }

    internal static PrefabFixture SupportWithEmbeddedRecord()
    {
        var f = Create("support-resource");
        var support = FloorResource();
        var patch = support.Patches.Values.Single();
        patch.Records.Add(new MapBoundaryChain("deck-edge", MapChainKind.Authored, null, new[]
        {
            new MapChainVertex(new("floor", MapLatticeAddress.Corner(0, 0)), 0),
            new MapChainVertex(new("floor", MapLatticeAddress.Corner(4, 0)), 0),
        }));
        var resource = new MapSupportResource(1, support, null, Slots(Surf("/patches/0/key/surfaceId", "floor"),
            Surf("/patches/0/records/0/vertices/0/vertex/surfaceId", "floor"),
            Surf("/patches/0/records/0/vertices/1/vertex/surfaceId", "floor")));
        return Reasset(f, f.Assets.GetPrefab("hut"), captured: SupportAssets(f.Assets.GetPrefab("hut"), resource));
    }

    internal static PrefabFixture CapturedOriginals()
    {
        var f = Create("attached");
        var owner = f.Assets.GetPrefab("hut").Children.OfType<MapPrefabPlacementChild>().Single(c => c.Key.Key == "deck");
        var asset = new MapPrefabAsset(1, "hut", new[] { owner });
        var bytes = new Dictionary<string, byte[]>
        {
            ["deck-support"] = MapSupportResourceFile.Write(new(1, FloorResource(), null, Slots(Surf("/patches/0/key/surfaceId", "floor")))),
            ["glow-light"] = MapLightFile.Write(GlowLight() with { Position = new(0, 1, 0), Radius = 2 }),
        };
        var captured = PublishPrefab(f.Assets, asset, bytes);
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with { Bindings = new([]) } }, []);
        return Reasset(f, asset, captured: captured);
    }

    internal static PrefabMaterializedExtractionFixture MaterializedExtraction()
    {
        var f = CapturedOriginals();
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var row = unpack.Prefabs!.Attachments.Single(a => a.Kind == MapPrefabAttachmentKind.Support);
        var surface = row.Children.Single(c => c.LocalChild.Kind == MapPrefabChildKind.Surface);
        var patch = unpack.Surfaces.Patches.Values.Single(p => p.Key.SurfaceId == surface.WorldId);
        Array.Fill(patch.Heights, 50);
        var lightRow = unpack.Prefabs.Attachments.Single(a => a.Kind == MapPrefabAttachmentKind.Light);
        string lightId = lightRow.Children.Single().WorldId;
        var intent = unpack.BuildingIntent!;
        unpack.BuildingIntent = new(1, intent.RoofLinks, intent.RoofGroups,
            intent.Lights.Select(l => l.Id == lightId ? l with { Position = new(0, 2, 0), Radius = 3 } : l).ToArray(), intent.Markers)
        { Storeys = intent.Storeys, Roofs = intent.Roofs };
        RefreshCapturedPins(unpack);
        var selected = unpack.Prefabs.Transfers.Where(t => !t.Retired).Select(t => World(t.Binding.Child.Kind, t.Binding.WorldId)).ToArray();
        return new(unpack, f.Assets, selected, new(new(-2, 0, -2), 0, 1));
    }

    internal static void RefreshCapturedPins(MapDocument document)
    {
        var set = document.Prefabs!;
        var poses = document.NativeResourceTransforms!.ToDictionary(p => (p.Kind, p.OwnerId));
        document.Prefabs = new(1, 1, set.Instances, set.Transfers)
        {
            Attachments = set.Attachments.Select(row => row with
            {
                Children = row.Children.Select(c =>
                {
                    MapPrefabChild child = c.LocalChild.Kind switch
                    {
                        MapPrefabChildKind.Surface => new MapPrefabSurfaceChild(new(c.LocalChild.Kind, c.WorldId),
                            document.Surfaces.Refs.Single(s => s.Id == c.WorldId), document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == c.WorldId)
                                .Select(p => { var copy = p.Clone(); copy.Records.Clear(); return copy; }).ToArray()),
                        MapPrefabChildKind.Light => new MapPrefabLightChild(new(c.LocalChild.Kind, c.WorldId), document.BuildingIntent!.Lights.Single(l => l.Id == c.WorldId)),
                        _ => throw new InvalidOperationException(),
                    };
                    var (_, occurrences) = MapPrefabReferenceWalker.Child(child, MapPrefabReferenceMap.Identity);
                    child = child with { References = MapPrefabReferences.Slots(occurrences, MapPrefabReferenceScope.World) };
                    string digest = MapPrefabAttachmentDescriptors.Digest(child, poses[(c.LocalChild.Kind, c.WorldId)].Transform);
                    poses[(c.LocalChild.Kind, c.WorldId)] = poses[(c.LocalChild.Kind, c.WorldId)] with { SourceDigest = digest };
                    return c with { SourceDigest = digest };
                }).ToArray(),
            }).ToArray(),
        };
        document.NativeResourceTransforms = poses.Values.ToArray();
    }

    internal static MapAssetClosure PublishPrefab(MapAssetClosure original, MapPrefabAsset asset,
        IReadOnlyDictionary<string, byte[]>? replacements = null, ArmedAssetSource? source = null)
    {
        var resources = original.CapturedResources.ToDictionary(r => r.Reference.Id, r => r.Bytes.ToArray(), StringComparer.Ordinal);
        resources[asset.Id] = MapPrefabFile.Write(asset);
        if (replacements is not null) foreach (var pair in replacements) resources[pair.Key] = pair.Value;
        var manifest = JsonNode.Parse(resources["manifest"])!.AsObject();
        var rows = manifest["resources"]!.AsArray();
        if (!rows.Any(r => r!["reference"]!["id"]!.GetValue<string>() == asset.Id))
        {
            var template = rows.Single(r => r!["reference"]!["id"]!.GetValue<string>() == "hut")!.DeepClone();
            template["reference"]!["id"] = asset.Id;
            template["reference"]!["path"] = "assets/" + asset.Id;
            rows.Add(template);
        }
        foreach (var row in rows)
            row!["reference"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(resources[row["reference"]!["id"]!.GetValue<string>()]));
        resources["manifest"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var roots = original.Roots.Select(r => r with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(resources[r.Id])) }).ToArray();
        source ??= new();
        source.Bytes = resources;
        return MapAssetClosure.Load(roots, source);
    }

    internal sealed class ArmedAssetSource : IMapAssetSource
    {
        internal Dictionary<string, byte[]> Bytes { get; set; } = new();
        internal bool Armed { get; set; }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => Armed
            ? throw new InvalidOperationException("Captured resource source must not be read again") : Bytes[reference.Id];
    }

    internal static PrefabFixture ExtractedFixture(MapPrefabAsset asset, MapAssetClosure? original = null, MapTransform? site = null)
    {
        var f = SpacelessRoomless();
        var assets = PublishPrefab(original ?? f.Assets, asset);
        f.Document.NativeAssets = assets.Roots.ToList();
        var result = MapPrefabEdits.Prepare(f.Document, assets, new MapPlacePrefab("copy", asset.Id,
            site ?? new(new(10.25f, 5, -3.125f), 0.371f, 1.2f)));
        return new(result.After, assets);
    }

    internal static MapEffectiveResources PlaceExtracted(MapPrefabAsset asset) =>
        Effective(ExtractedFixture(asset, CapturedOriginals().Assets, new(new(12, 0, -12), 0, 1)));

    internal static void AssertCapturedMaterializedSet(MapEffectiveResources resources)
    {
        var support = Assert.Single(resources.Surfaces.Refs, s => s.Id != "ground");
        var patch = MapEffectiveGeometry.Patch(resources.CompleteView(), new(support.Id, 0, 0));
        Assert.All(patch.ExactVertices, v => Assert.Equal(MapExactValue.FromSingle(0.5f), v.Y));
        var light = Assert.Single(MapEffectiveAnnotations.Lights(resources));
        Assert.Equal(new MapExactPoint(new(12, 1), new(2, 1), new(-12, 1)), light.Position);
        Assert.Equal(3, light.Radius);
        Assert.Single(resources.Placements);
        Assert.DoesNotContain(resources.Surfaces.Refs, s => s.Id.Contains("deck-support", StringComparison.Ordinal) && s.Id != support.Id);
    }
}
