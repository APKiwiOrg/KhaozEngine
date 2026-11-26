using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Attachments;

/// <summary>Reads and writes the canonical versioned UTF-8 cloth surface companion.</summary>
public static partial class ClothSurfaceJson
{
    public const int FormatVersion = 1;

    public static ClothSurfaceFile Parse(ReadOnlyMemory<byte> utf8, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        return ParseChecked(utf8, sourceName, null, out _);
    }

    internal static ClothSurfaceFile ParseChecked(ReadOnlyMemory<byte> utf8, string sourceName, uint? partId, out ClothRig rig)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 8 });
            var reader = new Reader(document.RootElement, sourceName, partId);
            ClothSurfaceFile file = reader.Read();
            rig = file.CreateRig(sourceName, partId);
            return file;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new ClothSurfaceDataException(ClothSurfaceRefusal.Json, sourceName, "The cloth sidecar contains invalid UTF-8 JSON.", error)
            { PartId = partId, JsonPath = "$" };
        }
    }

    sealed class Reader(JsonElement root, string sourceName, uint? partId)
    {
        internal ClothSurfaceFile Read()
        {
            StrictAttachmentJson.Members(root, "$", Refuse, "formatVersion", "meshSha256", "sourceVertices", "sourceTriangles",
                "material", "nodes", "triangles", "hinges", "pins", "render", "fallback");
            if (Index(Member("formatVersion"), "$.formatVersion") != FormatVersion)
                throw new ClothSurfaceDataException(ClothSurfaceRefusal.Version, sourceName, "Unsupported cloth surface format.")
                { PartId = partId, JsonPath = "$.formatVersion" };
            string mesh = StrictAttachmentJson.Text(Member("meshSha256"), "$.meshSha256", Refuse);
            int sourceVertices = Index(Member("sourceVertices"), "$.sourceVertices");
            int sourceTriangles = Index(Member("sourceTriangles"), "$.sourceTriangles");
            var nodes = Rows("nodes", ClothSurfaceFile.NodeLimit, 5, (row, path) =>
                (Position: Vector(row, 0, path), Material: new Vector2(Number(row, 3, path), Number(row, 4, path))));
            var positions = ImmutableArray.CreateBuilder<Vector3>(nodes.Length);
            var coordinates = ImmutableArray.CreateBuilder<Vector2>(nodes.Length);
            foreach (var node in nodes)
            {
                positions.Add(node.Position);
                coordinates.Add(node.Material);
            }

            return new ClothSurfaceFile(mesh, sourceVertices, sourceTriangles, Material(), positions.MoveToImmutable(),
                coordinates.MoveToImmutable(), Rows("triangles", ClothSurfaceFile.TriangleLimit, 3, Triangle),
                Rows("hinges", 3 * ClothSurfaceFile.TriangleLimit / 2, 3, (r, p) =>
                    new ClothRestHinge(Index(r[0], p), Index(r[1], p), Number(r, 2, p))),
                Rows("pins", ClothSurfaceFile.NodeLimit, 2, (r, p) =>
                    new ClothPinSource(Index(r[0], p), Point(r[1], p))),
                Rows("render", ClothSurfaceFile.SourceVertexLimit, 8, (r, p) =>
                    new ClothRenderEmbedding(new ClothSourceVertex(Index(r[0], p), Index(r[1], p), Index(r[2], p)),
                        Index(r[3], p), Vector(r, 4, p), Number(r, 7, p))),
                Rows("fallback", ClothSurfaceFile.NodeLimit, null, Point));
        }

        ClothMaterial Material()
        {
            const string path = "$.material";
            JsonElement value = Member("material");
            Row(value, path, 10);
            return new ClothMaterial(Number(value, 0, path), Number(value, 1, path), Number(value, 2, path),
                Number(value, 3, path), Number(value, 4, path), Number(value, 5, path), Number(value, 6, path),
                Number(value, 7, path), Number(value, 8, path), Number(value, 9, path));
        }

        ClothSourcePoint Point(JsonElement r, string p)
        {
            StrictAttachmentJson.ArrayValue(r, p, Refuse);
            bool vertex = r.GetArrayLength() == 3;
            Row(r, p, vertex ? 3 : 7);
            return new ClothSourcePoint(Index(r[0], p), Index(r[1], p), Index(r[2], p),
                vertex ? null : Vector(r, 3, p), vertex ? 0 : Number(r, 6, p));
        }

        ClothTriangle Triangle(JsonElement row, string path) =>
            new(Index(row[0], path), Index(row[1], path), Index(row[2], path));

        Vector3 Vector(JsonElement row, int first, string path) =>
            new(Number(row, first, path), Number(row, first + 1, path), Number(row, first + 2, path));

        float Number(JsonElement row, int index, string path) => StrictAttachmentJson.Number(row[index], path, Refuse);

        int Index(JsonElement value, string path) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int index)
            ? index : throw Refuse(StrictAttachmentJson.Fault.ValueShape, path, "Expected a 32-bit integer.");

        JsonElement Member(string name) => StrictAttachmentJson.Required(root, name, "$", Refuse);

        ImmutableArray<T> Rows<T>(string name, int maximum, int? length, Func<JsonElement, string, T> convert)
        {
            string path = "$." + name;
            JsonElement array = StrictAttachmentJson.ArrayValue(Member(name), path, Refuse);
            int count = array.GetArrayLength();
            if (count > maximum)
                throw new ClothSurfaceDataException(ClothSurfaceRefusal.Count, sourceName, "Cloth element limit exceeded.")
                { PartId = partId, JsonPath = path };
            var rows = ImmutableArray.CreateBuilder<T>(count);
            foreach (JsonElement item in array.EnumerateArray())
            {
                string rowPath = $"{path}[{rows.Count}]";
                if (length is int fields) Row(item, rowPath, fields);
                rows.Add(convert(item, rowPath));
            }
            return rows.MoveToImmutable();
        }

        void Row(JsonElement value, string path, int fields)
        {
            if (StrictAttachmentJson.ArrayValue(value, path, Refuse).GetArrayLength() != fields)
                throw Refuse(StrictAttachmentJson.Fault.ValueShape, path, $"Expected a tuple of {fields} values.");
        }

        Exception Refuse(StrictAttachmentJson.Fault fault, string path, string message, string? property = null)
        {
            int open = path.LastIndexOf('[');
            int? index = open >= 0 && int.TryParse(path.AsSpan(open + 1, path.Length - open - 2), out int parsed) ? parsed : null;
            return new ClothSurfaceDataException(ClothSurfaceRefusal.Json, sourceName, message)
            { PartId = partId, JsonPath = path, ElementIndex = index };
        }
    }
}
