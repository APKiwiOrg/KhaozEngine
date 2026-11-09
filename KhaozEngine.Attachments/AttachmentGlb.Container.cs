using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static partial class AttachmentGlb
{
    const uint JsonChunk = 0x4e4f534a;
    const uint BinaryChunk = 0x004e4942;

    static void Validate(ReadOnlyMemory<byte> glb, string sourceName, Action<Container> policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        try
        {
            using var container = new Container(glb);
            policy(container);
        }
        catch (MalformedDataException error)
        {
            if (error is IAttachmentDataException sourced) throw sourced.WithSource(sourceName);
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw SourceFailure(error, sourceName);
        }
    }

    static AttachmentGlbContainerDataException SourceFailure(Exception error, string sourceName) => new(
        error is JsonException ? AttachmentGlbContainerRefusal.JsonMalformed : AttachmentGlbContainerRefusal.ValueShapeInvalid,
        $"{sourceName}: {error.Message}", error)
    { SourceName = sourceName };

    internal sealed class Container : IDisposable
    {
        readonly JsonDocument document;
        internal JsonElement Root => document.RootElement;
        internal ReadOnlyMemory<byte> Binary { get; }

        internal Container(ReadOnlyMemory<byte> glb, bool validateImages = true)
        {
            ReadOnlySpan<byte> bytes = glb.Span;
            if (bytes.Length < 20 || Read(bytes, 0) != 0x46546c67 || Read(bytes, 4) != 2 || Read(bytes, 8) != (uint)bytes.Length)
                throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.HeaderInvalid, "Invalid GLB 2 header or declared length.")
                {
                    ByteLength = bytes.Length,
                    DeclaredLength = bytes.Length >= 12 ? Read(bytes, 8) : null,
                    Version = bytes.Length >= 8 ? Read(bytes, 4) : null,
                };
            int offset = 12;
            ReadOnlyMemory<byte> json = default;
            int binaryLength = -1;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 8)
                    throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.ChunkHeaderTruncated, "Truncated GLB chunk header.")
                    {
                        Offset = offset,
                        AvailableBytes = bytes.Length - offset,
                    };
                uint length = Read(bytes, offset);
                uint type = Read(bytes, offset + 4);
                if (length % 4 != 0 || length > (uint)(bytes.Length - offset - 8))
                    throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.ChunkLengthInvalid, "Invalid GLB chunk length.")
                    {
                        Offset = offset,
                        DeclaredLength = length,
                        AvailableBytes = bytes.Length - offset - 8,
                    };
                int start = offset + 8;
                if (offset == 12)
                {
                    if (type != JsonChunk || length == 0)
                        throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.FirstChunkNotJson, "The first GLB chunk must contain JSON.")
                        {
                            ChunkType = type,
                            ChunkLength = length,
                        };
                    json = glb.Slice(start, (int)length);
                }
                else if (type == BinaryChunk && binaryLength == -1)
                {
                    binaryLength = (int)length;
                    Binary = glb.Slice(start, binaryLength);
                }
                else
                    throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.UnexpectedChunk, "Unexpected or duplicate GLB chunk.")
                    {
                        Offset = offset,
                        ChunkType = type,
                    };
                offset = start + (int)length;
            }
            document = JsonDocument.Parse(json, new JsonDocumentOptions());
            try
            {
                ValidateResources(binaryLength, validateImages);
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }

        void ValidateResources(int binaryLength, bool validateImages)
        {
            JsonElement root = Root;
            if (root.ValueKind != JsonValueKind.Object)
                throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.JsonRootNotObject, "GLB JSON must be an object.");
            RefuseDuplicates(root, "root");
            if (!root.TryGetProperty("asset", out JsonElement asset) || asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.String
                || version.GetString() != "2.0")
                throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.AssetVersionUnsupported, "GLB asset.version must be 2.0.")
                {
                    AssetVersion = asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("version", out JsonElement assetVersion) && assetVersion.ValueKind == JsonValueKind.String ? assetVersion.GetString() : null,
                };
            foreach (JsonElement buffer in Array(root, "buffers"))
            {
                RefuseUri(buffer, "buffer");
                if (!buffer.TryGetProperty("byteLength", out JsonElement declared) || !declared.TryGetUInt32(out uint size)
                    || binaryLength < 0 || size > (uint)binaryLength || (uint)binaryLength - size > 3)
                    throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.EmbeddedBufferLengthMismatch, "The embedded buffer does not match its BIN chunk.")
                    {
                        DeclaredLength = declared.ValueKind == JsonValueKind.Number && declared.TryGetUInt32(out uint declaredSize) ? declaredSize : null,
                        BinaryLength = binaryLength,
                    };
            }
            if (root.TryGetProperty("buffers", out JsonElement buffers) && buffers.GetArrayLength() > 1)
                throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.MultipleEmbeddedBuffers, "A GLB attachment supports one embedded buffer.")
                {
                    BufferCount = buffers.GetArrayLength(),
                };
            if (validateImages)
                foreach (JsonElement image in Array(root, "images"))
                    RefuseUri(image, "image");
        }

        public void Dispose() => document.Dispose();
    }

    static uint Read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    static IEnumerable<JsonElement> Array(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
            yield break;
        if (value.ValueKind != JsonValueKind.Array)
            throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.PropertyNotArray, $"GLB '{name}' must be an array.")
            {
                PropertyName = name,
            };
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.ArrayEntryNotObject, $"GLB '{name}' entries must be objects.")
                {
                    PropertyName = name,
                    EntryIndex = index,
                };
            yield return item;
            index++;
        }
    }

    static void RefuseUri(JsonElement value, string context)
    {
        if (value.TryGetProperty("uri", out _))
            throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.ExternalResourceUri, $"GLB {context} URI is refused. Embed resources in the BIN chunk.")
            {
                ResourceKind = context,
            };
    }

    static void RefuseDuplicates(JsonElement value, string context, string jsonPath = "$")
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal.DuplicateJsonProperty, $"GLB {context}: duplicate '{property.Name}'.")
                    {
                        JsonPath = jsonPath,
                        PropertyName = property.Name,
                    };
                RefuseDuplicates(property.Value, context + "." + property.Name, AttachmentJsonPath.Property(jsonPath, property.Name));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
                RefuseDuplicates(item, context + "[]", jsonPath + $"[{index++}]");
        }
    }
}
