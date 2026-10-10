using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Shared native world fixtures, built through public MapDoc and Physics APIs only. Every fixture collider
/// sits with its bottom at the placement origin, using a compound child pose where a primitive is centred, unless a
/// fixture states otherwise.</summary>
internal static class NativeWorldFixtures
{
    // ---------------------------------------------------------------------------------------------------------------
    // Asset closure
    // ---------------------------------------------------------------------------------------------------------------

    internal const string AssetRootId = "native-world.assets";
    const string SharedMeshId = "native-world.mesh";

    /// <summary>One verified closure holding every fixture asset, loaded from an in-memory source.</summary>
    internal static (MapAssetClosure Closure, IReadOnlyList<MapAssetRef> Roots) Assets()
    {
        var source = new NativeWorldAssetSource();
        var assets = new JsonArray();
        var resources = new JsonArray { Resource(source.Add(SharedMeshId, "mesh:native-world"), MapResourceKind.Mesh) };

        void Solid(string id, PhysicsShape collider, Vector3 min, Vector3 max, params string[] supportIds)
        {
            string colliderId = id + ".collider";
            resources.Add(Resource(source.Add(colliderId, ColliderBytes(collider)), MapResourceKind.Collider));
            assets.Add(Asset(id, min, max, colliderId, null, supportIds));
        }

        // A doorway: two jambs and a lintel, 1.8 m wide and 2.7 m tall, open between x = -0.6 and 0.6.
        Solid("doorway", Compound(
                (Box(0.3f, 2.4f, 0.3f), new Vector3(-0.75f, 1.2f, 0f)),
                (Box(0.3f, 2.4f, 0.3f), new Vector3(0.75f, 1.2f, 0f)),
                (Box(1.8f, 0.3f, 0.3f), new Vector3(0f, 2.55f, 0f))),
            new Vector3(-0.9f, 0f, -0.15f), new Vector3(0.9f, 2.7f, 0.15f));

        // Two 2 m by 2 m by 0.2 m walls meeting at a right angle in the corner at the origin.
        Solid("corner-wall", Compound(
                (Box(2f, 2f, 0.2f), new Vector3(1f, 1f, 0.1f)),
                (Box(0.2f, 2f, 2f), new Vector3(0.1f, 1f, 1f))),
            Vector3.Zero, new Vector3(2f, 2f, 2f));

        Solid("crate", Compound((Box(0.6f, 0.2f, 0.6f), new Vector3(0f, 0.1f, 0f))),
            new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.3f, 0.2f, 0.3f));

        Solid("tree", Compound((new CylinderShape(0.3f, 6f), new Vector3(0f, 3f, 0f))),
            new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.3f, 6f, 0.3f));

        Solid("large-building", Compound((Box(100f, 10f, 100f), new Vector3(0f, 5f, 0f))),
            new Vector3(-50f, 0f, -50f), new Vector3(50f, 10f, 50f));

        // Its corners lie 75 m from the origin along x.
        Solid("long-wall", Compound((Box(150f, 3f, 0.5f), new Vector3(0f, 1.5f, 0f))),
            new Vector3(-75f, 0f, -0.25f), new Vector3(75f, 3f, 0.25f));

        // The one fixture whose top, not its bottom, is at the placement origin.
        Solid("bridge-deck", Compound((Box(18f, 0.25f, 5f), new Vector3(0f, -0.125f, 0f))),
            new Vector3(-9f, -0.25f, -2.5f), new Vector3(9f, 0f, 2.5f));

        Solid("parapet-1m", Compound((Box(1f, 1f, 0.2f), new Vector3(0f, 0.5f, 0f))),
            new Vector3(-0.5f, 0f, -0.1f), new Vector3(0.5f, 1f, 0.1f));

        // A selection volume and no collider, so the sign can be examined but never blocks.
        string selectionId = "examine-sign.selection";
        resources.Add(Resource(source.Add(selectionId, ColliderBytes(Box(0.8f, 1.2f, 0.1f))), MapResourceKind.Selection));
        assets.Add(Asset("examine-sign", new Vector3(-0.4f, -0.6f, -0.05f), new Vector3(0.4f, 0.6f, 0.05f), null, selectionId));

        // Refused shape data.
        resources.Add(Resource(source.Add("garbage-collider.collider", "not a collision payload"), MapResourceKind.Collider));
        assets.Add(Asset("garbage-collider", -Vector3.One, Vector3.One, "garbage-collider.collider", null));

        var triangle = new TriangleMeshShape(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, new[] { 0, 1, 2 });
        Solid("mesh-in-compound", Compound((triangle, Vector3.Zero)), Vector3.Zero, new Vector3(1f, 0f, 1f));

        string supportId = "deck-with-support.surface";
        resources.Add(Resource(source.Add(supportId, "surface:deck-with-support"), MapResourceKind.Surface));
        Solid("deck-with-support", Compound((Box(4f, 0.25f, 4f), new Vector3(0f, -0.125f, 0f))),
            new Vector3(-2f, -0.25f, -2f), new Vector3(2f, 0f, 2f), supportId);

        MapAssetRef root = source.Add(AssetRootId, new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = assets,
            ["resources"] = resources
        }.ToJsonString());
        MapAssetRef[] roots = { root };
        return (MapAssetClosure.Load(roots, source), roots);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Shape and manifest helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A box from its full size in metres.</summary>
    internal static BoxShape Box(float x, float y, float z) => new(new Vector3(x, y, z) * 0.5f);

    /// <summary>A compound of identity-oriented children at the given local centres.</summary>
    internal static CompoundShape Compound(params (PhysicsShape Shape, Vector3 Centre)[] children) =>
        new(children.Select(child => new CompoundChild(child.Shape, Pose.At(child.Centre))).ToArray());

    /// <summary>The <see cref="PropCollisionFormat"/> version 1 bytes a Collider or Selection resource carries.</summary>
    internal static byte[] ColliderBytes(PhysicsShape shape)
    {
        using var stream = new MemoryStream();
        PropCollisionFormat.Write(shape, stream);
        return stream.ToArray();
    }

    static JsonObject Asset(string id, Vector3 min, Vector3 max, string? colliderId, string? selectionId,
        params string[] supportIds)
    {
        var asset = new JsonObject
        {
            ["id"] = id,
            ["meshResourceId"] = SharedMeshId,
            ["supportResourceIds"] = Ids(supportIds),
            ["materialResourceIds"] = new JsonArray(),
            ["lodResourceIds"] = new JsonArray(),
            ["lightResourceIds"] = new JsonArray(),
            ["sourceUnitsToMetres"] = 1,
            ["renderBounds"] = new JsonObject { ["min"] = Vector(min), ["max"] = Vector(max) },
            ["source"] = "fixtures/" + id + ".blend",
            ["license"] = "CC0",
            ["textured"] = false
        };
        if (colliderId is not null) asset["collisionResourceId"] = colliderId;
        if (selectionId is not null) asset["selectionResourceId"] = selectionId;
        return asset;
    }

    static JsonObject Resource(MapAssetRef reference, MapResourceKind kind) => new()
    {
        ["reference"] = new JsonObject
        {
            ["id"] = reference.Id,
            ["path"] = reference.Path,
            ["sha256"] = reference.Sha256,
            ["payloadVersion"] = reference.PayloadVersion
        },
        ["kind"] = kind.ToString(),
        ["dependencies"] = new JsonArray()
    };

    static JsonObject Vector(Vector3 v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

    static JsonArray Ids(string[] ids) => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
}

/// <summary>An in-memory resource source keyed by resource ID. Each added buffer gets its own digest-bearing
/// reference.</summary>
internal sealed class NativeWorldAssetSource : IMapAssetSource
{
    readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);

    internal MapAssetRef Add(string id, string text) => Add(id, Encoding.UTF8.GetBytes(text));

    internal MapAssetRef Add(string id, byte[] bytes)
    {
        _bytes[id] = bytes.ToArray();
        return new MapAssetRef(id, "assets/" + id, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1);
    }

    public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _bytes.TryGetValue(reference.Id, out byte[]? bytes)
        ? bytes : throw new MapDocumentException("Missing fixture resource " + reference.Id);
}
