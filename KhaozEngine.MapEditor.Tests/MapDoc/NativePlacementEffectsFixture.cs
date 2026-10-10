using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>A resolver-1 native document over an in-memory closure on 64 m tiles. <c>short-crate</c> is a 1 m square box
/// 0.5 m tall standing on its origin, so its interaction envelope is raised to 1 m. <c>mesh-only</c> declares no
/// collider and no selection volume. <c>crate-1</c> stands at (10, 0, 10), and <c>marker-1</c>, a mesh-only placement
/// at (-10, 0, -10), exists only when asked for, since a world build refuses it.</summary>
internal sealed class NativePlacementEffectsFixture
{
    public static readonly MapWorldGrids Grids = new(64f, 1, 4, Vector2.Zero);
    public static readonly MapNavTileOptions NavOptions = new(0f, "placement-effects-profile", "placement-effects-controller");

    public MapDocument Document { get; }
    public MapAssetClosure Assets { get; }
    public EditorDocument Editor { get; }

    public NativePlacementEffectsFixture(bool bindProvider, bool withMeshOnly = false)
    {
        var source = new MemoryAssetSource();
        var crate = new CompoundShape(new[]
        {
            new CompoundChild(new BoxShape(new Vector3(0.5f, 0.25f, 0.5f)), Pose.At(new Vector3(0f, 0.25f, 0f))),
        });
        MapAssetRef crateMesh = source.Add("crate.mesh", "kit/crate.glb", Encoding.UTF8.GetBytes("crate mesh"));
        MapAssetRef crateCollider = source.Add("crate.collider", "kit/crate.coll", Collider(crate));
        MapAssetRef markerMesh = source.Add("marker.mesh", "kit/marker.glb", Encoding.UTF8.GetBytes("marker mesh"));
        string manifest = NativeEditorAssetFixtures.Manifest(
            new[]
            {
                NativeEditorAssetFixtures.Asset("short-crate", "crate.mesh", "crate.collider", 1f,
                    new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f)),
                NativeEditorAssetFixtures.Asset("mesh-only", "marker.mesh", null, 1f,
                    new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 2f, 0.5f)),
            },
            new[]
            {
                NativeEditorAssetFixtures.Resource(crateMesh, "Mesh"),
                NativeEditorAssetFixtures.Resource(crateCollider, "Collider"),
                NativeEditorAssetFixtures.Resource(markerMesh, "Mesh"),
            });
        MapAssetRef root = source.Add("kit", "kit/placement-effects.manifest.json", Encoding.UTF8.GetBytes(manifest));

        Document = new MapDocument
        {
            Id = "native-placement-effects",
            TileSize = 64f,
            Bounds = new() { MinX = -256, MinZ = -256, MaxX = 256, MaxZ = 256 },
            PlayableBounds = new() { MinX = -256, MinZ = -256, MaxX = 256, MaxZ = 256 },
            ResolverIdentity = new(1, 1),
            NativeAssets = new() { root },
        };
        Document.Terrain.Biomes.Add(new MapBiomeBand());
        Document.Placements.Add(Crate("crate-1", 10f, 10f));
        if (withMeshOnly)
            Document.Placements.Add(new MapPlacement { Id = "marker-1", Kind = "prop", AssetId = "mesh-only", X = -10, Y = 0, Z = -10 });
        Assets = MapAssetClosure.Load(Document.NativeAssets, source);
        MapBoundDocumentValidation.Validate(Document, Assets);
        Editor = new EditorDocument(Document);
        if (bindProvider) Editor.BindNativeAssets(Assets, NativePlacementBoundsProvider.Instance);
        else Editor.BindNativeAssets(Assets);
    }

    public static MapPlacement Crate(string id, float x, float z) =>
        new() { Id = id, Kind = "prop", AssetId = "short-crate", X = x, Y = 0, Z = z };

    /// <summary>The current document built as a resolver-1 world on flat ground at Y 0.</summary>
    public MapBuiltWorld Build() => MapWorldBuilder.Build(Document, Assets, new MapWorldBuildOptions(
        new MapResolveOptions("placement-effects", 1, "placement-effects-options"), "placement-effects-policy",
        new MapTerrainChunkPolicy(), (_, _) => 0f));

    /// <summary>The union of a built placement's collider bounds and interaction envelope bounds.</summary>
    public static MapBox3 ColliderAndEnvelope(MapBuiltWorld world, string placementId)
    {
        MapPlacementGeometry geometry = world.Placements.Single(p => p.PlacementId == placementId);
        MapBox3 c = geometry.ColliderBounds!.Value, e = geometry.Envelope.Bounds;
        return new MapBox3(Math.Min(c.MinX, e.MinX), Math.Min(c.MinY, e.MinY), Math.Min(c.MinZ, e.MinZ),
            Math.Max(c.MaxX, e.MaxX), Math.Max(c.MaxY, e.MaxY), Math.Max(c.MaxZ, e.MaxZ));
    }

    static byte[] Collider(PhysicsShape shape)
    {
        using var stream = new MemoryStream();
        PropCollisionFormat.Write(shape, stream);
        return stream.ToArray();
    }

    sealed class MemoryAssetSource : IMapAssetSource
    {
        readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);

        internal MapAssetRef Add(string id, string path, byte[] bytes)
        {
            _bytes.Add(path, bytes);
            return new MapAssetRef(id, path, NativeEditorAssetFixtures.Digest(bytes), 1);
        }

        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _bytes.TryGetValue(reference.Path, out byte[]? bytes)
            ? bytes : throw new KeyNotFoundException(reference.Path);
    }
}
