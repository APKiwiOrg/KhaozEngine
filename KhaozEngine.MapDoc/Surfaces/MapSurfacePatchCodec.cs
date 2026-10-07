using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Canonical, bounded UTF-8 patch payload. Packed integer arrays are little-endian.</summary>
public static class MapSurfacePatchCodec
{
    public const int MaxEncodedBytes = 1_048_576;

    public static byte[] Encode(MapSurfacePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        IReadOnlyList<string> errors = patch.ValidateLocal();
        if (errors.Count != 0) throw new MapDocumentException(errors[0]);
        using var output = new BoundedOutput();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("key"); WriteKey(writer, patch.Key);
            writer.WriteNumber("cellMinX", patch.CellMinX); writer.WriteNumber("cellMinZ", patch.CellMinZ);
            writer.WriteNumber("width", patch.Width); writer.WriteNumber("depth", patch.Depth);
            byte[] heights = new byte[patch.Heights.Length * 4];
            for (int i = 0; i < patch.Heights.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(heights.AsSpan(i * 4), patch.Heights[i]);
            writer.WriteBase64String("heights", heights);
            byte[] cells = new byte[patch.Cells.Length * 8];
            for (int i = 0; i < patch.Cells.Length; i++)
            {
                MapSurfaceCell cell = patch.Cells[i]; Span<byte> b = cells.AsSpan(i * 8, 8);
                BinaryPrimitives.WriteUInt16LittleEndian(b, cell.Underlay);
                BinaryPrimitives.WriteUInt16LittleEndian(b[2..], cell.Overlay);
                b[4] = (byte)cell.Cut; b[5] = cell.Rotation; b[6] = (byte)cell.Flags; b[7] = (byte)cell.Topology;
            }
            writer.WriteBase64String("cells", cells);
            byte[] presence = new byte[patch.Presence.Length * 8];
            for (int i = 0; i < patch.Presence.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(presence.AsSpan(i * 8), patch.Presence[i]);
            writer.WriteBase64String("presence", presence);
            writer.WriteStartArray("cornerDependencies");
            foreach (MapCornerDependency d in patch.CornerDependencies.OrderBy(d => d.CornerZ).ThenBy(d => d.CornerX))
            {
                writer.WriteStartObject(); writer.WriteNumber("cornerX", d.CornerX); writer.WriteNumber("cornerZ", d.CornerZ);
                writer.WriteStartObject("owner"); writer.WritePropertyName("patch"); WriteKey(writer, d.Owner.Patch);
                writer.WriteStartObject("address");
                writer.WriteString("x", Decimal(d.Owner.Address.X)); writer.WriteString("z", Decimal(d.Owner.Address.Z));
                writer.WriteNumber("d", d.Owner.Address.Denominator);
                writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("edgeSubdivisions");
            foreach (MapEdgeSubdivision e in patch.EdgeSubdivisions.OrderBy(e => e.CellZ).ThenBy(e => e.CellX).ThenBy(e => e.Edge))
            {
                writer.WriteStartObject(); writer.WriteNumber("cellX", e.CellX); writer.WriteNumber("cellZ", e.CellZ);
                writer.WriteNumber("edge", (byte)e.Edge); writer.WriteNumber("segments", e.Segments); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("records"); writer.WriteEndArray(); writer.WriteEndObject();
        }
        return output.Bytes();
    }

    public static MapSurfacePatch Decode(ReadOnlySpan<byte> bytes, MapPatchKey expectedKey)
    {
        if (bytes.Length > MaxEncodedBytes) throw Limit();
        try
        {
            // The parser's storage is bounded by the input cap. Size-based payload arrays follow dimension/length checks.
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            if (reader.Read()) throw new MapDocumentException("trailing patch payload");
            JsonElement root = document.RootElement;
            Members(root, "key", "cellMinX", "cellMinZ", "width", "depth", "heights", "cells", "presence",
                "cornerDependencies", "edgeSubdivisions", "records");
            MapPatchKey key = ReadKey(root.GetProperty("key"));
            if (key != expectedKey) throw new MapDocumentException("patch key does not match expected key");
            int minX = root.GetProperty("cellMinX").GetInt32(), minZ = root.GetProperty("cellMinZ").GetInt32();
            int width = root.GetProperty("width").GetInt32(), depth = root.GetProperty("depth").GetInt32();
            if (!MapSurfacePatch.ValidRectangle(minX, minZ, width, depth))
                throw new MapDocumentException("invalid patch width, depth or cell minimum");
            int cornerCount = (width + 1) * (depth + 1), cellCount = width * depth, wordCount = (cellCount + 63) / 64;
            byte[] heightBytes = Base64(root.GetProperty("heights"), cornerCount * 4, "heights");
            byte[] cellBytes = Base64(root.GetProperty("cells"), cellCount * 8, "cells");
            byte[] presenceBytes = Base64(root.GetProperty("presence"), wordCount * 8, "presence");
            JsonElement dependencies = Array(root.GetProperty("cornerDependencies"), cornerCount, "cornerDependencies");
            JsonElement edges = Array(root.GetProperty("edgeSubdivisions"), cellCount * 4, "edgeSubdivisions");
            _ = Array(root.GetProperty("records"), 0, "records");
            var patch = new MapSurfacePatch
            {
                Key = key,
                CellMinX = minX,
                CellMinZ = minZ,
                Width = width,
                Depth = depth,
                Heights = new int[cornerCount],
                Cells = new MapSurfaceCell[cellCount],
                Presence = new ulong[wordCount],
            };
            for (int i = 0; i < cornerCount; i++) patch.Heights[i] = BinaryPrimitives.ReadInt32LittleEndian(heightBytes.AsSpan(i * 4));
            for (int i = 0; i < cellCount; i++)
            {
                ReadOnlySpan<byte> b = cellBytes.AsSpan(i * 8, 8);
                patch.Cells[i] = new(BinaryPrimitives.ReadUInt16LittleEndian(b), BinaryPrimitives.ReadUInt16LittleEndian(b[2..]),
                    (MapOverlayCut)b[4], b[5], (MapCellFlags)b[6], (MapCellTopology)b[7]);
            }
            for (int i = 0; i < wordCount; i++) patch.Presence[i] = BinaryPrimitives.ReadUInt64LittleEndian(presenceBytes.AsSpan(i * 8));
            foreach (JsonElement d in dependencies.EnumerateArray())
            {
                Members(d, "cornerX", "cornerZ", "owner"); JsonElement owner = d.GetProperty("owner");
                Members(owner, "patch", "address"); JsonElement address = owner.GetProperty("address");
                Members(address, "x", "z", "d");
                patch.CornerDependencies.Add(new(d.GetProperty("cornerX").GetInt32(), d.GetProperty("cornerZ").GetInt32(),
                    new(ReadKey(owner.GetProperty("patch")), MapLatticeAddress.Create(Long(address.GetProperty("x")),
                        Long(address.GetProperty("z")), address.GetProperty("d").GetInt32()))));
            }
            foreach (JsonElement e in edges.EnumerateArray())
            {
                Members(e, "cellX", "cellZ", "edge", "segments");
                patch.EdgeSubdivisions.Add(new(e.GetProperty("cellX").GetInt32(), e.GetProperty("cellZ").GetInt32(),
                    (MapCellEdge)e.GetProperty("edge").GetByte(), e.GetProperty("segments").GetInt32()));
            }
            IReadOnlyList<string> errors = patch.ValidateLocal();
            if (errors.Count != 0) throw new MapDocumentException(errors[0]);
            return patch;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new MapDocumentException("invalid surface patch payload", ex);
        }
    }

    static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    static long Long(JsonElement value)
    {
        string? text = value.GetString();
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long result) || text != Decimal(result))
            throw new MapDocumentException("invalid decimal int64 string");
        return result;
    }
    static void WriteKey(Utf8JsonWriter writer, MapPatchKey key)
    {
        if (string.IsNullOrWhiteSpace(key.SurfaceId)) throw new MapDocumentException("surface id is required");
        if (key.SurfaceId.Length > MaxEncodedBytes) throw Limit();
        writer.WriteStartObject(); writer.WriteString("surfaceId", key.SurfaceId);
        writer.WriteString("slotX", Decimal(key.SlotX)); writer.WriteString("slotZ", Decimal(key.SlotZ)); writer.WriteEndObject();
    }
    static MapPatchKey ReadKey(JsonElement key)
    {
        Members(key, "surfaceId", "slotX", "slotZ");
        string? id = key.GetProperty("surfaceId").GetString();
        if (string.IsNullOrWhiteSpace(id)) throw new MapDocumentException("surface id is required");
        return new(id, Long(key.GetProperty("slotX")), Long(key.GetProperty("slotZ")));
    }
    static void Members(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new MapDocumentException("expected patch object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal))
                throw new MapDocumentException($"unknown member '{property.Name}'");
            if (!seen.Add(property.Name)) throw new MapDocumentException($"duplicate member '{property.Name}'");
        }
        foreach (string name in names)
            if (!seen.Contains(name)) throw new MapDocumentException($"missing member '{name}'");
    }
    static JsonElement Array(JsonElement value, int maxCount, string name)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maxCount)
            throw new MapDocumentException($"invalid {name} length");
        return value;
    }
    static byte[] Base64(JsonElement value, int expectedBytes, string name)
    {
        if (value.ValueKind != JsonValueKind.String) throw new MapDocumentException($"invalid {name} base64");
        string text = value.GetString()!;
        if (text.Length != ((expectedBytes + 2) / 3) * 4) throw new MapDocumentException($"invalid {name} length");
        var bytes = new byte[expectedBytes];
        if (!Convert.TryFromBase64String(text, bytes, out int written) || written != expectedBytes)
            throw new MapDocumentException($"invalid {name} base64 length");
        return bytes;
    }
    static MapDocumentException Limit() => new($"patch payload exceeds {MaxEncodedBytes} bytes");

    sealed class BoundedOutput : Stream
    {
        readonly MemoryStream _stream = new();
        public byte[] Bytes() => _stream.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MaxEncodedBytes - _stream.Length) throw Limit();
            _stream.Write(buffer);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
}
