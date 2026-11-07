using System;
using System.Buffers.Binary;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>Loads a native asset's mesh from its verified closure in asset-local metres. Unlike
/// <see cref="PropLoader.LoadProp"/>, which fits a mesh to a declared height, drops its base to zero and recentres X
/// and Z, this applies <see cref="MapResolvedAsset.SourceUnitsToMetres"/> once and nothing else, so a native asset keeps
/// its authored origin and proportions and the mesh agrees with its collider.</summary>
public static partial class NativeMapAssetLoader
{
    // "glTF" and "JSON" read as little-endian uint32 words: the GLB magic and the first chunk's type.
    const uint GlbMagic = 0x46546C67;
    const uint JsonChunk = 0x4E4F534A;

    /// <summary>Reads the mesh resource of <paramref name="asset"/>, which must be the descriptor
    /// <paramref name="closure"/> itself holds, and scales every vertex position by its source unit scale. Normals and
    /// tangents keep their directions under the uniform scale. The resource must be a self-contained binary glTF, parsed
    /// from the bytes the closure verified and nothing else. A buffer or image that names an external URI is refused,
    /// since its bytes would come from outside the closure. Embedded <c>data:</c> URIs are part of the verified bytes
    /// and load. Throws <see cref="MapDocumentException"/> with its reason and known asset and resource identifiers
    /// when the descriptor is not the closure's own, the resource is not a binary glTF, it names an external URI,
    /// or the mesh does not load.</summary>
    public static GltfMesh Load(MapResolvedAsset asset, MapAssetClosure closure)
    {
        RequireDescriptor(asset, closure);
        MapResolvedResource resource = closure.GetResource(asset.MeshResourceId);
        if (resource.Kind != MapResourceKind.Mesh)
            throw new MapDocumentException(MapDocumentRefusal.ReferenceKindMismatch,
                $"Native asset '{asset.Id}' requires Mesh resource '{asset.MeshResourceId}', which is {resource.Kind}.")
            { AssetId = asset.Id, ResourceId = asset.MeshResourceId, ExpectedResourceKind = MapResourceKind.Mesh, ActualResourceKind = resource.Kind };

        ReadOnlyMemory<byte> bytes = resource.Bytes;
        string what = $"Native asset '{asset.Id}' mesh resource '{asset.MeshResourceId}'";
        RequireSelfContained(bytes.Span, what, asset, resource);
        GltfMesh source;
        try
        {
            source = GltfLoader.LoadFlattenedAlbedo(bytes, $"{asset.Id}/{asset.MeshResourceId}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw MeshLoadFailure(ex, asset, resource);
        }

        float scale = asset.SourceUnitsToMetres;
        ModelVertex[] vertices = source.Vertices;
        var scaled = new ModelVertex[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            ModelVertex vertex = vertices[i];
            vertex.Position *= scale;
            scaled[i] = vertex;
        }
        return new GltfMesh(scaled, source.Indices32);
    }

    // Value-taking fault seam for the external loader boundary, without changing the closure's state.
    internal static MapDocumentException MeshLoadFailure(Exception error, MapResolvedAsset asset, MapResolvedResource resource, string? message = null)
    {
        string what = $"Native asset '{asset.Id}' mesh resource '{asset.MeshResourceId}'";
        message ??= $"{what} cannot be loaded: {error.Message}";
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is MapDocumentException known)
                return known.WithSourceName(null, message,
                    resourceId: resource.Reference.Id, assetId: asset.Id);
        return new MapDocumentException(MapDocumentRefusal.AssetPayloadUnreadable, message, error)
        { AssetId = asset.Id, ResourceId = resource.Reference.Id, Path = resource.Reference.Path, ExpectedHash = resource.Reference.Sha256 };
    }

    // Reads the GLB header and JSON chunk and refuses any buffer or image URI that is not an embedded data URI.
    static void RequireSelfContained(ReadOnlySpan<byte> glb, string what, MapResolvedAsset asset, MapResolvedResource resource)
    {
        if (glb.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != GlbMagic)
            throw new MapDocumentException(MapDocumentRefusal.SchemaMalformed, $"{what} is not a binary glTF.")
            { AssetId = asset.Id, ResourceId = asset.MeshResourceId };
        uint jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(glb[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(glb[16..]) != JsonChunk || jsonLength > (uint)(glb.Length - 20))
            throw new MapDocumentException(MapDocumentRefusal.SchemaMalformed, $"{what} is not a binary glTF: its first chunk is not a complete JSON chunk.")
            { AssetId = asset.Id, ResourceId = asset.MeshResourceId };
        try
        {
            var reader = new Utf8JsonReader(glb.Slice(20, (int)jsonLength));
            using JsonDocument json = JsonDocument.ParseValue(ref reader);
            RefuseExternal(json.RootElement, "buffers", "buffer", what, asset);
            RefuseExternal(json.RootElement, "images", "image", what, asset);
        }
        catch (JsonException ex)
        {
            throw new MapDocumentException(MapDocumentRefusal.SchemaMalformed, $"{what} is not a binary glTF: its JSON chunk does not parse.", ex)
            { AssetId = asset.Id, ResourceId = resource.Reference.Id, FieldPath = ex.Path, LineNumber = ex.LineNumber, BytePositionInLine = ex.BytePositionInLine };
        }
    }

    static void RefuseExternal(JsonElement root, string member, string kind, string what, MapResolvedAsset asset)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(member, out JsonElement list) ||
            list.ValueKind != JsonValueKind.Array) return;
        for (int index = 0; index < list.GetArrayLength(); index++)
        {
            JsonElement item = list[index];
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("uri", out JsonElement uri)) continue;
            string? text = uri.ValueKind == JsonValueKind.String ? uri.GetString() : uri.GetRawText();
            if (text is not null && text.StartsWith("data:", StringComparison.Ordinal)) continue;
            throw new MapDocumentException(MapDocumentRefusal.AssetGlbExternalUri,
                $"{what} names external {kind} URI '{text}'. A native mesh must be self-contained, because only bytes the closure verified may be parsed.")
            { AssetId = asset.Id, ResourceId = asset.MeshResourceId, FieldPath = $"$.{member}[{index}].uri", Uri = text };
        }
    }
}
