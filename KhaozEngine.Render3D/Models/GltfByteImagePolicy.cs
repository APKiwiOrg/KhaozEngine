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
        if (json.IsEmpty) return; // Not a recognisable GLB container: the model reader diagnoses it.

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

    // Bytes that are not a recognisable GLB 2 container go to the model reader, which diagnoses them. Inside a
    // recognised container every chunk framing fault refuses here, because the reader accepts some of them, such as a
    // partial chunk header at the end of the declared length, and the image policy would then be skipped.
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

        // Bytes beyond the declared GLB length are left to the model reader.
        ReadOnlyMemory<byte> chunks = glb[fileHeaderSize..(int)declaredLength];
        ReadOnlyMemory<byte> json = default;
        while (!chunks.IsEmpty)
        {
            if (chunks.Length < chunkHeaderSize)
                throw new InvalidOperationException("The GLB ends inside a chunk header.");
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunks.Span);
            if (chunkLength % 4 != 0 || chunkLength > chunks.Length - chunkHeaderSize)
                throw new InvalidOperationException("A GLB chunk is not 4-byte aligned or runs past the declared length.");

            if (BinaryPrimitives.ReadUInt32LittleEndian(chunks.Span[4..]) == jsonChunkType)
            {
                if (!json.IsEmpty || chunkLength == 0)
                    throw new InvalidOperationException("A GLB needs exactly one non-empty JSON chunk.");
                json = chunks.Slice(chunkHeaderSize, (int)chunkLength);
            }
            chunks = chunks[(chunkHeaderSize + (int)chunkLength)..];
        }
        if (json.IsEmpty) throw new InvalidOperationException("A GLB needs exactly one non-empty JSON chunk.");
        return json;
    }
}
