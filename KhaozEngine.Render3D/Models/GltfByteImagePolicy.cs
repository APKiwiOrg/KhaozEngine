using System;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Unicode;

namespace KhaozEngine.Render3D;

internal static class GltfByteImagePolicy
{
    internal static void Validate(ReadOnlyMemory<byte> glb, string identity)
    {
        ReadOnlyMemory<byte> json = FindJsonChunk(glb);
        if (json.IsEmpty) return; // Let the model reader diagnose unrecognized or truncated containers.

        // JSON tokenization alone can leave string bytes undecoded. Validate the entire original chunk.
        if (!Utf8.IsValid(json.Span))
            throw new JsonException("The GLB JSON chunk contains invalid UTF-8.");
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        // SharpGLTF appends repeated image arrays to one logical list and resolves even unused images.
        int index = 0;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!property.NameEquals("images"u8) || property.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement image in property.Value.EnumerateArray())
            {
                if (image.ValueKind == JsonValueKind.Object && image.TryGetProperty("uri", out JsonElement uri)
                    && uri.ValueKind == JsonValueKind.String
                    && !uri.GetString()!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GltfImportDataException(GltfImportRefusal.ExternalImageInByteImport,
                        $"glTF asset '{identity}' image {index} names a resource outside the supplied GLB bytes.")
                    { Identity = identity, ImageIndex = index };
                }
                index++;
            }
        }
    }

    static ReadOnlyMemory<byte> FindJsonChunk(ReadOnlyMemory<byte> glb)
    {
        const int fileHeaderSize = 12;
        const int chunkHeaderSize = 8;
        const uint glbMagic = 0x46546c67;
        const uint jsonChunkType = 0x4e4f534a;

        ReadOnlySpan<byte> header = glb.Span;
        if (header.Length < fileHeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(header) != glbMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 2)
            return default;

        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (declaredLength < fileHeaderSize || declaredLength > glb.Length) return default;

        // Leave unknown chunks and bytes beyond the declared GLB length to the model reader.
        ReadOnlyMemory<byte> chunks = glb[fileHeaderSize..(int)declaredLength];
        ReadOnlyMemory<byte> json = default;
        while (!chunks.IsEmpty)
        {
            if (chunks.Length < chunkHeaderSize) return default;
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunks.Span);
            if (chunkLength == 0 || chunkLength % 4 != 0 || chunkLength > chunks.Length - chunkHeaderSize)
                return default;

            if (BinaryPrimitives.ReadUInt32LittleEndian(chunks.Span[4..]) == jsonChunkType)
            {
                if (!json.IsEmpty) return default;
                json = chunks.Slice(chunkHeaderSize, (int)chunkLength);
            }
            chunks = chunks[(chunkHeaderSize + (int)chunkLength)..];
        }
        return json;
    }
}
