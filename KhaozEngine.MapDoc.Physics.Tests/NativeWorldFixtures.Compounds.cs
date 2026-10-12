using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.MapDoc.Physics;

internal static partial class NativeWorldFixtures
{
    internal static MapBuiltWorld BuildNestedCompound(int levels)
    {
        float[] offsets = new[] { -524288f }.Concat(Enumerable.Repeat(-0.03125f, levels - 2)).Append(524288f).ToArray();
        PhysicsShape shape = new BoxShape(new(0.515625f, 0.5f, 0.5f));
        foreach (float offset in offsets.Reverse())
            shape = new CompoundShape(new[] { new CompoundChild(shape, Pose.At(new(offset, 0, 0))) });
        var source = new NativeWorldAssetSource();
        MapAssetRef mesh = source.Add(SharedMeshId, "mesh:nested-compound");
        MapAssetRef collider = source.Add("nested-compound.collider", ColliderBytes(shape));
        MapAssetRef root = source.Add("nested-compound.assets", new JsonObject
        {
            ["payloadVersion"] = 1,
            // The authored render extent ends before the seam. Only the installed collider adds tile X 1.
            ["assets"] = new JsonArray(Asset("nested-compound", new(-1, -0.5f, -0.5f), new(0.453125f, 0.5f, 0.5f), collider.Id, null)),
            ["resources"] = new JsonArray(Resource(mesh, MapResourceKind.Mesh), Resource(collider, MapResourceKind.Collider))
        }.ToJsonString());
        var assets = MapAssetClosure.Load(new[] { root }, source);
        var document = new MapDocument
        {
            Id = "nested-compound-world",
            TileSize = 64,
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = new() { root },
            Bounds = new() { MinX = 0, MinZ = -64, MaxX = 64, MaxZ = 0 },
            PlayableBounds = new() { MinX = 0, MinZ = -64, MaxX = 64, MaxZ = 0 }
        };
        document.Placements.Add(new()
        {
            Id = "nested-compound",
            Kind = "prop",
            AssetId = "nested-compound",
            X = 63.5f,
            Y = 0,
            Z = 0,
            Scale = 1
        });
        return MapWorldBuilder.Build(document, assets, Options());
    }
}
