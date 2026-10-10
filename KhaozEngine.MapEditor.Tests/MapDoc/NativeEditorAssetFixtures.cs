using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>A verified one-asset closure over in-memory resources.</summary>
internal sealed record NativeScaledMeshAsset(MapResolvedAsset Asset, MapAssetClosure Closure);

/// <summary>Native asset fixtures for editor tests: binary glTF meshes, manifest JSON and in-memory closures.</summary>
internal static class NativeEditorAssetFixtures
{
    /// <summary>One asset whose mesh spans raw Y from 20 to <paramref name="rawMaxY"/> source units, with matching
    /// render bounds. A loader that normalized height or dropped the base would not land the top at
    /// <paramref name="rawMaxY"/> times <paramref name="sourceUnitsToMetres"/>.</summary>
    internal static NativeScaledMeshAsset ScaledMeshAsset(float sourceUnitsToMetres, float rawMaxY)
    {
        const float rawMinY = 20f;
        var source = new MemoryAssetSource();
        MapAssetRef mesh = source.Add("scaled.mesh", "kit/scaled-body.glb", QuadGlb(rawMinY, rawMaxY));
        string manifest = Manifest(
            new[] { Asset("scaled", "scaled.mesh", null, sourceUnitsToMetres, new Vector3(-1, rawMinY, 0), new Vector3(1, rawMaxY, 0)) },
            new[] { Resource(mesh, "Mesh") });
        MapAssetRef root = source.Add("scaled-kit", "kit/scaled.manifest.json", Encoding.UTF8.GetBytes(manifest));
        MapAssetClosure closure = MapAssetClosure.Load(new[] { root }, source);
        return new NativeScaledMeshAsset(closure.GetAsset("scaled"), closure);
    }

    /// <summary>One asset whose verified mesh is a binary glTF that names an external buffer, <c>body.bin</c>.</summary>
    internal static NativeScaledMeshAsset ExternalBufferMeshAsset() => MeshAsset("external",
        GlbWithJson("""{"asset":{"version":"2.0"},"buffers":[{"uri":"body.bin","byteLength":36}]}"""));

    /// <summary>One asset whose verified mesh is a binary glTF that names an external image, <c>albedo.png</c>.</summary>
    internal static NativeScaledMeshAsset ExternalImageMeshAsset() => MeshAsset("external-image",
        GlbWithJson("""{"asset":{"version":"2.0"},"images":[{"uri":"albedo.png"}]}"""));

    /// <summary>One asset whose verified mesh is a binary glTF holding one triangle, (0, 0, 0), (1, 0, 0) and
    /// (0, 2, 0), in a buffer embedded as a <c>data:</c> URI.</summary>
    internal static NativeScaledMeshAsset DataBufferMeshAsset()
    {
        var data = new byte[42];
        Buffer.BlockCopy(new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 2f, 0f }, 0, data, 0, 36);
        Buffer.BlockCopy(new ushort[] { 0, 1, 2 }, 0, data, 36, 6);
        string uri = "data:application/octet-stream;base64," + Convert.ToBase64String(data);
        return MeshAsset("embedded", GlbWithJson($$"""
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":42,"uri":"{{uri}}"}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36,"target":34962},
              {"buffer":0,"byteOffset":36,"byteLength":6,"target":34963}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,2,0]},
              {"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1,"mode":4}]}],
             "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
            """));
    }

    // A one-asset closure whose mesh resource holds glb.
    static NativeScaledMeshAsset MeshAsset(string id, byte[] glb)
    {
        var source = new MemoryAssetSource();
        MapAssetRef mesh = source.Add(id + ".mesh", "kit/" + id + "-body.glb", glb);
        string manifest = Manifest(
            new[] { Asset(id, id + ".mesh", null, 1f, new Vector3(-1, 0, 0), new Vector3(1, 1, 0)) },
            new[] { Resource(mesh, "Mesh") });
        MapAssetRef root = source.Add(id + "-kit", "kit/" + id + ".manifest.json", Encoding.UTF8.GetBytes(manifest));
        MapAssetClosure closure = MapAssetClosure.Load(new[] { root }, source);
        return new NativeScaledMeshAsset(closure.GetAsset(id), closure);
    }

    /// <summary>A binary glTF holding only a JSON chunk with <paramref name="json"/>, padded with spaces.</summary>
    internal static byte[] GlbWithJson(string json)
    {
        byte[] text = Encoding.UTF8.GetBytes(json);
        int padded = (text.Length + 3) & ~3;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x46546C67u);
            writer.Write(2u);
            writer.Write((uint)(12 + 8 + padded));
            writer.Write((uint)padded);
            writer.Write(0x4E4F534Au);
            writer.Write(text);
            for (int i = text.Length; i < padded; i++) writer.Write((byte)' ');
        }
        return stream.ToArray();
    }

    /// <summary>The largest vertex Y of <paramref name="mesh"/>.</summary>
    internal static float MaxVertexY(GltfMesh mesh)
    {
        float max = float.NegativeInfinity;
        foreach (ModelVertex vertex in mesh.Vertices) max = MathF.Max(max, vertex.Position.Y);
        return max;
    }

    /// <summary>A binary glTF holding one upright quad from X -1 to 1 and Y <paramref name="minY"/> to
    /// <paramref name="maxY"/> at Z 0.</summary>
    internal static byte[] QuadGlb(float minY, float maxY)
    {
        var material = new MaterialBuilder("flat").WithBaseColor(new Vector4(0.5f, 0.5f, 0.5f, 1f));
        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("quad");
        var primitive = mesh.UsePrimitive(material);
        VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(float x, float y) =>
            new(new VertexPositionNormal(new Vector3(x, y, 0), Vector3.UnitZ));
        primitive.AddTriangle(V(-1, minY), V(1, minY), V(1, maxY));
        primitive.AddTriangle(V(-1, minY), V(1, maxY), V(-1, maxY));
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        using var stream = new MemoryStream();
        scene.ToGltf2().WriteGLB(stream);
        return stream.ToArray();
    }

    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string Manifest(IEnumerable<string> assets, IEnumerable<string> resources) =>
        $$"""{"payloadVersion":1,"assets":[{{string.Join(",", assets)}}],"resources":[{{string.Join(",", resources)}}]}""";

    internal static string Asset(string id, string meshId, string? colliderId, float sourceUnitsToMetres, Vector3 min, Vector3 max)
    {
        string collider = colliderId is null ? "" : $",\"collisionResourceId\":\"{colliderId}\"";
        return $$"""
            {"id":"{{id}}","meshResourceId":"{{meshId}}"{{collider}},"supportResourceIds":[],"materialResourceIds":[],
             "lodResourceIds":[],"lightResourceIds":[],"sourceUnitsToMetres":{{F(sourceUnitsToMetres)}},
             "renderBounds":{"min":{{V(min)}},"max":{{V(max)}}},"source":"authored kit","license":"CC0","textured":false}
            """;
    }

    internal static string Resource(MapAssetRef reference, string kind) =>
        $$"""{"reference":{"id":"{{reference.Id}}","path":"{{reference.Path}}","sha256":"{{reference.Sha256}}","payloadVersion":1},"kind":"{{kind}}","dependencies":[]}""";

    static string V(Vector3 v) => $$"""{"x":{{F(v.X)}},"y":{{F(v.Y)}},"z":{{F(v.Z)}}}""";

    static string F(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Resources served from memory by path, so a closure needs no directory.</summary>
    sealed class MemoryAssetSource : IMapAssetSource
    {
        readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);

        internal MapAssetRef Add(string id, string path, byte[] bytes)
        {
            _bytes.Add(path, bytes);
            return new MapAssetRef(id, path, Digest(bytes), 1);
        }

        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _bytes.TryGetValue(reference.Path, out byte[]? bytes)
            ? bytes : throw new KeyNotFoundException(reference.Path);
    }
}
