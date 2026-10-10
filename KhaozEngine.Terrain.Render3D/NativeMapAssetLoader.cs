using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>Loads a native asset's mesh from its verified closure in asset-local metres. Unlike
/// <see cref="PropLoader.LoadProp"/>, which fits a mesh to a declared height, drops its base to zero and recentres X
/// and Z, this applies <see cref="MapResolvedAsset.SourceUnitsToMetres"/> once and nothing else, so a native asset keeps
/// its authored origin and proportions and the mesh agrees with its collider.</summary>
public static class NativeMapAssetLoader
{
    // "glTF" read as a little-endian uint32, the first word of every binary glTF.
    const uint GlbMagic = 0x46546C67;

    /// <summary>Reads the mesh resource of <paramref name="asset"/>, which must be the descriptor
    /// <paramref name="closure"/> itself holds, and scales every vertex position by its source unit scale. Normals and
    /// tangents keep their directions under the uniform scale. The resource must be a binary glTF, and only the bytes the
    /// closure verified are parsed. They are copied to a private temporary file first, so a relative external reference
    /// resolves where nothing exists: a missing external buffer fails the load, and a missing external image only loses
    /// its albedo tint. Throws <see cref="MapDocumentException"/> naming the asset and resource when the descriptor is not
    /// the closure's own, the resource is not a binary glTF, or the mesh does not load.</summary>
    public static GltfMesh Load(MapResolvedAsset asset, MapAssetClosure closure)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(closure);
        if (!ReferenceEquals(closure.GetAsset(asset.Id), asset))
            throw new MapDocumentException($"Native asset '{asset.Id}' is not the descriptor of the supplied closure.");
        MapResolvedResource resource = closure.GetResource(asset.MeshResourceId);
        if (resource.Kind != MapResourceKind.Mesh)
            throw new MapDocumentException(
                $"Native asset '{asset.Id}' requires Mesh resource '{asset.MeshResourceId}', which is {resource.Kind}.");

        // Bytes already returns a fresh copy, so use that buffer rather than copying it again.
        ReadOnlyMemory<byte> memory = resource.Bytes;
        byte[] bytes = MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) &&
            segment.Offset == 0 && segment.Count == segment.Array!.Length ? segment.Array : memory.ToArray();
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != GlbMagic)
            throw new MapDocumentException(
                $"Native asset '{asset.Id}' mesh resource '{asset.MeshResourceId}' is not a binary glTF.");

        GltfMesh source = Parse(bytes, asset);
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

    // GltfLoader reads from a path, so the verified buffer goes to a private temporary file and is parsed from there.
    static GltfMesh Parse(byte[] bytes, MapResolvedAsset asset)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ke-native-mesh-{Guid.NewGuid():N}.glb");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes);
            return GltfLoader.LoadFlattenedAlbedo(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new MapDocumentException(
                $"Native asset '{asset.Id}' mesh resource '{asset.MeshResourceId}' cannot be loaded: {ex.Message}", ex);
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    // A leftover temporary file must never replace the load's own result or exception.
    static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
