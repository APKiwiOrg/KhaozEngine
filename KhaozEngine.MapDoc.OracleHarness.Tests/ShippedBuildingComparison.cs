using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using static KhaozEngine.Tests.MapDocOracle.ShippedBuildingInputs;
using Coordinates = KhaozEngine.Tests.MapDocOracle.ShippedBuildingGeometry.Point;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record ShippedBuildingReport(IReadOnlyList<string> Failures, string DetailJson, IReadOnlyList<OracleComparisonFailure> ClassifiedFailures);

/// <summary>Assertion-free comparison. All source-derived evidence is returned to the secure entry boundary.</summary>
internal static partial class ShippedBuildingComparison
{
    const double VertexTolerance = 0.00001;
    const double NormalTolerance = 0.000001;
    static MapWorldBuildOptions Options() => new(new("private-building-oracle", 1, "building-comparison/1", 2), "building-comparison/1", new());

    internal static ShippedBuildingReport Compare(ShippedBuildingInputs inputs)
    {
        var evidence = new Evidence();
        var comparison = inputs.Comparison;
        var leaves = Array(comparison, "leaves");
        var sourceObjects = inputs.World.AllObjects().ToDictionary(o => o.Id);
        MapDocument converted = Convert(inputs, evidence);
        string text = MapDocumentFile.SaveText(converted);
        var first = MapWorldBuilder.Build(MapDocumentFile.LoadText(text), inputs.ReloadAssets(), Options());
        var second = MapWorldBuilder.Build(MapDocumentFile.LoadText(text), inputs.ReloadAssets(), Options());
        var reloaded = MapWorldBuilder.Build(MapDocumentFile.LoadText(MapDocumentFile.SaveText(MapDocumentFile.LoadText(text))), inputs.ReloadAssets(), Options());
        CompareHeads(first, second, evidence, "independent-heads");
        CompareHeads(first, reloaded, evidence, "reload");
        evidence.Record("new-parents", converted.Prefabs!.Instances.Select(i => new { i.Id, i.ResourceId, i.Transform, i.Unpacked }));
        foreach (var leaf in leaves)
        {
            long sourceId = leaf.GetProperty("sourceId").GetInt64();
            TileObject source = sourceObjects[sourceId];
            var resource = evidence.Resource(Array(comparison, "roofs").Any(r => r.GetProperty("leafId").GetInt64() == sourceId) ? "Roof" : "Placement", "placement");
            var native = first.Document.Placements.SingleOrDefault(p => p.NumericId == sourceId);
            resource.Check("numeric-leaf", native is not null && native.PlacementId == Text(leaf, "worldId"), new { sourceId, native });
            if (native is null) continue;
            var archetype = inputs.Catalogs.Archetype(source.ArchetypeId)!;
            var geometry = first.Placements.Single(p => p.PlacementId == native.PlacementId);
            var asset = inputs.Assets.GetAsset(native.AssetId);
            resource.Check("leaf-kind-asset", native.Kind == source.ArchetypeId && native.AssetId == Text(leaf, "assetId"), new { source, native.Kind, native.AssetId });
            resource.Check("ordered-tags", (source.Tags ?? []).SequenceEqual(native.Tags), new { sourceId, sourceTags = source.Tags, nativeTags = native.Tags });
            Vector3 position = TileObjectPlacement.AnchorPosition(inputs.World, archetype, source);
            float yaw = TileObjectPlacement.YawRadians(archetype, source.Rotation);
            var pose = geometry.EffectiveTransform;
            resource.Near("leaf-position", new[] { (double)position.X, position.Y, position.Z }, new[] { pose.X, pose.Y, pose.Z }, VertexTolerance, sourceId);
            // F-R9. Yaw is an angle. Scale stays a plain comparison.
            resource.Near("leaf-yaw-scale", new[] { 0d, 1d }, new[] { YawDifference(yaw, pose.YawRadians), pose.Scale }, NormalTolerance,
                new { sourceId, sourceYaw = yaw, nativeYaw = pose.YawRadians });
            resource.Check("original-mesh", inputs.Bytes(Text(leaf, "meshPath")).SequenceEqual(inputs.Assets.GetResource(asset.MeshResourceId).Bytes.ToArray()), new { sourceId, asset.MeshResourceId });
            var metadata = leaf.GetProperty("metadata");
            resource.Check("asset-metadata", asset.Source == Text(metadata, "source") && asset.License == Text(metadata, "license") &&
                asset.Category == NullableText(metadata, "category") && asset.Textured == metadata.GetProperty("textured").GetBoolean() &&
                asset.SourceUnitsToMetres == Number(metadata, "sourceUnitsToMetres") && BoundsEqual(asset.RenderBounds, metadata.GetProperty("renderBounds")) &&
                BoundsEqual(asset.LodBounds, metadata.GetProperty("lodBounds")) && BoundsEqual(asset.LightBounds, metadata.GetProperty("lightBounds")), new { sourceId, asset.Source, asset.License, asset.Category, asset.Textured, asset.SourceUnitsToMetres, expected = metadata });
            var legacyMatrix = TileObjectPlacement.LocalToWorld(inputs.World, archetype, source);
            var sourceColliders = TileWorldColliders.Build(SourceView(inputs.World, new[] { sourceId }), inputs.Catalogs, inputs.CollisionOptions)
                .Colliders.Where(c => c.Kind is TileColliderKind.Wall or TileColliderKind.Object or TileColliderKind.WalkSurface).ToArray();
            var expected = sourceColliders.SelectMany(c => ShippedBuildingGeometry.Capture(c.Shape, p => Transform(c.Pose, p), n => Vector3.Transform(n, c.Pose.Orientation))).ToArray();
            var actual = geometry.Collider is null ? [] : ShippedBuildingGeometry.Capture(geometry.Collider,
                p => Precise(pose, p), n => new MapResourceTransform(pose).Normal(n)).ToArray();
            ShippedBuildingGeometry.Compare("physical-collider", expected, actual, sourceId, resource.Check, resource.Near);
            var sourceSelectionPath = NullableText(leaf, "selectionPath");
            var shapes = MapAssetShapes.Read(inputs.Assets, native.AssetId);
            float units = Number(metadata, "sourceUnitsToMetres");
            if (sourceSelectionPath is null && expected.Length != 0)
                resource.Check("authored-selection", shapes.Selection is null, new { sourceId, shapes.SelectionSha256 });
            else if (sourceSelectionPath is null)
            {
                // F-R10. No source collider and no authored selection: the source picked this leaf by its model box, so
                // the native selection is exactly one box spanning the attested renderBounds in source units.
                var bounds = metadata.GetProperty("renderBounds");
                Vector3 min = Vector(bounds.GetProperty("min")), max = Vector(bounds.GetProperty("max"));
                var derived = new CompoundShape(new[] { new CompoundChild(new BoxShape((max - min) * 0.5f), new Pose((min + max) * 0.5f, Quaternion.Identity)) });
                var derivedExpected = ShippedBuildingGeometry.Capture(derived, p => Vector3.Transform(p * units, legacyMatrix),
                    n => Vector3.TransformNormal(n, legacyMatrix)).ToArray();
                var derivedActual = shapes.Selection is null ? [] : ShippedBuildingGeometry.Capture(shapes.Selection,
                    p => Precise(pose, p * units * (float)pose.Scale), n => new MapResourceTransform(pose).Normal(n)).ToArray();
                var derivedFailures = new List<string>();
                ShippedBuildingGeometry.Compare("derived-selection", derivedExpected, derivedActual, sourceId,
                    (category, ok, detail) => { if (!ok) derivedFailures.Add(category); resource.Check(category, ok, detail); }, resource.Near);
                resource.Check("derived-selection", shapes.Selection is not null && derivedFailures.Count == 0,
                    new { sourceId, min, max, units, shapes.SelectionSha256, derivedFailures });
            }
            else
            {
                byte[] original = inputs.Bytes(sourceSelectionPath);
                resource.Check("selection-bytes", asset.SelectionResourceId is not null &&
                    original.SequenceEqual(inputs.Assets.GetResource(asset.SelectionResourceId).Bytes.ToArray()), new { sourceId, sourceSelectionPath, asset.SelectionResourceId });
                using var stream = new MemoryStream(original);
                var sourceShape = PropCollisionFormat.Read(stream);
                var selectionExpected = ShippedBuildingGeometry.Capture(sourceShape, p => Vector3.Transform(p * units, legacyMatrix),
                    n => Vector3.TransformNormal(n, legacyMatrix)).ToArray();
                var selectionActual = shapes.Selection is null ? [] : ShippedBuildingGeometry.Capture(shapes.Selection,
                    p => Precise(pose, p * units * (float)pose.Scale), n => new MapResourceTransform(pose).Normal(n)).ToArray();
                ShippedBuildingGeometry.Compare("authored-selection", selectionExpected, selectionActual, sourceId, resource.Check, resource.Near);
            }
            evidence.Record("source-query-delta", ShippedBuildingPicking.Compare(inputs, leaf, source, archetype, geometry));
        }
        evidence.Check("original-leaves-once", first.Document.Placements.Count == leaves.Length &&
            first.Document.Placements.Select(p => p.NumericId).Order().SequenceEqual(leaves.Select(l => (long?)l.GetProperty("sourceId").GetInt64()).Order()),
            first.Document.Placements.Select(p => new { p.PlacementId, p.NumericId }));
        CompareFloorPaint(inputs, first, evidence);
        CompareDefinitions(inputs, evidence);
        CompareRoomsAndRoofs(inputs, first, second, evidence);
        CompareLights(inputs, first, evidence);
        CompareDoorways(inputs, first, evidence);
        CompareStandaloneRoofs(inputs, first, evidence);
        CompareCounts(inputs, first, evidence);
        return evidence.Finish();
    }

    // The attested native scaffold supplies the authored semantic conversion. Floors are regenerated by R2,
    // preserving its semantic records. Geometry parity is checked before replacing any native floor bytes.
    static MapDocument Convert(ShippedBuildingInputs inputs, Evidence evidence)
    {
        var terrain = evidence.Resource("Terrain", "partition");
        var document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(inputs.NativeDocument));
        var floorGroups = Array(inputs.Comparison, "floors").GroupBy(f => (Region: RegionCoord.Of(f.GetProperty("x").GetInt32(), f.GetProperty("z").GetInt32()), Plane: f.GetProperty("plane").GetInt32()));
        foreach (var group in floorGroups)
        {
            var (surface, patch) = LegacyOracleConverter.ToNative(inputs.World, group.Key.Region, group.Key.Plane);
            if (!document.Surfaces.Patches.TryGetValue(patch.Key, out var candidate))
            { terrain.Check("missing-native-floor", false, new { patch.Key }); continue; }
            var candidateSurface = document.Surfaces.Refs.Single(s => s.Id == surface.Id);
            var compiled = MapSurfaceCompiler.Compile(surface, patch);
            var candidateCompiled = MapSurfaceCompiler.Compile(candidateSurface, candidate);
            CompareGeometry("floor-vertices-normals", compiled.ExactVertices.Select(v => new Sample(false, v)).Concat(compiled.Faces.Select(f => new Sample(true, f.Normal))).ToArray(),
                candidateCompiled.ExactVertices.Select(v => new Sample(false, v)).Concat(candidateCompiled.Faces.Select(f => new Sample(true, f.Normal))).ToArray(), terrain, patch.Key);
            var legacy = TileGroundTriangles.Build(inputs.World, group.Key.Region, group.Key.Plane);
            terrain.Check("source-floor-face-count", legacy.Indices.Length / 3 == compiled.Faces.Count, new { patch.Key });
            for (int i = 0; i < Math.Min(legacy.Indices.Length / 3, compiled.Faces.Count); i++)
            {
                var face = compiled.Faces[i];
                var vertices = new[] { face.A, face.B, face.C };
                for (int v = 0; v < 3; v++)
                {
                    var old = legacy.Positions[legacy.Indices[i * 3 + v]];
                    var expected = new[] { group.Key.Region.OriginX + (double)old.X, old.Y, -group.Key.Region.OriginZ + (double)old.Z };
                    terrain.Near("source-floor-vertex", expected, Components(compiled.ExactVertices[vertices[v]]), VertexTolerance, new { patch.Key, face = i, vertex = v });
                }
                var normal = LegacyOracleConverter.LegacyNormal(legacy.Positions[legacy.Indices[i * 3]], legacy.Positions[legacy.Indices[i * 3 + 1]], legacy.Positions[legacy.Indices[i * 3 + 2]]);
                terrain.Near("source-floor-normal", Components(normal), Components(face.Normal), NormalTolerance, new { patch.Key, face = i });
            }
            terrain.Check("floor-integers", LegacyOracleConverter.CellBytes(patch).SequenceEqual(LegacyOracleConverter.CellBytes(candidate)) &&
                patch.Heights.SequenceEqual(candidate.Heights) && patch.Presence.SequenceEqual(candidate.Presence), new { patch.Key });
            foreach (var row in group)
            {
                int x = row.GetProperty("x").GetInt32(), z = row.GetProperty("z").GetInt32();
                int slot = (z - group.Key.Region.OriginZ) * TileRegion.Size + x - group.Key.Region.OriginX;
                var cell = patch.Cells[slot];
                terrain.Check("floor-material", cell.Underlay == row.GetProperty("underlay").GetUInt16() && cell.Overlay == row.GetProperty("overlay").GetUInt16(), new { row, cell });

            }
            patch.Records.AddRange(candidate.Records);
            document.Surfaces.Patches[patch.Key] = patch;
            var refs = document.Surfaces.Refs;
            int index = refs.FindIndex(s => s.Id == surface.Id);
            var digests = document.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)));
            refs[index] = surface with { SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, digests) };
        }
        return document;
    }

    static void CompareHeads(MapBuiltWorld a, MapBuiltWorld b, Evidence evidence, string category)
    {
        evidence.Check(category, a.BuildHash == b.BuildHash && a.AnnotationDigest == b.AnnotationDigest &&
            a.Document.EffectiveResources!.Digest == b.Document.EffectiveResources!.Digest &&
            a.Placements.Select(p => (p.PlacementId, p.NumericId, p.Digest, p.EffectiveTransform, p.Envelope.Bounds, p.Envelope.RaiseMetres)).SequenceEqual(
                b.Placements.Select(p => (p.PlacementId, p.NumericId, p.Digest, p.EffectiveTransform, p.Envelope.Bounds, p.Envelope.RaiseMetres))) &&
            a.Document.Placements.Select(LeafRecord).SequenceEqual(b.Document.Placements.Select(LeafRecord)) &&
            a.Statics.Select(s => (s.OwnerId, s.Digest, s.Bounds)).SequenceEqual(b.Statics.Select(s => (s.OwnerId, s.Digest, s.Bounds))) &&
            a.Lights.SequenceEqual(b.Lights), new { a.BuildHash, secondBuildHash = b.BuildHash });
        evidence.Record(category, new { a.BuildHash, leaves = a.Placements.Count, statics = a.Statics.Count });
    }

    static string LeafRecord(MapResolvedPlacement leaf) => JsonSerializer.Serialize(new
    { leaf.PlacementId, leaf.NumericId, leaf.AssetId, leaf.Kind, leaf.Tags, leaf.Transform });

    static bool BoundsEqual(MapLocalBounds? actual, JsonElement expected) => expected.ValueKind == JsonValueKind.Null
        ? actual is null : actual is { } value && value.Min == Vector(expected.GetProperty("min")) && value.Max == Vector(expected.GetProperty("max"));

    static void CompareFloorPaint(ShippedBuildingInputs inputs, MapBuiltWorld world, Evidence evidence)
    {
        var resource = evidence.Resource("Terrain", "partition");
        var resources = world.Document.EffectiveResources!;
        foreach (var floor in Array(inputs.Comparison, "floors"))
        {
            int x = floor.GetProperty("x").GetInt32(), z = floor.GetProperty("z").GetInt32();
            var region = RegionCoord.Of(x, z);
            string surfaceId = "plane-" + floor.GetProperty("plane").GetInt32();
            var patch = new MapPatchKey(surfaceId, region.Rx, region.Rz);
            int slot = (z - region.OriginZ) * TileRegion.Size + x - region.OriginX;
            var paints = resources.PaintRegions.TryGetValue(surfaceId, out var compiled) ? compiled.Regions.Where(p => p.Face.Patch == patch && p.Face.Primitive == slot).ToArray() : [];
            string? id = NullableText(floor, "paintId");
            resource.Check("floor-paint", id is null ? paints.Length == 0 : paints.Length != 0 && paints.All(p => p.PaintId == id &&
                p.Material.Underlay == floor.GetProperty("underlay").GetUInt16() && p.Material.Overlay == floor.GetProperty("overlay").GetUInt16()), new { floor, paints });
            evidence.Record("floor-material-and-notch", new { floor, paints });
        }
    }

    static void CompareDefinitions(ShippedBuildingInputs inputs, Evidence evidence)
    {
        var definitions = Array(inputs.Comparison, "definitions").ToDictionary(d => Text(d, "id"));
        var leaves = Array(inputs.Comparison, "leaves");
        var objects = inputs.World.AllObjects().ToDictionary(o => o.Id);
        foreach (var building in Array(inputs.Comparison, "buildings"))
        {
            var definition = definitions[Text(building, "definitionId")];
            var sourcePrefab = inputs.Prefabs[Text(definition, "id")];
            var keys = Array(definition, "keys").ToDictionary(k => Text(k, "key"));
            foreach (var leaf in leaves.Where(l => BuildingOf(l) == Text(building, "id")))
            {
                var actual = objects[leaf.GetProperty("sourceId").GetInt64()];
                var resource = evidence.Resource(Array(inputs.Comparison, "roofs").Any(r => r.GetProperty("leafId").GetInt64() == actual.Id) ? "Roof" : "Placement", "placement");
                bool addition = !keys.TryGetValue(Text(leaf, "key"), out var key);
                var nominal = addition ? null : sourcePrefab.Objects[key.GetProperty("objectIndex").GetInt32()];
                bool furnitureDifference = nominal is null || nominal.ArchetypeId != actual.ArchetypeId ||
                    !(nominal.Tags ?? []).SequenceEqual(actual.Tags ?? []);
                bool namedOverride = Array(building, "overrides").Any(o => Text(o, "key") == Text(leaf, "key") && Text(o, "kind") == "Placement");
                resource.Check("actual-furniture-override", !furnitureDifference || namedOverride, new { actual, nominal, namedOverride });
                evidence.Record("actual-instance-membership", new { building = Text(building, "id"), leaf, nominal, furnitureDifference });
            }
        }
    }

    static void CompareRoomsAndRoofs(ShippedBuildingInputs inputs, MapBuiltWorld world, MapBuiltWorld other, Evidence evidence)
    {
        var roomsCheck = evidence.Resource("Space", "room");
        var roofsCheck = evidence.Resource("Roof", "placement");
        var groupsCheck = evidence.Resource("Roof", "group");
        var membershipCheck = evidence.Resource("Space", "membership");
        var resources = world.Document.EffectiveResources!;
        var intent = resources.BuildingIntent;
        var rooms = Array(inputs.Comparison, "rooms");
        var domains = resources.Surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapPrismDomain>().ToArray();
        evidence.Check("room-inventory", rooms.Select(r => Text(r, "id")).Order().SequenceEqual(domains.Select(d => d.Space.Id).Order()),
            domains.Select(d => new { d.Space, d.InstanceId, d.StoreyId }));
        var membership = new MapSpaceMembership(world.Surfaces);
        foreach (var room in rooms)
        {
            string roomId = Text(room, "id");
            var domain = domains.SingleOrDefault(d => d.Space.Id == roomId);
            roomsCheck.Check("room-storey-owner", domain is not null && domain.InstanceId == Text(room, "buildingId") && domain.StoreyId == Text(room, "storeyId"), new { room, domain });
            foreach (var cell in Array(room, "cells"))
            {
                var key = CellKey(cell);
                Vector3 centre = new((key.X + 0.5f) * inputs.World.TileSize, key.Y, -(key.Z + 0.5f) * inputs.World.TileSize);
                var actual = membership.Query(Frame(world.Surfaces, centre));
                roomsCheck.Check("room-cell-centre", actual.Status == MapMembershipStatus.Resolved && actual.SpaceId == roomId,
                    new { cell, roomId, actual.Status, actual.SpaceId, actual.DomainKeys });
            }
        }
        var groups = Array(inputs.Comparison, "groups");
        evidence.Check("concealment-groups", groups.Length == intent.RoofGroups.Count, new { expected = groups, actual = intent.RoofGroups });
        foreach (var group in groups)
        {
            var actual = intent.RoofGroups.SingleOrDefault(g => g.Id == Text(group, "id"));
            groupsCheck.Check("explicit-concealment-group", actual is not null && Strings(group, "rooms").Order().SequenceEqual(actual.Rooms.Select(r => r.Id).Order()), new { group, actual });
        }
        var leaves = Array(inputs.Comparison, "leaves").ToDictionary(l => l.GetProperty("sourceId").GetInt64());
        var roofs = Array(inputs.Comparison, "roofs");
        evidence.Check("roof-inventory", roofs.Select(r => Text(leaves[r.GetProperty("leafId").GetInt64()], "worldId")).Order().SequenceEqual(intent.Roofs.Select(r => r.Id).Order()), new { expected = roofs, actual = intent.Roofs });
        foreach (var roof in roofs)
        {
            string? linkId = NullableText(roof, "linkId");
            string worldId = Text(leaves[roof.GetProperty("leafId").GetInt64()], "worldId");
            var links = intent.RoofLinks.Where(l => l.Roof.Id == worldId).ToArray();
            roofsCheck.Check("roof-links", linkId is null ? links.Length == 0 : links.Length == 1 && links[0].Id == linkId &&
                links[0].AboveY == Number(roof, "aboveY") && Strings(roof, "rooms").Order().SequenceEqual(links[0].Rooms.Select(r => r.Id).Order()) &&
                Strings(roof, "groups").Order().SequenceEqual(links[0].Groups.Select(r => r.Id).Order()), new { roof, links });
        }
        foreach (var probe in Array(inputs.Comparison, "probes"))
        {
            var position = Vector(probe.GetProperty("position"));
            var point = Frame(world.Surfaces, position);
            var actual = membership.Query(point);
            var second = new MapSpaceMembership(other.Surfaces).Query(Frame(other.Surfaces, position));
            string? expected = NullableText(probe, "spaceId"), source = NullableText(probe, "sourceSpaceId");
            membershipCheck.Check("membership-" + Text(probe, "kind"), actual.SpaceId == expected &&
                actual.Status == (expected is null ? MapMembershipStatus.Outside : MapMembershipStatus.Resolved), new { probe, actual.Status, actual.SpaceId, actual.DomainKeys });
            if (source != expected)
            {
                bool boundary = Text(probe, "kind") == "boundary";
                membershipCheck.Check("unexplained-membership-difference", boundary, new { probe, actual.SpaceId });
                evidence.Record("boundary-change-awaits-r11", new { probe, oneOwnerPolicy = true, accepted = false });
            }
            membershipCheck.Check("head-membership", actual.Status == second.Status && actual.SpaceId == second.SpaceId &&
                actual.DomainKeys.SequenceEqual(second.DomainKeys) && actual.ParentSpaceIds.SequenceEqual(second.ParentSpaceIds), new { probe });
            var hidden = Ids(probe, "hiddenRoofIds").Select(id => Text(leaves[id], "worldId")).ToHashSet(StringComparer.Ordinal);
            var statics = world.Statics.Select(s => (s.OwnerId, s.Digest, s.Bounds, s.Position, s.Orientation)).ToArray();
            foreach (MapRoofMode mode in Enum.GetValues<MapRoofMode>())
            {
                var policy = MapRoofPolicy.Evaluate(resources, world.Surfaces, point, mode);
                foreach (var roof in policy.Roofs)
                {
                    bool visible = mode == MapRoofMode.AlwaysVisible || mode == MapRoofMode.Auto && !hidden.Contains(roof.RoofPlacementId);
                    roofsCheck.Check("roof-policy", roof.ColourVisible == visible && roof.CastShadows && roof.CameraBoomBlocks == visible && roof.SkyPickingBlocks,
                        new { probe, mode, roof, visible });
                }
                evidence.Check("roof-statics-unchanged", statics.SequenceEqual(world.Statics.Select(s => (s.OwnerId, s.Digest, s.Bounds, s.Position, s.Orientation))), new { probe, mode });
            }
        }
    }

    static void CompareLights(ShippedBuildingInputs inputs, MapBuiltWorld world, Evidence evidence)
    {
        var resource = evidence.Resource("Light", "light");
        // This public facade consumes the engine's shared LightFrame expansion. No copied owner-frame policy.
        var lights = MapEffectiveAnnotations.Lights(world.Document.EffectiveResources!);
        var expected = Array(inputs.Comparison, "lights");
        evidence.Check("all-light-inventory", expected.Length == lights.Count, new { expected, actual = lights });
        var leaves = Array(inputs.Comparison, "leaves").ToDictionary(l => l.GetProperty("sourceId").GetInt64());
        foreach (var row in expected)
        {
            var light = lights.SingleOrDefault(l => l.Id == Text(row, "id"));
            string owner = Text(leaves[row.GetProperty("ownerLeafId").GetInt64()], "worldId");
            resource.Check("light-owner", light is not null && light.OwnerPlacementId == owner && world.Placements.Any(p => p.PlacementId == owner), new { row, light, owner });
            if (light is null) continue;
            resource.Near("light-position", Components(Vector(row.GetProperty("position"))), Components(light.Position), VertexTolerance, row);
            resource.Near("light-colour", Components(Vector(row.GetProperty("colour"))), Components(light.Colour), NormalTolerance, row);
            resource.Check("light-properties", light.Intensity == Number(row, "intensity") && light.Radius == Number(row, "radius") &&
                light.CastShadows == row.GetProperty("castShadows").GetBoolean(), new { row, light });
            evidence.Record(row.GetProperty("dedicated").GetBoolean() ? "dedicated-light" : "emitting-prop", new { row, light });
        }
    }

    static void CompareDoorways(ShippedBuildingInputs inputs, MapBuiltWorld world, Evidence evidence)
    {
        var resource = evidence.Resource("Placement", "doorway");
        using var native = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, native);
        var leaves = Array(inputs.Comparison, "leaves").ToDictionary(l => l.GetProperty("sourceId").GetInt64());
        foreach (var door in Array(inputs.Comparison, "doorways"))
        {
            var ids = Ids(door, "leafIds");
            var shells = ids.Select(id => world.Placements.Single(p => p.PlacementId == Text(leaves[id], "worldId"))).ToArray();
            resource.Check("native-compound-door-shell", shells.Any(p => p.Collider is CompoundShape), new { door, shellIds = shells.Select(p => p.PlacementId) });
            using var source = new BepuPhysicsWorld();
            var colliders = TileWorldColliders.Build(SourceView(inputs.World, ids), inputs.Catalogs, inputs.CollisionOptions);
            foreach (var c in colliders.Colliders.Where(c => c.Kind is TileColliderKind.Wall or TileColliderKind.Object or TileColliderKind.WalkSurface)) source.AddStatic(c.Shape, c.Pose);
            var capsule = new CapsuleShape(Number(door, "radius"), Number(door, "length"));
            foreach (string name in new[] { "clear", "jamb" })
            {
                var points = Array(door, name).Select(Vector).ToArray();
                bool oldClear = Clear(source, capsule, points), newClear = Clear(native, capsule, points);
                resource.Check("doorway-" + name, oldClear == (name == "clear") && newClear == oldClear, new { door, oldClear, newClear, proof = "physical aperture only" });
            }
        }
    }

    static bool Clear(IPhysicsWorld physics, CapsuleShape capsule, Vector3[] feet)
    {
        Vector3 lift = new(0, capsule.Radius + capsule.Length * 0.5f, 0);
        for (int i = 0; i < feet.Length; i++)
        {
            var pose = new Pose(feet[i] + lift - physics.Origin, Quaternion.Identity);
            if (physics.ComputePenetration(capsule, pose, out _)) return false;
            if (i + 1 == feet.Length) continue;
            Vector3 delta = feet[i + 1] - feet[i];
            float length = delta.Length();
            if (length > 0 && physics.SweepCapsule(capsule, pose, delta / length, length, out _)) return false;
        }
        return true;
    }

    // F-R6. Transform, mesh, collider and numeric parity run in the shared leaf loop. Here the standalone roof must stay
    // outside every instance, remain in the unlinked World roof inventory and stay visible under Auto at its own site.
    static void CompareStandaloneRoofs(ShippedBuildingInputs inputs, MapBuiltWorld world, Evidence evidence)
    {
        var resource = evidence.Resource("Roof", "placement");
        var resources = world.Document.EffectiveResources!;
        var intent = resources.BuildingIntent;
        var objects = inputs.World.AllObjects().ToDictionary(o => o.Id);
        foreach (var leaf in Array(inputs.Comparison, "leaves").Where(l => BuildingOf(l) is null))
        {
            string worldId = Text(leaf, "worldId");
            var source = objects[leaf.GetProperty("sourceId").GetInt64()];
            var native = world.Document.Placements.SingleOrDefault(p => p.PlacementId == worldId);
            resource.Check("standalone-roof-placement", native is not null && native.NumericId == source.Id &&
                inputs.NativeDocument.Placements.Any(p => p.Id == worldId) &&
                inputs.NativeDocument.Prefabs!.Instances.All(i => i.Bindings.Entries.All(b => b.WorldId != worldId)), new { worldId, native });
            resource.Check("standalone-roof-inventory", intent.Roofs.Contains(new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, worldId)) &&
                intent.RoofLinks.All(l => l.Roof.Id != worldId), new { worldId, intent.Roofs });
            Vector3 site = TileObjectPlacement.AnchorPosition(inputs.World, inputs.Catalogs.Archetype(source.ArchetypeId)!, source);
            var auto = MapRoofPolicy.Evaluate(resources, world.Surfaces, Frame(world.Surfaces, site + new Vector3(0, 0.5f, 0)), MapRoofMode.Auto).Roofs
                .SingleOrDefault(r => r.RoofPlacementId == worldId);
            resource.Check("standalone-roof-auto-visible", auto is not null && auto.ColourVisible && auto.CameraBoomBlocks, new { worldId, auto });
        }
    }

    static void CompareCounts(ShippedBuildingInputs inputs, MapBuiltWorld world, Evidence evidence)
    {
        var c = inputs.Comparison;
        var actual = new Dictionary<string, int>
        {
            ["definitions"] = inputs.Prefabs.Count,
            ["buildings"] = inputs.NativeDocument.Prefabs!.Instances.Count,
            ["originalLeaves"] = world.Document.Placements.Count,
            ["connectedInteriors"] = ConcealmentComponents(c),
            ["roofs"] = world.Document.EffectiveResources!.BuildingIntent.Roofs.Count,
            ["openings"] = Array(c, "doorways").Length,
            ["doorwayRecords"] = Array(c, "doorwayRecords").Length,
            ["dedicatedLights"] = Array(c, "lights").Count(l => l.GetProperty("dedicated").GetBoolean() && world.Lights.Any(w => w.Id == Text(l, "id"))),
        };
        foreach (var expectation in c.GetProperty("expectations").EnumerateObject())
            evidence.Check("expectation-" + expectation.Name.ToLowerInvariant(), actual[expectation.Name] == expectation.Value.GetInt32(), new { name = expectation.Name, expected = expectation.Value, actual = actual[expectation.Name] });
        evidence.Record("inventory-counts", actual);
    }

    static int ConcealmentComponents(JsonElement c)
    {
        var parents = Array(c, "rooms").ToDictionary(r => Text(r, "id"), r => Text(r, "id"));
        string Root(string id) { while (parents[id] != id) id = parents[id]; return id; }
        foreach (var group in Array(c, "groups"))
        {
            var rooms = Strings(group, "rooms");
            foreach (var room in rooms.Skip(1)) parents[Root(room)] = Root(rooms[0]);
        }
        return parents.Keys.Select(Root).Distinct().Count();
    }

    static TileWorldDocument SourceView(TileWorldDocument original, long[] ids)
    {
        var view = new TileWorldDocument { Id = original.Id, TileSize = original.TileSize, PlaneCount = original.PlaneCount, PlaneHeight = original.PlaneHeight };
        var selected = ids.ToHashSet();
        foreach (var region in original.Regions.Values)
        {
            var copy = view.GetOrCreateRegion(region.Coord);
            for (int plane = 0; plane < original.PlaneCount; plane++) copy.Planes[plane] = region.Planes[plane];
            copy.Objects.AddRange(region.Objects.Where(o => selected.Contains(o.Id)));
        }
        view.RebuildObjectIndex();
        return view;
    }

    readonly record struct Sample(bool Normal, Coordinates Value)
    {
        internal Sample(bool normal, Vector3 value) : this(normal, (Coordinates)value) { }
        internal Sample(bool normal, MapExactPoint value) : this(normal, new Coordinates(value.X.ToDouble(), value.Y.ToDouble(), value.Z.ToDouble())) { }
    }
    static void CompareGeometry(string category, Sample[] expected, Sample[] actual, OracleResourceChecks evidence, object owner)
    {
        double Error(Sample a, Sample b) => Math.Max(Math.Abs(a.Value.X - b.Value.X),
            Math.Max(Math.Abs(a.Value.Y - b.Value.Y), Math.Abs(a.Value.Z - b.Value.Z)));
        bool Near(Sample a, Sample b) => a.Normal == b.Normal &&
            double.IsFinite(Error(a, b)) && Error(a, b) <= (a.Normal ? NormalTolerance : VertexTolerance);
        evidence.Check(category + "-count", expected.Length == actual.Length, new { owner, expected = expected.Length, actual = actual.Length });
        if (expected.Length == actual.Length && expected.Select((sample, index) => Near(sample, actual[index])).All(value => value)) return;

        // Match occurrences bijectively within the fixed component tolerance. Exact sorting is unstable at rotations.
        var assigned = ShippedBuildingGeometry.Match(expected, actual, Near);
        var matched = assigned.Where(index => index >= 0).ToHashSet();
        for (int index = 0; index < expected.Length; index++)
        {
            if (matched.Contains(index)) continue;
            evidence.Check(category + "-correspondence", false, new { owner, index, expected = expected[index] });
            int closest = Enumerable.Range(0, actual.Length).Where(i => actual[i].Normal == expected[index].Normal)
                .OrderBy(i => Error(expected[index], actual[i])).FirstOrDefault(-1);
            if (closest >= 0) evidence.Near(category, Components(expected[index].Value), Components(actual[closest].Value),
                expected[index].Normal ? NormalTolerance : VertexTolerance, new { owner, index, actualIndex = closest, expected[index].Normal });
        }
        for (int index = 0; index < actual.Length; index++)
            if (assigned[index] < 0) evidence.Check(category + "-unexpected", false, new { owner, index, actual = actual[index] });
    }
    /// <summary>F-R9. Signed angular distance from source to native yaw, wrapped into one turn about zero.</summary>
    internal static double YawDifference(double source, double native) => Math.IEEERemainder(native - source, 2 * Math.PI);
    static Vector3 Transform(Pose pose, Vector3 p) => pose.Position + Vector3.Transform(p, pose.Orientation);
    static Coordinates Precise(MapEffectiveTransform pose, Vector3 p)
    { var (s, c) = Math.SinCos(pose.YawRadians); return new(pose.X + c * p.X + s * p.Z, pose.Y + p.Y, pose.Z - s * p.X + c * p.Z); }
    static double[] Components(MapExactPoint p) => new[] { p.X.ToDouble(), p.Y.ToDouble(), p.Z.ToDouble() };
    static double[] Components(Coordinates p) => new[] { p.X, p.Y, p.Z };
    static double[] Components(Vector3 p) => new[] { (double)p.X, p.Y, p.Z };
    // F-R14. Membership refuses a point outside the acquired view's frame, so every query point uses that frame.
    internal static MapFramePoint Frame(MapScopedSurfaces view, Vector3 p) => new(view.Scope.Frame, view.Scope.Frame.ToLocal(p));
    static string? NullableText(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(row, name);

}
