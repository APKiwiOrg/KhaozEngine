using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Physics;

namespace KhaozEngine.Tests.MapDoc;

internal sealed partial class NativePlacementEffectsFixture
{
    NativePlacementEffectsFixture(MapDocument document, MapAssetClosure assets)
    {
        Document = document;
        Assets = assets;
        MapBoundDocumentValidation.Validate(Document, Assets);
        Editor = new EditorDocument(Document);
        Editor.BindNativeAssets(Assets, NativePlacementBoundsProvider.Instance);
    }

    public static NativePlacementEffectsFixture NestedCompound()
    {
        var source = new MemoryAssetSource();
        MapAssetRef mesh = source.Add("nested.mesh", "kit/nested.glb", Encoding.UTF8.GetBytes("nested compound mesh"));
        MapAssetRef collider = source.Add("nested.collider", "kit/nested.coll", Collider(NativeWorldFixtures.NestedCompoundShape(4)));
        string manifest = NativeEditorAssetFixtures.Manifest(
            new[] { NativeEditorAssetFixtures.Asset("nested-compound", mesh.Id, collider.Id, 1f,
                new(-1, -0.5f, -0.5f), new(0.453125f, 0.5f, 0.5f)) },
            new[] { NativeEditorAssetFixtures.Resource(mesh, "Mesh"), NativeEditorAssetFixtures.Resource(collider, "Collider") });
        MapAssetRef root = source.Add("nested.assets", "kit/nested.manifest.json", Encoding.UTF8.GetBytes(manifest));
        var document = new MapDocument
        {
            Id = "nested-compound-edit-world",
            TileSize = 64,
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = new() { root },
            Bounds = new() { MinX = 0, MinZ = -64, MaxX = 64, MaxZ = 0 },
            PlayableBounds = new() { MinX = 0, MinZ = -64, MaxX = 64, MaxZ = 0 },
        };
        document.Placements.Add(new() { Id = "nested-compound", Kind = "prop", AssetId = "nested-compound", X = 62.5f, Y = 0, Z = 0 });
        return new(document, MapAssetClosure.Load(document.NativeAssets, source));
    }

    public MapBuiltWorld BuildNestedCompound() => MapWorldBuilder.Build(Document, Assets,
        new(NativeDocumentService.SessionOptionsV2, "nested-compound-edit-policy", new MapTerrainChunkPolicy()));
}
