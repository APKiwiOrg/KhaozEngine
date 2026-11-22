using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static partial class AttachmentGlb
{
    const string LiftDirectionAttribute = "_LIFT_DIRECTION";
    const string LiftBearingAttribute = "_LIFT_BEARING";
    const float LiftDirectionTolerance = 0.001f;
    const float CoincidentLiftTolerance = 0.0001f;

    /// <summary>Validates a skinned attachment GLB and reads its per-vertex lift basis without a renderer.</summary>
    /// <exception cref="AttachmentSkinnedGeometryDataException">An attribute is missing or has an invalid shape or
    /// value, or vertices at bitwise equal positions disagree on lift data.</exception>
    public static LiftBasis LiftBasis(ReadOnlyMemory<byte> glb, string sourceName)
    {
        LiftBasis? basis = null;
        Validate(glb, sourceName, container =>
        {
            ReadSkinned(container, chainJoints: null);
            basis = new LiftBasisReader(container.Root, new AttachmentGlbAccessors(container.Root, container.Binary)).Read();
        });
        return basis!;
    }

    sealed class LiftBasisReader(JsonElement root, AttachmentGlbAccessors accessors)
    {
        readonly List<LiftBasisVertex> vertices = [];
        readonly Dictionary<int, (int Part, int VertexCount)> materialParts = [];
        readonly Dictionary<(uint X, uint Y, uint Z), CoincidentLiftRange> coincident = [];

        internal LiftBasis Read()
        {
            int meshIndex = 0;
            foreach (JsonElement mesh in Array(root, "meshes"))
            {
                int primitiveIndex = 0;
                foreach (JsonElement primitive in Array(mesh, "primitives"))
                    ReadPrimitive(primitive, $"$.meshes[{meshIndex}].primitives[{primitiveIndex++}]");
                meshIndex++;
            }
            return new LiftBasis(vertices);
        }

        void ReadPrimitive(JsonElement primitive, string primitivePath)
        {
            JsonElement attributes = primitive.GetProperty("attributes");
            string path = primitivePath + ".attributes";
            var positions = accessors.Read(attributes.GetProperty("POSITION"), path + ".POSITION", "VEC3", path + ".POSITION");
            var directions = ReadAttribute(attributes, path, LiftDirectionAttribute, "VEC3", positions.Count);
            var bearings = ReadAttribute(attributes, path, LiftBearingAttribute, "SCALAR", positions.Count);
            int material = -1;
            if (primitive.TryGetProperty("material", out JsonElement reference))
            {
                string materialPath = primitivePath + ".material";
                material = AttachmentGlbAccessors.Index(reference, materialPath, materialPath);
                AttachmentGlbAccessors.Entry(root, "materials", material, materialPath);
            }
            if (!materialParts.TryGetValue(material, out var group)) group = (materialParts.Count, 0);

            Span<float> directionValues = stackalloc float[3];
            Span<float> bearingValues = stackalloc float[1];
            for (int vertex = 0; vertex < positions.Count; vertex++)
            {
                directions.ReadElement(vertex, directionValues);
                bearings.ReadElement(vertex, bearingValues);
                var direction = new Vector3(directionValues[0], directionValues[1], directionValues[2]);
                float length = direction.Length();
                if (!float.IsFinite(length) || MathF.Abs(length - 1) > LiftDirectionTolerance)
                    throw LiftRefusal(AttachmentSkinnedGeometryRefusal.LiftDirectionInvalid, path, LiftDirectionAttribute,
                        "Direction must be finite and within 0.001 of unit length.", vertex);
                float bearing = bearingValues[0];
                if (!float.IsFinite(bearing) || bearing < 0)
                    throw LiftRefusal(AttachmentSkinnedGeometryRefusal.LiftBearingInvalid, path, LiftBearingAttribute,
                        "Bearing must be finite and nonnegative.", vertex);

                var value = new LiftBasisVertex(direction, bearing, group.Part, group.VertexCount + vertex);
                var position = PositionBits(positions.RawElement(vertex));
                if (coincident.TryGetValue(position, out CoincidentLiftRange range))
                    range.Include(value, vertices.Count, path);
                else
                    range = new CoincidentLiftRange(value, vertices.Count);
                coincident[position] = range;
                vertices.Add(value);
            }
            materialParts[material] = (group.Part, group.VertexCount + positions.Count);
        }

        AttachmentGlbAccessors.Accessor ReadAttribute(JsonElement attributes, string path, string name, string type, int count)
        {
            if (!attributes.TryGetProperty(name, out JsonElement reference))
                throw LiftRefusal(AttachmentSkinnedGeometryRefusal.LiftAttributeMissing, path, name, "A lifting primitive requires this attribute.");

            string attributePath = path + "." + name;
            int index = AttachmentGlbAccessors.Index(reference, attributePath, attributePath);
            JsonElement accessor = AttachmentGlbAccessors.Entry(root, "accessors", index, attributePath);
            // Check the lift-specific contract before interpreting the byte range with an invalid shape or count.
            if (!accessor.TryGetProperty("type", out JsonElement shape) || shape.ValueKind != JsonValueKind.String
                || shape.GetString() != type || !HasInteger(accessor, "componentType", 5126)
                || (accessor.TryGetProperty("normalized", out JsonElement normalized) && normalized.ValueKind != JsonValueKind.False)
                || !HasInteger(accessor, "count", count))
                throw LiftRefusal(AttachmentSkinnedGeometryRefusal.LiftAttributeShapeMismatch, path, name,
                    $"Expected an unnormalized {type} FLOAT accessor with {count} elements.");
            return accessors.Read(reference, attributePath, type, attributePath);
        }

        static bool HasInteger(JsonElement accessor, string property, int expected) =>
            accessor.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int actual) && actual == expected;

        static (uint X, uint Y, uint Z) PositionBits(ReadOnlySpan<byte> position) => position.IsEmpty ? default
            : (BinaryPrimitives.ReadUInt32LittleEndian(position),
                BinaryPrimitives.ReadUInt32LittleEndian(position[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(position[8..]));
    }

    static AttachmentSkinnedGeometryDataException LiftRefusal(AttachmentSkinnedGeometryRefusal reason,
        string attributesPath, string name, string detail, int? vertex = null, int? otherVertex = null)
    {
        string path = attributesPath + "." + name;
        return new AttachmentSkinnedGeometryDataException(reason, $"{path}: {detail}")
        {
            JsonPath = path,
            AttributeName = name,
            VertexIndex = vertex,
            OtherVertexIndex = otherVertex,
        };
    }

    // The per-component extrema check every pair of splits. Comparing only with the first vertex
    // would admit values on opposite sides of its tolerance.
    struct CoincidentLiftRange(LiftBasisVertex first, int index)
    {
        LiftComponentRange x = new(first.Direction.X, index);
        LiftComponentRange y = new(first.Direction.Y, index);
        LiftComponentRange z = new(first.Direction.Z, index);
        LiftComponentRange bearing = new(first.Bearing, index);

        internal void Include(LiftBasisVertex value, int vertex, string path)
        {
            int other = x.Include(value.Direction.X, vertex);
            if (other < 0) other = y.Include(value.Direction.Y, vertex);
            if (other < 0) other = z.Include(value.Direction.Z, vertex);
            if (other >= 0) throw Mismatch(path, LiftDirectionAttribute, vertex, other);

            other = bearing.Include(value.Bearing, vertex);
            if (other >= 0) throw Mismatch(path, LiftBearingAttribute, vertex, other);
        }

        static AttachmentSkinnedGeometryDataException Mismatch(string path, string attribute, int vertex, int other) =>
            LiftRefusal(AttachmentSkinnedGeometryRefusal.CoincidentLiftDataMismatch, path, attribute,
                $"Coincident flattened vertices {other} and {vertex} have different lift data.", vertex, other);
    }

    struct LiftComponentRange(float first, int index)
    {
        float min = first, max = first;
        int minVertex = index, maxVertex = index;

        internal int Include(float value, int vertex)
        {
            if (value - min > CoincidentLiftTolerance) return minVertex;
            if (max - value > CoincidentLiftTolerance) return maxVertex;
            if (value < min)
            {
                min = value;
                minVertex = vertex;
            }
            if (value > max)
            {
                max = value;
                maxVertex = vertex;
            }
            return -1;
        }
    }
}
