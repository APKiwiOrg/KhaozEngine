using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public sealed record MapExtentProofResult(MapRenderAssetStatus Status, MapAssetExtentProofSet? Proofs, long PeakTemporaryBytes, string? Detail = null)
{
    public KhaozEngine.MapDoc.Scene.MapSceneCapacity? Capacity { get; init; }
}

/// <summary>Streams source geometry for conservative bounds, retaining provenance only and never decoding an image.</summary>
public static class MapRenderExtentValidator
{
    public static MapExtentProofResult Validate(MapAssetClosure closure, IReadOnlyList<string> assetIds,
        GltfDecodeBudget scratch, long maxProofBytes = 16777216)
    {
        ArgumentNullException.ThrowIfNull(closure); ArgumentNullException.ThrowIfNull(assetIds); ArgumentNullException.ThrowIfNull(scratch);
        long peak = 0, metadata = 256;
        var rows = new List<MapAssetExtentProof>();
        try
        {
            if (DemandDiagnostics.Check(closure, "MapRenderExtentValidator.MaxProofBytes.1", "MaxProofBytes", 0, metadata, maxProofBytes, maxProofBytes < metadata, CapacityDemandLifetime.Transient)) throw MapRenderDecodeCapacity.Refuse("MaxProofBytes", metadata, maxProofBytes);
            foreach (string id in assetIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                MapResolvedAsset asset;
                try { asset = closure.GetAsset(id); }
                catch (MapDocumentException ex) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, ex.Message); }
                string[] resources = new[] { asset.MeshResourceId }.Concat(asset.LodResourceIds).ToArray();
                long rowBytes = checked(512L + Encoding.UTF8.GetByteCount(id) + resources.Sum(r => 256L + Encoding.UTF8.GetByteCount(r)));
                if (DemandDiagnostics.Check(closure, "MapRenderExtentValidator.MaxProofBytes.2", "MaxProofBytes", metadata, rowBytes, maxProofBytes, rowBytes > maxProofBytes - metadata, CapacityDemandLifetime.Transient)) throw MapRenderDecodeCapacity.Refuse("MaxProofBytes", (decimal)metadata + rowBytes, maxProofBytes);
                var proofs = new List<MapAssetResourceExtentProof>();
                for (int i = 0; i < resources.Length; i++)
                {
                    string resourceId = resources[i];
                    var resource = closure.GetResource(resourceId);
                    MapLocalBounds declared = i == 0 ? asset.RenderBounds : asset.LodBounds ?? asset.RenderBounds;
                    MapLocalBounds actual = Bounds(closure.BorrowVerifiedResourceBytes(resourceId), scratch, ref peak);
                    if (!Contains(declared, actual)) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Declared bounds exclude geometry for " + id + "/" + resourceId);
                    proofs.Add(new(resourceId, resource.Reference.Sha256, declared, actual));
                }
                MapLocalBounds? lights = null;
                foreach (string lightId in asset.LightResourceIds)
                {
                    if (!closure.HasLight(lightId)) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, "Native light requires a typed light resource.");
                    var light = closure.BorrowLight(lightId);
                    var b = new MapLocalBounds((light.Position - new Vector3(light.Radius)) / asset.SourceUnitsToMetres,
                        (light.Position + new Vector3(light.Radius)) / asset.SourceUnitsToMetres);
                    lights = lights is { } prior ? new(Vector3.Min(prior.Min, b.Min), Vector3.Max(prior.Max, b.Max)) : b;
                }
                if (lights is { } lightBounds && (asset.LightBounds is not { } declaredLight || !Contains(declaredLight, lightBounds)))
                    throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Declared light bounds exclude effective light reach.");
                metadata += rowBytes;
                rows.Add(new(id, asset.SourceUnitsToMetres, proofs, asset.LightBounds, lights, 1));
            }
            long hashStrings = rows.Count == 0 ? 0 : rows.Max(row => checked((long)Encoding.UTF8.GetByteCount(row.AssetId) +
                row.Resources.Sum(resource => (long)Encoding.UTF8.GetByteCount(resource.ResourceId) + resource.Sha256.Length)));
            long hashCharge = checked(8192 + 12 * hashStrings);
            if (DemandDiagnostics.Check(closure, "MapRenderExtentValidator.Array.MaxLength.1", "Array.MaxLength", 0, hashCharge, Array.MaxLength, hashCharge > Array.MaxLength, CapacityDemandLifetime.Transient)) throw MapRenderDecodeCapacity.Refuse("Array.MaxLength", hashCharge, Array.MaxLength);
            MapRenderDecodeCapacity.Reserve(scratch, 0, hashCharge);
            peak = Math.Max(peak, hashCharge);
            string digest;
            try
            {
                using var hash = SHA256.Create();
                using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
                using (var writer = new Utf8JsonWriter(stream, MapJsonTextOptions.IndentedWriter))
                {
                    writer.WriteStartObject(); writer.WriteString("closure", closure.Hash); writer.WriteStartArray("assets");
                    foreach (var row in rows)
                    {
                        writer.WriteStartObject(); writer.WriteString("id", row.AssetId); writer.WriteNumber("units", row.SourceUnitsToMetres);
                        writer.WriteNumber("validationVersion", row.ValidationVersion); writer.WriteStartArray("resources");
                        foreach (var resource in row.Resources)
                        {
                            writer.WriteStartObject(); writer.WriteString("id", resource.ResourceId); writer.WriteString("sha256", resource.Sha256);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray(); writer.WriteEndObject();
                        writer.Flush();
                    }
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                stream.FlushFinalBlock();
                digest = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
            }
            finally { scratch.ReleaseTemporary(hashCharge); }
            return new(MapRenderAssetStatus.Complete, new(closure.Hash, digest, rows, metadata), peak);
        }
        catch (Exception ex) when (ex is MapRenderAssetRefusal or MapDocumentException or JsonException or InvalidOperationException or
            KeyNotFoundException or ArgumentOutOfRangeException or OverflowException or FormatException)
        { return new(MapRenderAssets.Status(ex), null, peak, ex.Message) { Capacity = MapRenderDecodeCapacity.From(ex) }; }
    }

    static MapLocalBounds Bounds(ReadOnlyMemory<byte> glb, GltfDecodeBudget scratch, ref long peak)
    {
        var bytes = glb.Span;
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546c67 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) != 0x4e4f534a) throw Invalid("Invalid GLB header in extent validation.");
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        if (length < 0 || length > bytes.Length - 20) throw Invalid("Incomplete GLB JSON.");
        var jsonBytes = glb.Slice(20, length);
        long tokens = 0;
        var reader = new Utf8JsonReader(jsonBytes.Span);
        while (reader.Read()) tokens = checked(tokens + 1);
        long charge = checked(512L + 6L * length + 64 * tokens);
        MapRenderDecodeCapacity.Reserve(scratch, 0, charge);
        peak = Math.Max(peak, charge);
        try
        {
            using var json = JsonDocument.Parse(jsonBytes);
            var root = json.RootElement;
            int binOffset = checked(20 + length);
            ReadOnlyMemory<byte> binary = ReadOnlyMemory<byte>.Empty;
            if (binOffset != bytes.Length)
            {
                if (bytes.Length - binOffset < 8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(binOffset + 4)..]) != 0x004e4942)
                    throw Invalid("Invalid binary buffer.");
                int binLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[binOffset..]));
                if (binLength != bytes.Length - binOffset - 8) throw Invalid("Invalid binary chunk length.");
                binary = glb.Slice(binOffset + 8, binLength);
            }
            int nodeCount = root.TryGetProperty("nodes", out var nodes) ? nodes.GetArrayLength() : 0;
            var meshes = root.GetProperty("meshes");
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            bool saw = false;
            for (int m = 0; m < meshes.GetArrayLength(); m++)
            {
                bool placed = false;
                for (int n = 0; n < nodeCount; n++)
                    if (Int(nodes[n], "mesh", -1) == m) { Visit(meshes[m], World(n, 0)); placed = true; }
                if (!placed) Visit(meshes[m], Matrix4x4.Identity);
            }
            if (!saw) throw Invalid("Extent resource has no positions.");
            return new(min, max);

            Matrix4x4 World(int node, int depth)
            {
                if (depth > 64) throw Invalid("Node hierarchy is cyclic or too deep.");
                var n = nodes[node];
                Matrix4x4 local;
                if (n.TryGetProperty("matrix", out var matrix))
                {
                    if (matrix.GetArrayLength() != 16) throw Invalid("Invalid node matrix.");
                    local = new(matrix[0].GetSingle(), matrix[1].GetSingle(), matrix[2].GetSingle(), matrix[3].GetSingle(),
                        matrix[4].GetSingle(), matrix[5].GetSingle(), matrix[6].GetSingle(), matrix[7].GetSingle(),
                        matrix[8].GetSingle(), matrix[9].GetSingle(), matrix[10].GetSingle(), matrix[11].GetSingle(),
                        matrix[12].GetSingle(), matrix[13].GetSingle(), matrix[14].GetSingle(), matrix[15].GetSingle());
                }
                else
                {
                    Vector3 scale = Vector(n, "scale", Vector3.One), translation = Vector(n, "translation", Vector3.Zero);
                    Quaternion rotation = Quaternion.Identity;
                    if (n.TryGetProperty("rotation", out var r)) rotation = new(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
                    local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
                }
                int parent = -1;
                for (int p = 0; p < nodeCount; p++)
                    if (nodes[p].TryGetProperty("children", out var children))
                        foreach (var child in children.EnumerateArray())
                            if (child.GetInt32() == node)
                            { if (parent != -1) throw Invalid("Node has multiple parents."); parent = p; }
                return parent < 0 ? local : local * World(parent, depth + 1);
            }

            void Visit(JsonElement mesh, Matrix4x4 world)
            {
                foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
                {
                    var a = root.GetProperty("accessors")[primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32()];
                    if (a.GetProperty("componentType").GetInt32() != 5126 || a.GetProperty("type").GetString() != "VEC3" || a.TryGetProperty("sparse", out _))
                        throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, "Extent positions require nonsparse floating VEC3 accessors.");
                    var view = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
                    var buffer = root.GetProperty("buffers")[view.GetProperty("buffer").GetInt32()];
                    ReadOnlyMemory<byte> bufferBytes = binary;
                    if (buffer.TryGetProperty("uri", out var uri))
                    {
                        string text = uri.GetString()!;
                        int comma = text.IndexOf(',');
                        if (!text.StartsWith("data:", StringComparison.Ordinal) || comma < 0 ||
                            !text.AsSpan(0, comma).EndsWith(";base64", StringComparison.Ordinal))
                            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, "Extent buffers must be verified embedded data.");
                        bufferBytes = Convert.FromBase64String(text[(comma + 1)..]);
                    }
                    else if (view.GetProperty("buffer").GetInt32() != 0) throw Invalid("Invalid binary buffer index.");
                    int count = a.GetProperty("count").GetInt32(), stride = Int(view, "byteStride", 12);
                    long viewOffset = Int(view, "byteOffset", 0), offset = Int(a, "byteOffset", 0);
                    long viewLength = view.GetProperty("byteLength").GetInt64();
                    if (count < 0 || stride < 12 || offset < 0 || viewOffset < 0 || viewLength < 0 ||
                        checked(viewOffset + viewLength) > bufferBytes.Length || checked(viewOffset + viewLength) > buffer.GetProperty("byteLength").GetInt64() ||
                        (count > 0 && checked(offset + (long)(count - 1) * stride + 12) > viewLength)) throw Invalid("Position accessor exceeds verified buffer ranges.");
                    for (int i = 0; i < count; i++)
                    {
                        int start = checked((int)(viewOffset + offset + (long)i * stride));
                        var data = bufferBytes.Span.Slice(start, 12);
                        var point = Vector3.Transform(new(BinaryPrimitives.ReadSingleLittleEndian(data),
                            BinaryPrimitives.ReadSingleLittleEndian(data[4..]), BinaryPrimitives.ReadSingleLittleEndian(data[8..])), world);
                        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) throw Invalid("Extent positions must be finite.");
                        min = Vector3.Min(min, point); max = Vector3.Max(max, point); saw = true;
                    }
                }
            }
        }
        finally { scratch.ReleaseTemporary(charge); }
    }

    static Vector3 Vector(JsonElement node, string name, Vector3 fallback) => node.TryGetProperty(name, out var v)
        ? new(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle()) : fallback;
    static int Int(JsonElement element, string name, int fallback) => element.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
    static bool Contains(MapLocalBounds declared, MapLocalBounds actual) => actual.Min.X >= declared.Min.X && actual.Min.Y >= declared.Min.Y &&
        actual.Min.Z >= declared.Min.Z && actual.Max.X <= declared.Max.X && actual.Max.Y <= declared.Max.Y && actual.Max.Z <= declared.Max.Z;
    static MapRenderAssetRefusal Invalid(string detail) => new(MapRenderAssetStatus.Invalid, detail);
}
