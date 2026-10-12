using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc.Physics;

internal static class PrefabPhysicsFixtures
{
    internal static MapWorldBuildOptions Options() => new(PrefabFixtures.Options(), "public-prefab-physics/1", new());
    internal static MapBuiltWorld Build(PrefabFixture fixture) => MapWorldBuilder.Build(fixture.Document, fixture.Assets, Options());

    internal static IReadOnlyList<MapFramePoint> DoorPath(MapBuiltWorld world, bool throughJamb)
    {
        var pose = world.Document.EffectiveResources!.Transforms["Placement:a/doorway"];
        return new[] { At(pose, throughJamb ? 0.8 : 0, 0.01, -1), At(pose, throughJamb ? 0.8 : 0, 0.01, 1) };
    }

    // Independent binary64 vertex transform, with framing before the binary32 query boundary.
    internal static MapFramePoint At(MapEffectiveTransform pose, double x, double y, double z)
    {
        var (s, c) = Math.SinCos(pose.YawRadians);
        double wx = pose.X + pose.Scale * (c * x + s * z);
        double wz = pose.Z + pose.Scale * (-s * x + c * z);
        var frame = WorldFrame.Nearest((float)wx, (float)wz);
        return new(frame, new((float)(wx - frame.Anchor.X), (float)(pose.Y + y * pose.Scale), (float)(wz - frame.Anchor.Z)));
    }

    internal static PrefabFixture TightBounds(PrefabFixture f)
    {
        var p = f.Document.Prefabs!.Instances.Single().Transform.Position;
        f.Document.TileSize = 64;
        f.Document.Bounds = new() { MinX = p.X - 64, MinZ = p.Z - 64, MaxX = p.X + 64, MaxZ = p.Z + 64 };
        f.Document.PlayableBounds = new() { MinX = f.Document.Bounds.MinX, MinZ = f.Document.Bounds.MinZ, MaxX = f.Document.Bounds.MaxX, MaxZ = f.Document.Bounds.MaxZ };
        return f;
    }

    internal static PrefabFixture CentimetreSupport(bool duplicateColliderFloor = false, KhaozEngine.Physics.PhysicsShape? colliderShape = null)
    {
        var f = PrefabFixtures.SupportAt(false);
        var resources = f.Assets.CapturedResources.ToDictionary(r => r.Reference.Id, r => r.Bytes.ToArray(), StringComparer.Ordinal);
        var set = new MapSurfaceSet();
        var patch = PrefabFixtures.Patch("floor", 1, 1, 0);
        set.Patches.Add(patch.Key, patch);
        // Each cell is one metre. The hundred source units are already represented by the frame's metre units.
        set.Refs.Add(PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor));
        resources["deck-support"] = MapSupportResourceFile.Write(new(1, set, null,
            PrefabFixtures.Slots(PrefabFixtures.Surf("/patches/0/key/surfaceId", "floor"))));
        var shape = NativeWorldFixtures.Compound((NativeWorldFixtures.Box(100, 100, 100), new(50, -50, -50)));
        // For edge agreement the box is below the floor with its top at -0.01 m. The duplicate variant puts it at 0.
        if (!duplicateColliderFloor) shape = NativeWorldFixtures.Compound((NativeWorldFixtures.Box(100, 100, 100), new(50, -51, -50)));
        var source = new NativeWorldAssetSource();
        var collider = source.Add("deck.collider", NativeWorldFixtures.ColliderBytes(colliderShape ?? shape));
        resources[collider.Id] = source.Read(collider).ToArray();
        var manifest = JsonNode.Parse(f.Assets.BorrowVerifiedResourceBytes("manifest").Span)!.AsObject();
        var leaf = manifest["assets"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "deck")!;
        leaf["sourceUnitsToMetres"] = 0.01f;
        leaf["collisionResourceId"] = collider.Id;
        leaf["renderBounds"] = JsonNode.Parse("""{"min":{"x":0,"y":-101,"z":-100},"max":{"x":100,"y":-1,"z":0}}""");
        var rows = manifest["resources"]!.AsArray();
        foreach (var row in rows)
        {
            string id = row!["reference"]!["id"]!.GetValue<string>();
            row["reference"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(resources[id]));
        }
        rows.Add(new JsonObject
        {
            ["reference"] = new JsonObject { ["id"] = collider.Id, ["path"] = collider.Path, ["sha256"] = collider.Sha256, ["payloadVersion"] = 1 },
            ["kind"] = "Collider",
            ["dependencies"] = new JsonArray()
        });
        rows.Single(r => r!["reference"]!["id"]!.GetValue<string>() == "hut")!["dependencies"]!.AsArray().Add(collider.Id);
        byte[] rootBytes = System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var root = f.Assets.Roots.Single() with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(rootBytes)) };
        resources[root.Id] = rootBytes;
        var assets = MapAssetClosure.Load(new[] { root }, new CapturedSource(resources));
        f.Document.NativeAssets = assets.Roots.ToList();
        return new(f.Document, assets);
    }

    sealed class CapturedSource(Dictionary<string, byte[]> resources) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => resources[reference.Id];
    }

    internal static (MapBuiltWorld Before, MapBuiltWorld After, MapNativeEditEffects PatchEffects) SupportReseat()
    {
        var f = NativeWorldFixtures.Placed("long-wall");
        var p = f.Document.Placements.Single();
        p.X = 65; p.Z = 1; p.Y = null;
        p.SupportBinding = new(MapSupportBindingKind.Surface, "floor", null, null, null, null);
        f.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Surface, "floor", new(64, 0, 0, 0, 1), new string('a', 64)) };
        f.Document.Bounds = new() { MinX = -64, MinZ = -64, MaxX = 192, MaxZ = 64 };
        f.Document.PlayableBounds = new() { MinX = f.Document.Bounds.MinX, MinZ = f.Document.Bounds.MinZ, MaxX = f.Document.Bounds.MaxX, MaxZ = f.Document.Bounds.MaxZ };
        var before = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        foreach (var patch in f.Document.Surfaces.Patches.Values) Array.Fill(patch.Heights, 100);
        var surface = f.Document.Surfaces.Refs.Single();
        f.Document.Surfaces.Refs[0] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            f.Document.Surfaces.Patches.Select(patch => new KeyValuePair<MapPatchKey, string>(patch.Key, MapSurfaceSemantics.PatchDigest(patch.Value))))
        };
        var after = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        var effects = new MapNativeEditEffects(new(64, 0, 0, 72, 0, 8), new(64, 1, 0, 72, 1, 8),
            new[] { new MapPatchKey("floor", 0, 0) }, [], [], [], MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency);
        return (before, after, effects);
    }
}
