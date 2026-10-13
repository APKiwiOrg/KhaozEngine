using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    internal const string ActiveTransferId = "unpacked-item";

    /// <summary>A detached copy whose root projection is computed at the released format 6.</summary>
    internal static string FormatSixRootDigest(MapDocument document)
    {
        MapDocument copy = MapDocumentFile.LoadText(MapDocumentFile.SaveText(document));
        copy.FormatVersion = 6;
        return MapSurfaceSemantics.RootDigest(copy);
    }

    /// <summary>The pinned stored form of a complete document, written through the released tiled writer.</summary>
    internal static MapStoredSurfaceSource Stored(MapDocument document)
    {
        string directory = TempDirectory();
        MapDocumentFile.SaveTiled(document, directory);
        return MapStoredSurfaceSource.Open(directory);
    }

    internal static IReadOnlyList<MapPrefabBinding> LiveBindings(MapDocument document) =>
        (document.Prefabs?.Instances ?? []).Where(i => !i.Removed)
            .OrderBy(i => i.Id, StringComparer.Ordinal)
            .SelectMany(i => i.Bindings.Entries.Where(b => !b.Removed)
                .OrderBy(b => b.Child.Kind.ToString(), StringComparer.Ordinal).ThenBy(b => b.Child.Key, StringComparer.Ordinal))
            .ToArray();

    /// <summary>Removes the ordinal first child of a kind and every child that references a removed child, so the
    /// remaining asset stays closed.</summary>
    internal static MapPrefabAsset WithChildRemoved(MapPrefabAsset asset, MapPrefabChildKind kind)
    {
        var removed = new HashSet<MapPrefabChildKey>
        {
            asset.Children.Where(c => c.Key.Kind == kind).OrderBy(c => c.Key.Key, StringComparer.Ordinal).First().Key,
        };
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (MapPrefabChild child in asset.Children.Where(c => !removed.Contains(c.Key)))
                if (LocalTargets(child).Any(removed.Contains)) changed |= removed.Add(child.Key);
        }
        return asset with
        {
            Children = asset.Children.Where(c => !removed.Contains(c.Key)).ToArray(),
            Roofs = asset.Roofs.Where(r => r.Scope == MapPrefabReferenceScope.World || !removed.Contains(new(r.Kind, r.Id))).ToArray(),
        };
    }

    static IEnumerable<MapPrefabChildKey> LocalTargets(MapPrefabChild child)
    {
        var references = child.References.Select(s => s.Target).ToList();
        switch (child)
        {
            case MapPrefabLightChild { Value.Owner: { } owner }: references.Add(owner); break;
            case MapPrefabRoofChild roof: references.Add(roof.Value.Roof); references.AddRange(roof.Value.Rooms); references.AddRange(roof.Value.Groups); break;
            case MapPrefabRoofGroupChild group: references.AddRange(group.Value.Rooms); break;
        }
        return references.Where(r => r.Scope == MapPrefabReferenceScope.Local).Select(r => new MapPrefabChildKey(r.Kind, r.Id));
    }

    /// <summary>The leaf light resource payload. Its owner is bound to each consuming placement.</summary>
    internal static MapLocalLightDoc GlowLight() => new(1, "glow",
        new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "$owner"), new(0, 2, 0), Vector3.One, 1, 3, false);

    /// <summary>A leaf support resource with one 2 by 2 m floor at 0.5 m and one embedded authored chain. A
    /// <paramref name="worldSource"/> gives the chain a World-scoped source patch.</summary>
    internal static MapSupportResource DeckSupport(MapPatchKey? worldSource = null)
    {
        var top = Patch("top", 2, 2, 50);
        top.Records.Add(new MapBoundaryChain("top-edge", MapChainKind.Authored, worldSource, new[]
        {
            new MapChainVertex(new("top", MapLatticeAddress.Corner(0, 0)), 50),
            new MapChainVertex(new("top", MapLatticeAddress.Corner(2, 0)), 50),
        }));
        var surfaces = new MapSurfaceSet();
        surfaces.Refs.Add(Surface("top", MapSurfaceRole.SupportFloor));
        surfaces.Patches.Add(top.Key, top);
        var slots = new List<MapPrefabReferenceSlot>
        {
            Surf("/patches/0/key/surfaceId", "top"),
            Surf("/patches/0/records/0/vertices/0/vertex/surfaceId", "top"),
            Surf("/patches/0/records/0/vertices/1/vertex/surfaceId", "top"),
        };
        if (worldSource is { } source) slots.Add(Surf("/patches/0/records/0/sourcePatch/surfaceId", source.SurfaceId, MapPrefabReferenceScope.World));
        return new(1, surfaces, null, slots);
    }

    /// <summary>The deck's support and light, captured as explicit asset children with their materialized rows.</summary>
    internal static MapPrefabAsset Materialized(IReadOnlyList<MapPrefabChild> owners)
    {
        const string surface = "deck/deck-support/top", chain = "deck/deck-support/top-edge", light = "deck/glow-light/glow";
        var patch = Patch(surface, 2, 2, 50);
        var captured = new MapPrefabChild[]
        {
            new MapPrefabSurfaceChild(new(MapPrefabChildKind.Surface, surface), Surface(surface, MapSurfaceRole.SupportFloor), new[] { patch })
            { References = Slots(Surf("/patches/0/key/surfaceId", surface)) },
            new MapPrefabRecordChild(new(MapPrefabChildKind.Record, chain), new MapBoundaryChain(chain, MapChainKind.Authored, null, new[]
            {
                new MapChainVertex(new(surface, MapLatticeAddress.Corner(0, 0)), 50),
                new MapChainVertex(new(surface, MapLatticeAddress.Corner(2, 0)), 50),
            }), patch.Key)
            {
                References = Slots(Anchor(surface), Surf("/value/vertices/0/vertex/surfaceId", surface), Surf("/value/vertices/1/vertex/surfaceId", surface)),
            },
            new MapPrefabLightChild(new(MapPrefabChildKind.Light, light), GlowLight() with
            { Id = light, Owner = new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "deck") }),
        };
        var frame = new MapEffectiveTransform(-2, 0, -2, 0, 1);
        MapPrefabLocalMaterializedChild Row(MapPrefabChild child) =>
            new(new(MapPrefabReferenceScope.Local, child.Key.Kind, child.Key.Key), new(0, 0, 0, 0, 1), MapPrefabIdentity.ChildDigest(child));
        var deck = new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "deck");
        return new(1, "hut", owners.Concat(captured).ToArray())
        {
            MaterializedAttachments = new[]
            {
                new MapPrefabLocalMaterialization(deck, "deck-support", ResourceSha("deck-support"), MapPrefabAttachmentKind.Support, frame,
                    new[] { Row(captured[0]), Row(captured[1]) }),
                new MapPrefabLocalMaterialization(deck, "glow-light", ResourceSha("glow-light"), MapPrefabAttachmentKind.Light, frame,
                    new[] { Row(captured[2]) }),
            },
        };
    }

    static string ResourceSha(string id) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(id switch
    {
        "deck-support" => MapSupportResourceFile.Write(DeckSupport()),
        "glow-light" => MapLightFile.Write(GlowLight()),
        _ => throw new ArgumentException(id),
    }));

    static bool TryIdentityVariant(string variant, out PrefabFixture? fixture)
    {
        fixture = variant switch
        {
            "all-kinds" => Create("all-resources"),
            _ when variant.StartsWith("unpacked-", StringComparison.Ordinal) => Unpacked(Create("all-resources"), variant["unpacked-".Length..]),
            _ when variant.StartsWith("attachment", StringComparison.Ordinal) => Attachment(Create(), variant),
            "native-without-prefabs" => WithoutPrefabs(Create()),
            "transform" or "override" or "binding" or "reservation" or "roof-group" or "missing-binding" or "duplicate-world-id" or
                "standalone-collision" or "dangling-transfer" or "retired-live" or "numeric-duplicate" or "active-transfer" => Changed(Create(), variant),
            _ => null,
        };
        return fixture is not null;
    }

    static PrefabFixture WithoutPrefabs(PrefabFixture f)
    {
        f.Document.Prefabs = null;
        return f;
    }

    static PrefabFixture Changed(PrefabFixture f, string variant)
    {
        MapDocument d = f.Document;
        MapPrefabInstance instance = d.Prefabs!.Instances.Single();
        MapPrefabBinding Binding(MapPrefabChildKind kind, string key) => instance.Bindings.Entries.Single(b => b.Child == new MapPrefabChildKey(kind, key));
        MapPrefabBinding[] With(MapPrefabBinding old, MapPrefabBinding? replacement) =>
            instance.Bindings.Entries.Where(b => b != old).Concat(replacement is null ? [] : new[] { replacement }).ToArray();
        IReadOnlyList<MapPrefabBindingTransfer> transfers = [];
        switch (variant)
        {
            case "transform": instance = instance with { Transform = new(new(1.5f, 0, -2), 0.371f, 1.2f) }; break;
            case "override":
                instance = instance with { Overrides = new MapPrefabOverride[] { new MapPrefabTransformOverride(new(MapPrefabChildKind.Marker, "entry"), new(new(0.5f, 0, 0), 0.25f, 1)) } };
                break;
            case "binding":
                var item = Binding(MapPrefabChildKind.Placement, "item");
                instance = instance with { Bindings = new(With(item, item with { WorldId = "a/item-renamed" })) };
                break;
            case "reservation":
                d.NumericIdHighWaterMark++;
                instance = instance with
                {
                    Bindings = new(instance.Bindings.Entries.Append(new MapPrefabBinding(new(MapPrefabChildKind.Placement, "retired-item"),
                        "a/retired-item", d.NumericIdHighWaterMark, true)).ToArray()),
                };
                break;
            case "roof-group":
                d.BuildingIntent = new(1, [], new[] { new MapRoofGroupDoc("world-rooms",
                    new[] { new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "exterior") }) }, [], []);
                break;
            case "missing-binding":
                instance = instance with { Bindings = new(With(Binding(MapPrefabChildKind.Record, "room"), null)) };
                break;
            case "duplicate-world-id":
                var ceiling = Binding(MapPrefabChildKind.Surface, "ceiling");
                instance = instance with { Bindings = new(With(ceiling, ceiling with { WorldId = Binding(MapPrefabChildKind.Surface, "floor").WorldId })) };
                break;
            case "standalone-collision": d.Placements.Add(Standalone(Binding(MapPrefabChildKind.Placement, "item").WorldId, null)); break;
            case "numeric-duplicate": d.Placements.Add(Standalone("standalone", Binding(MapPrefabChildKind.Placement, "item").NumericId)); break;
            case "dangling-transfer": transfers = new[] { Transfer(false) }; break;
            case "retired-live":
                d.Placements.Add(Standalone(ActiveTransferId, null));
                transfers = new[] { Transfer(true) with { Binding = Transfer(true).Binding with { NumericId = null } } };
                break;
            case "active-transfer":
                d.NumericIdHighWaterMark++;
                d.Placements.Add(Standalone(ActiveTransferId, d.NumericIdHighWaterMark));
                transfers = new[] { Transfer(false) with { Binding = Transfer(false).Binding with { NumericId = d.NumericIdHighWaterMark } } };
                break;
        }
        d.Prefabs = new(1, 1, new[] { instance }, transfers);
        return f;
    }

    /// <summary>Instance a retains its complete inventory. Only the first child of a kind was transferred, while
    /// the others are dormant reservations. Kinds with a standalone home get an active transfer and that owner.
    /// Variant kept preserves a live source producer, and lost drops the required transfer.</summary>
    static PrefabFixture Unpacked(PrefabFixture f, string variant)
    {
        MapDocument d = f.Document;
        MapPrefabInstance instance = d.Prefabs!.Instances.Single();
        var kind = variant is "kept" or "lost" ? MapPrefabChildKind.Placement : Enum.Parse<MapPrefabChildKind>(variant);
        MapPrefabBinding moved = instance.Bindings.Entries.Where(b => b.Child.Kind == kind).OrderBy(b => b.Child.Key, StringComparer.Ordinal).First();
        bool active = kind is MapPrefabChildKind.Placement or MapPrefabChildKind.Surface or MapPrefabChildKind.Light or MapPrefabChildKind.Marker or
            MapPrefabChildKind.Storey or MapPrefabChildKind.RoofGroup;
        string id = moved.WorldId;
        var intent = d.BuildingIntent ?? new MapBuildingIntent(1, [], [], [], []);
        switch (active ? kind : (MapPrefabChildKind?)null)
        {
            case MapPrefabChildKind.Placement: d.Placements.Add(Standalone(id, moved.NumericId)); break;
            case MapPrefabChildKind.Surface: AddStandaloneSurface(d, id, null); break;
            case MapPrefabChildKind.Light: intent = With(intent, lights: new MapLocalLightDoc(1, id, null, Vector3.One, Vector3.One, 1, 2, false)); break;
            case MapPrefabChildKind.Marker: intent = With(intent, markers: new MapLocalMarkerDoc(1, id, "Entry", Vector3.Zero, 0, "entrance", true, [])); break;
            case MapPrefabChildKind.Storey: intent = With(intent, storeys: new KhaozEngine.MapDoc.Interiors.MapStoreyDoc(1, id, "Lower", null)); break;
            case MapPrefabChildKind.RoofGroup:
                intent = With(intent, groups: new MapRoofGroupDoc(id, new[] { new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "exterior") }));
                break;
        }
        if (intent.RoofGroups.Count + intent.Lights.Count + intent.Markers.Count + intent.Storeys.Count > 0) d.BuildingIntent = intent;
        var kept = variant == "kept" ? instance.Bindings.Entries : instance.Bindings.Entries.Select(b => b with { Removed = b != moved }).ToArray();
        IReadOnlyList<MapPrefabBindingTransfer> transfers = variant == "lost" ? [] : new[] { new MapPrefabBindingTransfer("a", moved with { Removed = false }) { Retired = !active } };
        d.Prefabs = new(1, 1, new[] { instance with { Removed = variant != "kept", Unpacked = variant != "kept", Bindings = new(kept.ToArray()) } }, transfers);
        return f;
    }

    static MapBuildingIntent With(MapBuildingIntent intent, MapLocalLightDoc? lights = null, MapLocalMarkerDoc? markers = null,
        KhaozEngine.MapDoc.Interiors.MapStoreyDoc? storeys = null, MapRoofGroupDoc? groups = null) =>
        new(1, intent.RoofLinks, groups is null ? intent.RoofGroups : intent.RoofGroups.Append(groups).ToArray(),
            lights is null ? intent.Lights : intent.Lights.Append(lights).ToArray(), markers is null ? intent.Markers : intent.Markers.Append(markers).ToArray())
        { Roofs = intent.Roofs, Storeys = storeys is null ? intent.Storeys : intent.Storeys.Append(storeys).ToArray() };

    static void AddStandaloneSurface(MapDocument d, string id, MapTopologyRecord? record)
    {
        var patch = Patch(id, 2, 2, 0);
        if (record is not null) patch.Records.Add(record);
        d.Surfaces.Patches.Add(patch.Key, patch);
        var surface = Surface(id, MapSurfaceRole.SupportFloor);
        d.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                new[] { new KeyValuePair<MapPatchKey, string>(patch.Key, MapSurfaceSemantics.PatchDigest(patch)) }),
        });
    }

    /// <summary>An unpacked deck: a standalone placement, its transferred support surface and embedded chain, their
    /// precise descriptor pins and one active attachment row. Fault variants remove a pin, change a digest or drop
    /// the record anchor.</summary>
    static PrefabFixture Attachment(PrefabFixture f, string variant)
    {
        MapDocument d = f.Document;
        long numeric = ++d.NumericIdHighWaterMark;
        d.Placements.Add(new MapPlacement { Id = "unpacked-deck", Kind = "deck", AssetId = "deck", X = 3, Y = -2, Z = -3, Scale = 1, NumericId = numeric });
        var anchor = new MapPatchKey("unpacked-top", 0, 0);
        AddStandaloneSurface(d, "unpacked-top", new MapBoundaryChain("unpacked-edge", MapChainKind.Authored, null, new[]
        {
            new MapChainVertex(new("unpacked-top", MapLatticeAddress.Corner(0, 0)), 0),
            new MapChainVertex(new("unpacked-top", MapLatticeAddress.Corner(2, 0)), 0),
        }));
        var surfaceKey = new MapPrefabChildKey(MapPrefabChildKind.Surface, "deck/deck-support/top");
        var recordKey = new MapPrefabChildKey(MapPrefabChildKind.Record, "deck/deck-support/top-edge");
        var pose = new MapEffectiveTransform(3, -2, -3, 0, 1);
        string surfaceDigest = new('b', 64), recordDigest = new('c', 64);
        var poses = new List<MapStandaloneResourceTransform>
        {
            new(MapPrefabChildKind.Surface, "unpacked-top", pose, surfaceDigest),
            new(MapPrefabChildKind.Record, "unpacked-edge", pose, recordDigest),
        };
        if (variant == "attachment-missing-pin") poses.RemoveAt(0);
        d.NativeResourceTransforms = poses;
        var children = new[]
        {
            new MapPrefabAttachedChild(surfaceKey, "unpacked-top", null, new(0, 0, 0, 0, 1), variant == "attachment-digest" ? new string('d', 64) : surfaceDigest),
            new MapPrefabAttachedChild(recordKey, "unpacked-edge", variant == "attachment-missing-anchor" ? null : anchor, new(0, 0, 0, 0, 1), recordDigest),
        };
        var transfers = new[]
        {
            new MapPrefabBindingTransfer("unpacked", new(new(MapPrefabChildKind.Placement, "deck"), "unpacked-deck", numeric, false)),
            new MapPrefabBindingTransfer("unpacked", new(surfaceKey, "unpacked-top", null, false)),
            new MapPrefabBindingTransfer("unpacked", new(recordKey, "unpacked-edge", null, false)),
        };
        d.Prefabs = new(1, 1, d.Prefabs!.Instances, transfers)
        {
            Attachments = new[] { new MapPrefabAttachmentTransfer("unpacked-deck", "deck-support", ResourceSha("deck-support"),
                MapPrefabAttachmentKind.Support, children, false) },
        };
        return f;
    }

    static MapPrefabBindingTransfer Transfer(bool retired) =>
        new("unpacked", new MapPrefabBinding(new(MapPrefabChildKind.Placement, "item"), ActiveTransferId, null, false)) { Retired = retired };

    static MapPlacement Standalone(string id, long? numericId) =>
        new() { Id = id, Kind = "item", AssetId = "item", X = 3, Y = -2, Z = -3, Scale = 1, NumericId = numericId };
}
