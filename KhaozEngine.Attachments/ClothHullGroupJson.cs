using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal static class ClothHullGroupJson
{
    internal static ImmutableArray<ClothHullGroup> Read(JsonElement part, uint partId, int partIndex)
    {
        if (!part.TryGetProperty("clothHulls", out JsonElement value)) return [];
        string path = $"$.parts[{partIndex}].clothHulls";
        value = StrictAttachmentJson.ArrayValue(value, path, Refuse);
        int count = value.GetArrayLength();
        if (count is < 1 or > AttachmentSetDefinition.MaxColliders)
            throw Refuse(StrictAttachmentJson.Fault.ValueShape, path, "Invalid cloth hull group count.");
        var groups = ImmutableArray.CreateBuilder<ClothHullGroup>(count);
        foreach (JsonElement group in value.EnumerateArray())
        {
            string row = $"{path}[{groups.Count}]";
            StrictAttachmentJson.Members(group, row, Refuse, "group", "capsules", "vertices", "faces");
            int id = Index(Member("group"), row + ".group");
            JsonElement capsules = Array("capsules", AttachmentSetDefinition.MaxColliders);
            JsonElement vertices = Array("vertices", ClothSupportHull.VertexLimit);
            JsonElement faces = Array("faces", 2 * ClothSupportHull.VertexLimit - 4);
            var indices = ImmutableArray.CreateBuilder<int>(capsules.GetArrayLength());
            foreach (JsonElement capsule in capsules.EnumerateArray()) indices.Add(Index(capsule, row + ".capsules"));
            var points = new Vector3[vertices.GetArrayLength()];
            for (int i = 0; i < points.Length; i++)
                points[i] = AttachmentColliderJson.ReadPoint(vertices[i], row + $".vertices[{i}]", Refuse);
            var triangles = new ClothTriangle[faces.GetArrayLength()];
            for (int i = 0; i < triangles.Length; i++)
            {
                string at = row + $".faces[{i}]";
                JsonElement triangle = StrictAttachmentJson.ArrayValue(faces[i], at, Refuse);
                if (triangle.GetArrayLength() != 3)
                    throw Refuse(StrictAttachmentJson.Fault.ValueShape, at, "A hull face needs three vertices.");
                triangles[i] = new ClothTriangle(Index(triangle[0], at), Index(triangle[1], at), Index(triangle[2], at));
            }
            try { groups.Add(new ClothHullGroup(id, ClothSupportHull.Create(points, triangles), indices.MoveToImmutable())); }
            catch (ArgumentException error)
            {
                string at = error switch
                {
                    ClothSupportHullException { FaceIndex: int face } => row + $".faces[{face}]",
                    ClothSupportHullException { VertexIndex: int vertex } => row + $".vertices[{vertex}]",
                    _ => row,
                };
                throw new AttachmentSetDataException(AttachmentSetRefusal.ClothHullGroupInvalid, error.Message, error)
                { PartId = partId, PartIndex = partIndex, JsonPath = at };
            }

            JsonElement Member(string name) => StrictAttachmentJson.Required(group, name, row, Refuse);
            JsonElement Array(string name, int maximum)
            {
                string at = row + "." + name;
                JsonElement array = StrictAttachmentJson.ArrayValue(Member(name), at, Refuse);
                if (array.GetArrayLength() > maximum)
                    throw Refuse(StrictAttachmentJson.Fault.ValueShape, at, "Cloth hull element limit exceeded.");
                return array;
            }
        }
        return groups.MoveToImmutable();

        int Index(JsonElement element, string at) => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int index)
            ? index : throw Refuse(StrictAttachmentJson.Fault.ValueShape, at, "Expected an integer.");
        Exception Refuse(StrictAttachmentJson.Fault fault, string at, string message, string? property = null) =>
            new AttachmentSetDataException(AttachmentSetJson.StrictReason(fault), message)
            { PartId = partId, PartIndex = partIndex, JsonPath = at, PropertyName = property };
    }

    internal static void Write(Utf8JsonWriter writer, ImmutableArray<ClothHullGroup> groups)
    {
        if (groups.IsDefaultOrEmpty) return;
        writer.WriteStartArray("clothHulls");
        foreach (ClothHullGroup group in groups)
        {
            writer.WriteStartObject();
            writer.WriteNumber("group", group.FeatureGroup);
            writer.WriteStartArray("capsules");
            foreach (int index in group.CapsuleIndices) writer.WriteNumberValue(index);
            writer.WriteEndArray();
            writer.WriteStartArray("vertices");
            foreach (Vector3 vertex in group.Hull.Vertices)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(vertex.X == 0 ? 0 : vertex.X);
                writer.WriteNumberValue(vertex.Y == 0 ? 0 : vertex.Y);
                writer.WriteNumberValue(vertex.Z == 0 ? 0 : vertex.Z);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("faces");
            foreach (ClothTriangle face in group.Hull.Faces)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(face.A);
                writer.WriteNumberValue(face.B);
                writer.WriteNumberValue(face.C);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
