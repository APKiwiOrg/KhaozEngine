using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    internal static PrefabFixture Cave(string variant)
    {
        if (variant == "stacked") return StackedPrisms();
        if (variant == "transformed")
        {
            var seam = Seam();
            var instance = seam.Document.Prefabs!.Instances.Single() with
            { Transform = new(new(30000.25f, -450, -29999.75f), 0.371f, 1.2f) };
            seam.Document.Prefabs = new(1, 1, new[] { instance }, []);
            AuthorWorldParent(seam, instance.Transform);
            return seam;
        }
        var f = Create();
        var asset = f.Assets.GetPrefab("hut");
        var children = asset.Children.ToList();
        if (variant == "west-portal")
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is MapPrefabRecordChild { Value: MapBoundaryChain chain } edge && chain.Id is "door-bottom" or "door-top")
                    children[i] = edge with
                    {
                        Value = chain with
                        {
                            Vertices = new[]
                    { new MapChainVertex(new(chain.Vertices[0].Vertex.SurfaceId, MapLatticeAddress.Corner(0, 0)), chain.Vertices[0].HeightUnits),
                        new MapChainVertex(new(chain.Vertices[1].Vertex.SurfaceId, MapLatticeAddress.Corner(0, 4)), chain.Vertices[1].HeightUnits) }
                        }
                    };
                if (children[i] is MapPrefabRecordChild { Value: MapCavePortal portal } doorway)
                    children[i] = doorway with
                    {
                        Value = portal with
                        {
                            Interval = new[]
                    { new MapLatticeVertex("floor", MapLatticeAddress.Corner(0, 0)), new MapLatticeVertex("floor", MapLatticeAddress.Corner(0, 4)) }
                        }
                    };
            }
        var index = children.FindIndex(c => c.Key.Key == "room-domain");
        var record = (MapPrefabRecordChild)children[index];
        var domain = (MapPrismDomain)record.Value;
        if (variant == "second-room-domain")
        {
            var anchor = new MapPatchKey("floor", 0, 0);
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "legacy-room-domain"),
                new MapSpaceFootprint("legacy-room-domain", new("room", anchor), anchor,
                    Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 4).Select(x => z * 64 + x)).ToArray(),
                    new(MapBoundKind.SupportFloor, "floor", null), new(MapBoundKind.Ceiling, "ceiling", null)), anchor)
            {
                References = Slots(Anchor("floor"), Ref("/value/space", "room", "floor"), Surf("/value/lattice/surfaceId", "floor"),
                Surf("/value/lower/surfaceId", "floor"), Surf("/value/upper/surfaceId", "ceiling"))
            });
        }
        if (variant == "missing-parent")
        {
            int room = children.FindIndex(c => c.Key.Key == "room");
            var r = (MapPrefabRecordChild)children[room];
            children[room] = r with { Value = ((MapSpaceDoc)r.Value) with { Parent = null }, References = Slots(Anchor("floor")) };
        }
        if (variant == "geometry-drift") domain = domain with { Footprint = Domain(0, 0, 3, 4) };
        if (variant == "collinear") domain = domain with
        { Footprint = new(MapLatticeFrame.ImportedMetreCentimetre, [], new[] { Ring((0, 0), (2, 0), (4, 0), (4, 4), (2, 4), (0, 4)) }) };
        if (variant == "outside-parent") domain = domain with { Footprint = Domain(0, 0, 17, 4) };
        if (variant == "zero-height") domain = domain with { UpperY = 0 };
        if (variant == "oversized-prism") domain = domain with
        { Footprint = new(MapLatticeFrame.ImportedMetreCentimetre, Enumerable.Repeat(new MapWaterRect(0, 0, 4, 4), 1500).ToArray(), []) };
        if (variant == "floor-hole")
            foreach (var patch in children.OfType<MapPrefabSurfaceChild>().Single(c => c.Key.Key == "floor").Patches) Array.Fill(patch.Presence, 0UL);
        if (variant == "self-intersection") domain = domain with
        { Footprint = new(MapLatticeFrame.ImportedMetreCentimetre, [], new[] { Ring((0, 0), (4, 4), (0, 4), (4, 0)) }) };
        if (variant == "reflex-storeys") domain = domain with
        { Footprint = new(MapLatticeFrame.ImportedMetreCentimetre, [], new[] { Ring((0, 0), (4, 0), (4, 2), (2, 2), (2, 4), (0, 4)) }) };
        children[index] = record with { Value = domain };
        if (variant is "peer-overlap" or "adjacent" or "reflex-storeys")
            AddRoom("peer-room", variant == "peer-overlap" ? Domain(1, 1, 3, 3) :
                variant == "adjacent" ? Domain(4, 0, 8, 4) : Domain(2, 2, 4, 4), 0, 3);
        if (variant == "reflex-storeys") AddRoom("upper-room", Domain(0, 0, 4, 4), 3, 6);
        if (variant == "nested") AddRoom("inner-room", Domain(1, 1, 3, 3), 1, 2, "room");
        if (variant is "uneven-parent" or "uneven-reverse")
        {
            children.RemoveAll(c => c.Key.Key is "room-portal" or "door-bottom" or "door-top");
            var patch = f.Document.Surfaces.Patches.Values.Single();
            for (int z = 0; z <= patch.Depth; z++)
                for (int x = 0; x <= patch.Width; x++) patch.Heights[z * (patch.Width + 1) + x] = variant == "uneven-parent" ? -50 + 25 * x : 50 - 25 * x;
            var surface = f.Document.Surfaces.Refs.Single();
            f.Document.Surfaces.Refs[0] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                new[] { new KeyValuePair<MapPatchKey, string>(patch.Key, MapSurfaceSemantics.PatchDigest(patch)) })
            };
        }
        return Reasset(f, asset with { Children = children });

        void AddRoom(string id, MapWaterDomainDoc footprint, float lower, float upper, string parentId = "$parent")
        {
            var anchor = new MapPatchKey("floor", 0, 0);
            var parent = new MapRecordRef(parentId, parentId == "$parent" ? new("$parent", 0, 0) : anchor);
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, id),
                new MapSpaceDoc(id, MapSpaceKind.Room, parent, null, [], [], [], []), anchor)
            { References = Slots(Anchor("floor"), Ref("/value/parent", parentId, parent.Anchor.SurfaceId)) });
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, id + "-domain"),
                new MapPrismDomain(id + "-domain", 1, new(id, anchor), parent, "", "lower", footprint, lower, upper), anchor)
            { References = Slots(Anchor("floor"), Ref("/value/space", id, "floor"), Ref("/value/parent", parentId, parent.Anchor.SurfaceId), Slot("/value/storeyId", MapPrefabChildKind.Storey, "lower")) });
        }
    }

    internal static MapWaterDomainDoc Domain(long x0, long z0, long x1, long z1) =>
        new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(x0, z0, x1, z1) }, []);
    static MapWaterRing Ring(params (long X, long Z)[] vertices) => new(vertices.Select(p => MapLatticeAddress.Corner(p.X, p.Z)).ToArray());
    internal static MapExactPoint RoomProbe(MapEffectiveResources resources, float x, float y, float z) =>
        new MapResourceTransform(resources.Transforms["Record:a/room-domain"])
            .Point(new(MapExactValue.FromSingle(x), MapExactValue.FromSingle(y), MapExactValue.FromSingle(z)));

    // Literal owners are derived from the world direction, not from any product containment predicate.
    static readonly (float X, float Y, float Z, string Owner)[] BoundaryTable =
    {
        (0, 1, -1, "a/room"), (4, 1, -1, "exterior"), (1, 1, -4, "a/room"), (1, 1, 0, "exterior"),
        (0, 1, -4, "a/room"), (4, 1, 0, "exterior"), (2, 1, -2, "a/room"),
        (3, 1, -2, "a/room"), (2, 1, -3, "prefab/a/Record/peer-room"), (3, 1, -3, "prefab/a/Record/peer-room"),
        (2, 0, -2, "a/room"), (2, 2.999f, -2, "a/room"), (2, 3, -2, "prefab/a/Record/upper-room"), (2, 6, -2, "exterior"),
    };
    internal static IReadOnlyList<MapExactPoint> RoomBoundaryPoints(MapEffectiveResources resources) =>
        BoundaryTable.Select(p => BoundaryPoint(resources, p.X, p.Y, p.Z)).ToArray();
    internal static string ExpectedBoundaryOwner(MapEffectiveResources resources, MapExactPoint point) =>
        BoundaryTable.Single(p => BoundaryPoint(resources, p.X, p.Y, p.Z) == point).Owner;
    static MapExactPoint BoundaryPoint(MapEffectiveResources resources, float x, float y, float z)
    {
        (float X0, float Z0, float X1, float Z1, int N, int D)? edge = (x, z) switch
        {
            (0, -1) => (0, -4, 0, 0, 3, 4),
            (4, -1) => (4, -2, 4, 0, 1, 2),
            (1, -4) => (0, -4, 2, -4, 1, 2),
            (1, 0) => (0, 0, 4, 0, 1, 4),
            (3, -2) => (2, -2, 4, -2, 1, 2),
            (2, -3) => (2, -4, 2, -2, 1, 2),
            _ => null,
        };
        if (edge is not { } e) return RoomProbe(resources, x, y, z);
        var a = RoomProbe(resources, e.X0, y, e.Z0); var b = RoomProbe(resources, e.X1, y, e.Z1);
        var t = new MapExactValue(e.N, e.D);
        return new(a.X.Add(b.X.Subtract(a.X).Multiply(t)), a.Y, a.Z.Add(b.Z.Subtract(a.Z).Multiply(t)));
    }

    internal static PrefabFixture PlacedPrisms(string variant, float scale, bool far)
    {
        var f = Cave(variant);
        var source = f.Document.Surfaces.Patches[new("exterior", 0, 0)];
        double height = source.Heights[0] / 100d, dx = (source.Heights[1] - source.Heights[0]) / 100d, dz = (source.Heights[source.Width + 1] - source.Heights[0]) / 100d;
        var transform = new MapTransform(far ? new(30000.25f, -450, -29999.75f) : new(10.25f, 5, -3.125f), 0.371f, scale);
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with { Transform = transform } }, []);
        AuthorWorldParent(f, transform, (x, z) => height + x * dx - z * dz);
        return f;
    }

    internal static PrefabFixture AliasParent(bool roomAlias)
    {
        var f = roomAlias ? Cave("nested") : Create();
        if (!roomAlias)
        {
            var anchor = new MapPatchKey("exterior", 0, 0);
            f.Document.Surfaces.Patches[anchor].Records.Add(new MapSpaceDoc("exterior-alias", MapSpaceKind.Exterior,
                null, new("exterior", anchor), [], [], [], []));
            var instance = f.Document.Prefabs!.Instances.Single() with { ParentSpace = new("exterior-alias", anchor) };
            f.Document.Prefabs = new(1, 1, new[] { instance }, []);
            return f;
        }
        var asset = f.Assets.GetPrefab("hut");
        var floor = new MapPatchKey("floor", 0, 0);
        var alias = new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "room-alias"), new MapSpaceDoc("room-alias", MapSpaceKind.Room,
            new("$parent", new("$parent", 0, 0)), new("room", floor), [], [], [], []), floor)
        { References = Slots(Anchor("floor"), Ref("/value/parent", "$parent", "$parent"), Ref("/value/aliasOf", "room", "floor")) };
        var children = asset.Children.Select(c => c switch
        {
            MapPrefabRecordChild { Value: MapSpaceDoc { Id: "inner-room" } space } child => child with
            { Value = space with { Parent = new("room-alias", floor) }, References = child.References.Select(s => s.Path == "/value/parent/id" ? s with { Target = s.Target with { Id = "room-alias" } } : s).ToArray() },
            MapPrefabRecordChild { Value: MapPrismDomain { Id: "inner-room-domain" } prism } child => child with
            { Value = prism with { Parent = new("room-alias", floor) }, References = child.References.Select(s => s.Path == "/value/parent/id" ? s with { Target = s.Target with { Id = "room-alias" } } : s).ToArray() },
            _ => c,
        }).Append(alias).ToArray();
        return Reasset(f, asset with { Children = children });
    }

    internal static PrefabFixture AdjacentInstances(float scale, bool far)
    {
        var f = PlacedPrisms("dry-flooded", scale, far);
        var a = f.Document.Prefabs!.Instances.Single();
        var domain = (MapPrefabRecordChild)f.Assets.GetPrefab("hut").Children.Single(c => c.Key.Key == "room-domain");
        var replacement = domain with { Value = ((MapPrismDomain)domain.Value) with { Footprint = Domain(4, 0, 8, 4) } };
        long numeric = f.Document.NumericIdHighWaterMark;
        var b = a with
        {
            Id = "b",
            Bindings = new(a.Bindings.Entries.Select(binding => binding with
            { WorldId = "b/" + binding.Child.Key, NumericId = binding.NumericId is null ? null : ++numeric }).ToArray()),
            Overrides = new MapPrefabOverride[] { new MapPrefabReplaceOverride(domain.Key, replacement) }
        };
        f.Document.NumericIdHighWaterMark = numeric;
        f.Document.Prefabs = new(1, 1, new[] { a, b }, []);
        return f;
    }
    internal static PrefabFixture ReloadPrisms(PrefabFixture f) => new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets);

    internal static void PadParent(PrefabFixture f)
    {
        var anchor = new MapPatchKey("exterior", 0, 0);
        var source = f.Document.Surfaces.Patches[anchor];
        foreach (var (x, z) in new[] { (-1L, 0L), (0L, -1L), (-1L, -1L) })
        {
            var patch = Patch("exterior", 16, 16, source.Heights[0]);
            patch.Key = new("exterior", x, z);
            patch.CellMinX = x < 0 ? 48 : 0; patch.CellMinZ = z < 0 ? 48 : 0;
            int dx = source.Heights[1] - source.Heights[0], dz = source.Heights[source.Width + 1] - source.Heights[0];
            for (int row = 0; row <= patch.Depth; row++)
                for (int col = 0; col <= patch.Width; col++)
                    patch.Heights[row * (patch.Width + 1) + col] = checked(source.Heights[0] +
                        (int)(x * 64 + patch.CellMinX + col) * dx + (int)(z * 64 + patch.CellMinZ + row) * dz);
            patch.Records.Add(new MapSpaceFootprint($"exterior-domain-{x}-{z}", new("exterior", anchor), patch.Key,
                Enumerable.Range(patch.CellMinZ, 16).SelectMany(row => Enumerable.Range(patch.CellMinX, 16).Select(col => row * 64 + col)).ToArray(),
                new(MapBoundKind.SupportFloor, "exterior", null), new(MapBoundKind.OpenTop, null, null)));
            f.Document.Surfaces.Patches.Add(patch.Key, patch);
        }
        var surface = f.Document.Surfaces.Refs.Single();
        f.Document.Surfaces.Refs[0] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            f.Document.Surfaces.Patches.Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
        };
    }

    internal static PrefabFixture StandalonePrism()
    {
        var f = Create();
        var r = Effective(f);
        var doc = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document));
        doc.Prefabs = null;
        doc.Surfaces.Refs.Clear(); doc.Surfaces.Patches.Clear();
        var effective = r.Surfaces;
        doc.Surfaces.Refs.AddRange(effective.Refs);
        foreach (var entry in effective.Patches) doc.Surfaces.Patches.Add(entry.Key, entry.Value);
        doc.BuildingIntent = new(1, [], [], [], []) { Storeys = new[] { new MapStoreyDoc(1, "a/lower", "Lower", null) } };
        foreach (var patch in doc.Surfaces.Patches.Values)
            for (int i = 0; i < patch.Records.Count; i++)
                if (patch.Records[i] is MapPrismDomain prism) patch.Records[i] = prism with { InstanceId = "" };
        return new(doc, f.Assets);
    }

    static PrefabFixture StackedPrisms()
    {
        var f = Create("roomless");
        f.Document.Prefabs = null;
        f.Document.Placements.Clear();
        f.Document.Surfaces.Refs.Clear(); f.Document.Surfaces.Patches.Clear();
        f.Document.BuildingIntent = new(1, [], [], [], [])
        { Storeys = new[] { new MapStoreyDoc(1, "deep-storey", "Deep", null), new MapStoreyDoc(1, "high-storey", "High", null) } };
        AddCave("deep", -6000, -58, -52);
        AddCave("high", -4000, -38, -32);
        foreach (var surface in f.Document.Surfaces.Refs.ToArray())
        {
            int index = f.Document.Surfaces.Refs.FindIndex(s => s.Id == surface.Id);
            f.Document.Surfaces.Refs[index] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                f.Document.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id).Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
        return f;

        void AddCave(string id, int height, float lower, float upper)
        {
            var floor = Patch(id + "-floor", 4, 4, height);
            var ceiling = Patch(id + "-ceiling", 4, 4, height + 1000);
            for (int z = 0; z <= 4; z++)
                for (int x = 0; x <= 4; x++) { floor.Heights[z * 5 + x] += x * 25; ceiling.Heights[z * 5 + x] += x * 25; }
            f.Document.Surfaces.Refs.Add(Surface(floor.Key.SurfaceId, MapSurfaceRole.SupportFloor));
            f.Document.Surfaces.Refs.Add(Surface(ceiling.Key.SurfaceId, MapSurfaceRole.Ceiling));
            f.Document.Surfaces.Patches.Add(floor.Key, floor); f.Document.Surfaces.Patches.Add(ceiling.Key, ceiling);
            var walls = new List<MapBoundaryRef>();
            foreach (var (edge, x0, z0, x1, z1) in new[] { ("west", 0L, 0L, 0L, 4L), ("south", 0L, 4L, 4L, 4L),
                ("east", 4L, 4L, 4L, 0L), ("north", 4L, 0L, 0L, 0L) })
            {
                string wall = id + "-" + edge;
                foreach (var (suffix, surface, h) in new[] { ("lo", floor.Key.SurfaceId, height), ("hi", ceiling.Key.SurfaceId, height + 1000) })
                    floor.Records.Add(new MapBoundaryChain(wall + "-" + suffix, MapChainKind.Authored, null,
                        new[] { new MapChainVertex(new(surface, MapLatticeAddress.Corner(x0, z0)), h + (int)x0 * 25),
                            new MapChainVertex(new(surface, MapLatticeAddress.Corner(x1, z1)), h + (int)x1 * 25) }));
                floor.Records.Add(new MapWallStrip(wall, new(wall + "-lo", floor.Key), new(wall + "-hi", floor.Key), MapStripFacing.Front, 0));
                walls.Add(new(new(wall, floor.Key), MapSide.Front));
            }
            floor.Records.Add(new MapSpaceDoc(id + "-cave", MapSpaceKind.Cave, null, null, [], walls, [], []));
            floor.Records.Add(new MapSpaceFootprint(id + "-domain", new(id + "-cave", floor.Key), floor.Key,
                Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 4).Select(x => z * 64 + x)).ToArray(),
                new(MapBoundKind.SupportFloor, floor.Key.SurfaceId, null), new(MapBoundKind.Ceiling, ceiling.Key.SurfaceId, null)));
            floor.Records.Add(new MapSpaceDoc(id + "-room", MapSpaceKind.Room, new(id + "-cave", floor.Key), null, [], [], [], []));
            floor.Records.Add(new MapPrismDomain(id + "-prism", 1, new(id + "-room", floor.Key), new(id + "-cave", floor.Key), "", id + "-storey", Domain(1, 1, 3, 3), lower, upper));
        }
    }
}
