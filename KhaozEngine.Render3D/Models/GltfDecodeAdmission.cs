using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D;

public enum GltfDecodeStatus { Invalid, Unsupported, CapacityExceeded }
public sealed class GltfDecodeRefusal(GltfDecodeStatus status, string detail) : Exception(detail)
{
    public GltfDecodeStatus Status { get; } = status;
    public GltfDecodeCapacity? Capacity { get; init; }
}

public readonly record struct GltfAccessorFact(int Count, int ComponentBytes, int Components, int Stride);

/// <summary>A non-reserving checked measurement. No model, mesh or pixels are decoded by this scan.</summary>
public sealed class GltfDecodeAdmission
{
    [ThreadStatic] static int _modelDecodeEntries;
    [ThreadStatic] static int _largeAllocationEntries;
    internal static int ModelDecodeEntries => _modelDecodeEntries;
    internal static int LargeAllocationEntries => _largeAllocationEntries;
    internal static void EnterModelDecode() => _modelDecodeEntries++;
    internal static void EnterLargeAllocation() => _largeAllocationEntries++;

    public bool Allowed { get; }
    public GltfDecodeCapacity? Capacity { get; }
    public long PeakBytes { get; }
    public long OwnedBytes { get; }
    public long ExpandedVertexReferences { get; }
    public long IndexCount { get; }
    public int PartCount { get; }
    public IReadOnlyList<GltfAccessorFact> Accessors { get; }
    public IReadOnlyList<ImageDecodeInfo> Images { get; }

    GltfDecodeAdmission(bool allowed, long peak, long owned = 0, long corners = 0, long indices = 0,
        int parts = 0, GltfAccessorFact[]? accessors = null, ImageDecodeInfo[]? images = null, GltfDecodeCapacity? capacity = null)
    {
        Allowed = allowed; Capacity = capacity; PeakBytes = peak; OwnedBytes = owned;
        ExpandedVertexReferences = corners; IndexCount = indices; PartCount = parts;
        Accessors = Array.AsReadOnly(accessors ?? []); Images = Array.AsReadOnly(images ?? []);
    }

    public static GltfDecodeAdmission Measure(ReadOnlyMemory<byte> glb, GltfDecodeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        using var demandScope = DemandDiagnostics.Enter(budget, "GltfDecodeAdmission.Measure");
        try { return Scan(glb, budget); }
        catch (GltfDecodeRefusal ex) when (ex.Status == GltfDecodeStatus.CapacityExceeded) { return new(false, long.MaxValue, capacity: ex.Capacity); }
        catch (OverflowException) { return new(false, long.MaxValue); }
        catch (ImageDecodeRefusal ex) { throw new GltfDecodeRefusal((GltfDecodeStatus)ex.Status, ex.Message); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException or FormatException)
        { throw new GltfDecodeRefusal(GltfDecodeStatus.Invalid, "Invalid binary glTF metadata: " + ex.Message); }
    }

    static GltfDecodeAdmission Scan(ReadOnlyMemory<byte> glb, GltfDecodeBudget budget)
    {
        ReadOnlyMemory<byte> jsonBytes = JsonChunk(glb, out ReadOnlyMemory<byte> binary);
        long tokens = 0;
        var reader = new Utf8JsonReader(jsonBytes.Span);
        while (reader.Read()) tokens = checked(tokens + 1);
        long metadata = checked(512L + glb.Length * 2L + jsonBytes.Length * 6L + tokens * 32);
        long remaining = budget.RemainingBytes;
        if (DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.MaxPeakBytes.1", "MaxPeakBytes", budget.Limits.MaxPeakBytes - remaining, metadata, budget.Limits.MaxPeakBytes, metadata > remaining, CapacityDemandLifetime.Transient, allowanceSource: budget.Limits)) return new(false, metadata, capacity: new(nameof(GltfDecodeLimits.MaxPeakBytes),
            (decimal)budget.Limits.MaxPeakBytes - remaining + metadata, budget.Limits.MaxPeakBytes));
        using var json = JsonDocument.Parse(jsonBytes);
        JsonElement root = json.RootElement;
        if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0")
            throw new GltfDecodeRefusal(GltfDecodeStatus.Unsupported, "Only glTF 2.0 is supported.");
        if (root.TryGetProperty("extensionsRequired", out var extensions) && extensions.GetArrayLength() != 0)
            throw new GltfDecodeRefusal(GltfDecodeStatus.Unsupported, "Required glTF extensions are unsupported.");
        foreach (string member in new[] { "buffers", "images" })
            if (root.TryGetProperty(member, out var list))
                foreach (var item in list.EnumerateArray())
                    if (item.TryGetProperty("uri", out var uri) && !(uri.GetString()?.StartsWith("data:", StringComparison.Ordinal) ?? false))
                        throw new GltfDecodeRefusal(GltfDecodeStatus.Unsupported, "External glTF resources are not verified.");
        var buffers = root.GetProperty("buffers");
        for (int i = 0; i < buffers.GetArrayLength(); i++)
        {
            long bytes = buffers[i].GetProperty("byteLength").GetInt64();
            if (bytes < 0) throw Invalid("Negative buffer size.");
            ReadOnlyMemory<byte> content = Buffer(root, i, binary);
            if (bytes > content.Length) throw Invalid("Buffer exceeds its verified bytes.");
        }
        var views = root.GetProperty("bufferViews");
        foreach (var view in views.EnumerateArray())
        {
            int length = view.GetProperty("byteLength").GetInt32(), offset = Int(view, "byteOffset", 0);
            var buffer = buffers[view.GetProperty("buffer").GetInt32()];
            if (length < 0 || offset < 0 || (long)offset + length > buffer.GetProperty("byteLength").GetInt64())
                throw Invalid("Buffer view exceeds its buffer.");
        }
        var accessors = root.GetProperty("accessors");
        var facts = new GltfAccessorFact[accessors.GetArrayLength()];
        for (int i = 0; i < facts.Length; i++)
        {
            var a = accessors[i];
            int count = a.GetProperty("count").GetInt32();
            if (count < 0) throw Invalid("Negative accessor count.");
            if (DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.5999.1", budget.Limits.MaxVertices >= budget.Limits.MaxIndices ? "MaxVertices" : "MaxIndices", 0, count, Math.Max(budget.Limits.MaxVertices, budget.Limits.MaxIndices), count > Math.Max(budget.Limits.MaxVertices, budget.Limits.MaxIndices), CapacityDemandLifetime.Transient))
                throw Refused(budget.Limits.MaxVertices >= budget.Limits.MaxIndices ? nameof(GltfDecodeLimits.MaxVertices) : nameof(GltfDecodeLimits.MaxIndices), count, Math.Max(budget.Limits.MaxVertices, budget.Limits.MaxIndices));
            if (a.TryGetProperty("sparse", out _)) throw new GltfDecodeRefusal(GltfDecodeStatus.Unsupported, "Sparse accessors are unsupported.");
            int bytes = a.GetProperty("componentType").GetInt32() switch
            { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw Invalid("Invalid accessor component type.") };
            int components = a.GetProperty("type").GetString() switch
            { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" or "MAT2" => 4, "MAT3" => 9, "MAT4" => 16, _ => throw Invalid("Invalid accessor type.") };
            if (DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.7136.1", components == 1 ? "MaxIndices" : "MaxVertices", 0, count, components == 1 ? budget.Limits.MaxIndices : budget.Limits.MaxVertices, count > (components == 1 ? budget.Limits.MaxIndices : budget.Limits.MaxVertices), CapacityDemandLifetime.Transient))
                throw Refused(components == 1 ? nameof(GltfDecodeLimits.MaxIndices) : nameof(GltfDecodeLimits.MaxVertices), count, components == 1 ? budget.Limits.MaxIndices : budget.Limits.MaxVertices);
            var view = views[a.GetProperty("bufferView").GetInt32()];
            int element = checked(bytes * components), stride = Int(view, "byteStride", element), offset = Int(a, "byteOffset", 0);
            if (stride < element || offset < 0 || (count > 0 && checked((long)(count - 1) * stride + element + offset) > view.GetProperty("byteLength").GetInt64()))
                throw Invalid("Accessor exceeds its buffer view.");
            facts[i] = new(count, bytes, components, stride);
        }
        int nodeCount = root.TryGetProperty("nodes", out var nodes) ? nodes.GetArrayLength() : 0;
        long corners = 0, indices = 0;
        var materials = new HashSet<int>();
        var meshes = root.GetProperty("meshes");
        for (int m = 0; m < meshes.GetArrayLength(); m++)
        {
            int instances = 0;
            if (nodeCount > 0)
                foreach (var node in nodes.EnumerateArray()) if (Int(node, "mesh", -1) == m) instances++;
            instances = Math.Max(1, instances);
            foreach (var primitive in meshes[m].GetProperty("primitives").EnumerateArray())
            {
                if (Int(primitive, "mode", 4) != 4) throw new GltfDecodeRefusal(GltfDecodeStatus.Unsupported, "Strict rigid decoding requires triangles.");
                int position = primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                var positionFact = Accessor(facts, position);
                if (DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.MaxVertices.1", "MaxVertices", 0, positionFact.Count, budget.Limits.MaxVertices, positionFact.Count > budget.Limits.MaxVertices, CapacityDemandLifetime.Transient, allowanceSource: budget.Limits)) throw Refused(nameof(GltfDecodeLimits.MaxVertices), positionFact.Count, budget.Limits.MaxVertices);
                if (positionFact.Components != 3 || accessors[position].GetProperty("componentType").GetInt32() != 5126)
                    throw Invalid("POSITION must be floating VEC3.");
                long n = primitive.TryGetProperty("indices", out var index) ? Accessor(facts, index.GetInt32()).Count : positionFact.Count;
                if (n % 3 != 0) throw Invalid("Triangle corner count is not divisible by three.");
                corners = checked(corners + n * instances); indices = checked(indices + n * instances);
                materials.Add(Int(primitive, "material", -1));
            }
        }
        if (corners == 0) throw Invalid("glTF has no triangles.");
        if (DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.MaxVertices.2", "MaxVertices", 0, corners, budget.Limits.MaxVertices, corners > budget.Limits.MaxVertices, CapacityDemandLifetime.Transient, allowanceSource: budget.Limits)) throw Refused(nameof(GltfDecodeLimits.MaxVertices), corners, budget.Limits.MaxVertices);
        if (DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.MaxIndices.1", "MaxIndices", 0, indices, budget.Limits.MaxIndices, indices > budget.Limits.MaxIndices, CapacityDemandLifetime.Transient, allowanceSource: budget.Limits)) throw Refused(nameof(GltfDecodeLimits.MaxIndices), indices, budget.Limits.MaxIndices);
        long pixels = 0, encodedImages = 0;
        var images = new List<ImageDecodeInfo>();
        foreach (int id in materials)
        {
            if (id < 0) continue;
            var material = root.GetProperty("materials")[id];
            if (material.TryGetProperty("emissiveTexture", out _) || material.TryGetProperty("occlusionTexture", out _) ||
                (material.TryGetProperty("extensions", out var materialExtensions) && materialExtensions.EnumerateObject().Any()))
                throw new GltfDecodeRefusal(GltfDecodeStatus.Unsupported,
                    "Strict material decoding supports only core base colour, normal and metallic-roughness textures.");
            AddImage(material, "normalTexture");
            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
            { AddImage(pbr, "baseColorTexture"); AddImage(pbr, "metallicRoughnessTexture"); }
        }
        long owned = checked(corners * 64 + indices * 4 + pixels * 4 + 256L * materials.Count + images.Count * 24L);
        // Covers geometric list growth, weld-table resize overlap and the defensive/scaled output copy.
        long weldPerCorner = checked(2L * Unsafe.SizeOf<MeshCorner>() + 2L * (48 + 16 + 8 + 1 + 20) +
            3L * (Unsafe.SizeOf<MeshWeldKey>() + 20) + 128);
        long arrayHeaders = checked(24L * (64 + 32L * materials.Count + 10L * facts.Length + nodeCount + 3L * images.Count));
        long peak = checked(metadata + owned + corners * (256 + weldPerCorner) + indices * 16 +
            nodeCount * 64L + pixels * 8 + encodedImages * 2 + arrayHeaders);
        remaining = budget.RemainingBytes;
        return new(!DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.MaxPeakBytes.2", "MaxPeakBytes", budget.Limits.MaxPeakBytes - remaining, peak, budget.Limits.MaxPeakBytes, !(peak <= remaining), CapacityDemandLifetime.Transient, allowanceSource: budget.Limits), peak, owned, corners, indices, materials.Count, facts, images.ToArray(),
            !DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.MaxPeakBytes.3", "MaxPeakBytes", budget.Limits.MaxPeakBytes - remaining, peak, budget.Limits.MaxPeakBytes, !(peak <= remaining), CapacityDemandLifetime.Transient, allowanceSource: budget.Limits) ? null : new(nameof(GltfDecodeLimits.MaxPeakBytes), (decimal)budget.Limits.MaxPeakBytes - remaining + peak, budget.Limits.MaxPeakBytes));

        void AddImage(JsonElement material, string channel)
        {
            if (!material.TryGetProperty(channel, out var texture)) return;
            int textureId = texture.GetProperty("index").GetInt32();
            var image = root.GetProperty("images")[root.GetProperty("textures")[textureId].GetProperty("source").GetInt32()];
            ReadOnlyMemory<byte> content;
            if (image.TryGetProperty("uri", out var uri)) content = DataUri(uri.GetString()!);
            else
            {
                var view = views[image.GetProperty("bufferView").GetInt32()];
                content = Buffer(root, view.GetProperty("buffer").GetInt32(), binary)
                    .Slice(Int(view, "byteOffset", 0), view.GetProperty("byteLength").GetInt32());
            }
            ImageDecodeInfo info = ImageDecodeAdmission.Probe(content.Span);
            long p = checked((long)info.Width * info.Height);
            if (DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.MaxImagePixels.1", "MaxImagePixels", 0, p, budget.Limits.MaxImagePixels, p > budget.Limits.MaxImagePixels, CapacityDemandLifetime.Transient, allowanceSource: budget.Limits)) throw Refused(nameof(GltfDecodeLimits.MaxImagePixels), p, budget.Limits.MaxImagePixels);
            if (DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.MaxImageBufferPixels.1", "MaxImageBufferPixels", 0, p, Array.MaxLength / 4, p > Array.MaxLength / 4, CapacityDemandLifetime.Transient)) throw Refused("MaxImageBufferPixels", p, Array.MaxLength / 4);
            if (DemandDiagnostics.Check(budget.DemandIdentity, "GltfDecodeAdmission.MaxImages.1", "MaxImages", images.Count, 1, budget.Limits.MaxImages, images.Count >= budget.Limits.MaxImages, CapacityDemandLifetime.Transient, allowanceSource: budget.Limits)) throw Refused(nameof(GltfDecodeLimits.MaxImages), (long)images.Count + 1, budget.Limits.MaxImages);
            pixels = checked(pixels + p); encodedImages = checked(encodedImages + content.Length);
            images.Add(info);
        }
    }

    internal static ReadOnlyMemory<byte> JsonChunk(ReadOnlyMemory<byte> glb, out ReadOnlyMemory<byte> binary)
    {
        var span = glb.Span;
        if (span.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x46546c67 ||
            BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(span[8..]) != span.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(span[16..]) != 0x4e4f534a) throw Invalid("Invalid GLB header.");
        uint json = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        if (json > span.Length - 20 || json % 4 != 0) throw Invalid("Invalid GLB JSON chunk length.");
        int offset = checked(20 + (int)json);
        binary = ReadOnlyMemory<byte>.Empty;
        if (offset != span.Length)
        {
            if (span.Length - offset < 8 || BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]) != 0x004e4942)
                throw Invalid("Invalid GLB binary chunk.");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
            if (length != span.Length - offset - 8) throw Invalid("Invalid GLB binary length.");
            binary = glb.Slice(offset + 8, (int)length);
        }
        return glb.Slice(20, (int)json);
    }

    static ReadOnlyMemory<byte> Buffer(JsonElement root, int index, ReadOnlyMemory<byte> binary)
    {
        var buffer = root.GetProperty("buffers")[index];
        if (buffer.TryGetProperty("uri", out var uri)) return DataUri(uri.GetString()!);
        if (index != 0) throw Invalid("Only the first buffer can use the GLB binary chunk.");
        return binary;
    }
    static ReadOnlyMemory<byte> DataUri(string uri)
    {
        int comma = uri.IndexOf(',');
        if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.Ordinal))
            throw new GltfDecodeRefusal(GltfDecodeStatus.Unsupported, "Only base64 embedded resources are supported.");
        return Convert.FromBase64String(uri[(comma + 1)..]);
    }
    static GltfAccessorFact Accessor(GltfAccessorFact[] facts, int index)
    {
        if ((uint)index >= (uint)facts.Length) throw Invalid("Primitive accessor reference is outside the accessor array.");
        return facts[index];
    }
    static int Int(JsonElement item, string name, int fallback) => item.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
    static GltfDecodeRefusal Invalid(string detail) => new(GltfDecodeStatus.Invalid, detail);
    static GltfDecodeRefusal Refused(string name, decimal requested, long allowance)
    { var capacity = new GltfDecodeCapacity(name, requested, allowance); return new(GltfDecodeStatus.CapacityExceeded, capacity.Message) { Capacity = capacity }; }
}
