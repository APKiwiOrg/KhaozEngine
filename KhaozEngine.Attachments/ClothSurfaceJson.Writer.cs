using System;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static partial class ClothSurfaceJson
{
    /// <summary>Validate the offline inputs and write the bytes to hash for the attachment-set reference.</summary>
    public static byte[] Write(ClothSurfaceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.CreateRig("cloth surface");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", file.MaterialRender.IsDefault ? FormatVersion : MaterialFormatVersion);
            writer.WriteString("meshSha256", file.MeshSha256);
            writer.WriteNumber("sourceVertices", file.SourceVertexCount);
            writer.WriteNumber("sourceTriangles", file.SourceTriangleCount);
            ClothMaterial m = file.Material;
            writer.WriteStartArray("material");
            Number(m.Density); Number(m.WarpStiffness); Number(m.WeftStiffness); Number(m.ShearStiffness);
            Number(m.BendModulus); Number(m.DampingSeconds); Number(m.HalfThickness); Number(m.StaticFriction);
            Number(m.KineticFriction); Number(m.NormalAirDrag);
            writer.WriteEndArray();

            writer.WriteStartArray("nodes");
            for (int i = 0; i < file.RestPositions.Length; i++)
            {
                writer.WriteStartArray();
                Vector(file.RestPositions[i]);
                Number(file.MaterialCoordinates[i].X);
                Number(file.MaterialCoordinates[i].Y);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("triangles");
            foreach (ClothTriangle t in file.Triangles) Indices(t.A, t.B, t.C);
            writer.WriteEndArray();
            writer.WriteStartArray("hinges");
            foreach (ClothRestHinge h in file.Hinges.AsSpan())
            {
                writer.WriteStartArray();
                Index(h.First); Index(h.Second); Number(h.NaturalAngle);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("pins");
            foreach (ClothPinSource pin in file.Pins)
            {
                writer.WriteStartArray();
                Index(pin.Node);
                Point(pin.Point);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            Embeddings("render", file.Render);

            writer.WriteStartArray("fallback");
            foreach (ClothSourcePoint point in file.Fallback) Point(point);
            writer.WriteEndArray();
            if (!file.MaterialRender.IsDefault) Embeddings("materialRender", file.MaterialRender);
            writer.WriteEndObject();

            void Number(float value) => writer.WriteNumberValue(value == 0 ? 0 : value);
            void Index(int index) => writer.WriteNumberValue(index);
            void Vector(Vector3 v) { Number(v.X); Number(v.Y); Number(v.Z); }
            void Indices(int a, int b, int c)
            {
                writer.WriteStartArray();
                Index(a); Index(b); Index(c);
                writer.WriteEndArray();
            }

            void Point(ClothSourcePoint point)
            {
                writer.WriteStartArray();
                Index(point.Mesh); Index(point.Primitive); Index(point.Element);
                if (point.Barycentric is { } weights)
                {
                    Vector(weights); Number(point.SignedOffset);
                }
                writer.WriteEndArray();
            }

            void Embeddings(string name, ImmutableArray<ClothRenderEmbedding> entries)
            {
                writer.WriteStartArray(name);
                foreach (ClothRenderEmbedding entry in entries)
                {
                    writer.WriteStartArray();
                    Index(entry.Vertex.Mesh); Index(entry.Vertex.Primitive); Index(entry.Vertex.Vertex); Index(entry.Triangle);
                    Vector(entry.Barycentric); Number(entry.SignedOffset);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
        }
        return stream.ToArray();
    }
}
