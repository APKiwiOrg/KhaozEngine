using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static class AttachmentGlb
{
    const uint JsonChunk = 0x4e4f534a;
    const uint BinaryChunk = 0x004e4942;

    public static void ValidateRigidEmbedded(ReadOnlyMemory<byte> glb, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        try
        {
            ReadOnlySpan<byte> bytes = glb.Span;
            if (bytes.Length < 20 || Read(bytes, 0) != 0x46546c67 || Read(bytes, 4) != 2 || Read(bytes, 8) != (uint)bytes.Length)
                throw new InvalidDataException("Invalid GLB 2 header or declared length.");
            int offset = 12;
            ReadOnlyMemory<byte> json = default;
            int binaryLength = -1;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 8)
                    throw new InvalidDataException("Truncated GLB chunk header.");
                uint length = Read(bytes, offset);
                uint type = Read(bytes, offset + 4);
                if (length % 4 != 0 || length > (uint)(bytes.Length - offset - 8))
                    throw new InvalidDataException("Invalid GLB chunk length.");
                int start = offset + 8;
                if (offset == 12)
                {
                    if (type != JsonChunk || length == 0)
                        throw new InvalidDataException("The first GLB chunk must contain JSON.");
                    json = glb.Slice(start, (int)length);
                }
                else if (type == BinaryChunk && binaryLength == -1)
                    binaryLength = (int)length;
                else
                    throw new InvalidDataException("Unexpected or duplicate GLB chunk.");
                offset = start + (int)length;
            }
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("GLB JSON must be an object.");
            RefuseDuplicates(root, "root");
            if (!root.TryGetProperty("asset", out JsonElement asset) || asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.String
                || version.GetString() != "2.0")
                throw new InvalidDataException("GLB asset.version must be 2.0.");

            foreach (JsonElement buffer in Array(root, "buffers"))
            {
                RefuseUri(buffer, "buffer");
                if (!buffer.TryGetProperty("byteLength", out JsonElement declared) || !declared.TryGetUInt32(out uint size)
                    || binaryLength < 0 || size > (uint)binaryLength || (uint)binaryLength - size > 3)
                    throw new InvalidDataException("The embedded buffer does not match its BIN chunk.");
            }
            if (root.TryGetProperty("buffers", out JsonElement buffers) && buffers.GetArrayLength() > 1)
                throw new InvalidDataException("A GLB attachment supports one embedded buffer.");
            foreach (JsonElement image in Array(root, "images"))
                RefuseUri(image, "image");
            foreach (JsonElement _ in Array(root, "skins"))
                throw new InvalidDataException("Skin data is not rigid geometry.");
            foreach (JsonElement _ in Array(root, "animations"))
                throw new InvalidDataException("Animated geometry is not a rigid attachment.");
            foreach (JsonElement node in Array(root, "nodes"))
                if (node.TryGetProperty("skin", out _) || node.TryGetProperty("weights", out _))
                    throw new InvalidDataException("Node skin or morph data is not rigid geometry.");
            foreach (JsonElement mesh in Array(root, "meshes"))
            {
                if (mesh.TryGetProperty("weights", out _))
                    throw new InvalidDataException("Mesh morph data is not rigid geometry.");
                foreach (JsonElement primitive in Array(mesh, "primitives"))
                {
                    if (primitive.TryGetProperty("targets", out _))
                        throw new InvalidDataException("Morph targets are not rigid geometry.");
                    if (primitive.TryGetProperty("attributes", out JsonElement attributes))
                        foreach (JsonProperty attribute in attributes.EnumerateObject())
                            if (attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal) || attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal))
                                throw new InvalidDataException("Skin vertex attributes are not rigid geometry.");
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{sourceName}: {error.Message}", error);
        }
    }

    static uint Read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    static IEnumerable<JsonElement> Array(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
            yield break;
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"GLB '{name}' must be an array.");
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"GLB '{name}' entries must be objects.");
            yield return item;
        }
    }

    static void RefuseUri(JsonElement value, string context)
    {
        if (value.TryGetProperty("uri", out _))
            throw new InvalidDataException($"GLB {context} URI is refused. Embed resources in the BIN chunk.");
    }

    static void RefuseDuplicates(JsonElement value, string context)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"GLB {context}: duplicate '{property.Name}'.");
                RefuseDuplicates(property.Value, context + "." + property.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray())
                RefuseDuplicates(item, context + "[]");
    }
}
