using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Builds one geometric extrusion normal for every mesh vertex. Vertices at the exact same position share an
    /// angle-weighted normal, so authored hard edges and UV seams stay closed when an inverted hull expands.
    /// </summary>
    internal static class OutlineNormalBuilder
    {
        const float MinLengthSquared = 1e-20f;

        internal static Vector3[] Build(ModelVertex[] vertices, uint[] indices)
            => Build(new[] { vertices }, new[] { indices })[0];

        internal static Vector3[][] Build(IReadOnlyList<GltfMeshPart> parts)
        {
            var vertices = new ModelVertex[parts.Count][];
            var indices = new uint[parts.Count][];
            for (int i = 0; i < parts.Count; i++)
            {
                vertices[i] = parts[i].Mesh.Vertices;
                indices[i] = parts[i].Mesh.Indices32;
            }
            return Build(vertices, indices);
        }

        static Vector3[][] Build(IReadOnlyList<ModelVertex[]> verticesByMesh, IReadOnlyList<uint[]> indicesByMesh)
        {
            var groups = new List<Group>();
            var byPosition = new Dictionary<Vector3, int>();
            var vertexGroups = new int[verticesByMesh.Count][];

            for (int mesh = 0; mesh < verticesByMesh.Count; mesh++)
            {
                ModelVertex[] vertices = verticesByMesh[mesh];
                vertexGroups[mesh] = new int[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 position = vertices[i].Position;
                    if (!byPosition.TryGetValue(position, out int group))
                    {
                        group = groups.Count;
                        byPosition.Add(position, group);
                        groups.Add(default);
                    }
                    vertexGroups[mesh][i] = group;

                    Vector3 source = vertices[i].Normal;
                    if (!TryNormalize(source, out Vector3 unitSource)) continue;
                    Group current = groups[group];
                    current.SourceSum += unitSource;
                    if (!current.HasSource)
                    {
                        current.FirstSource = unitSource;
                        current.HasSource = true;
                    }
                    groups[group] = current;
                }
            }

            for (int mesh = 0; mesh < verticesByMesh.Count; mesh++)
            {
                ModelVertex[] vertices = verticesByMesh[mesh];
                uint[] indices = indicesByMesh[mesh];
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    uint ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
                    if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length) continue;

                    Vector3 a = vertices[ia].Position;
                    Vector3 b = vertices[ib].Position;
                    Vector3 c = vertices[ic].Position;
                    if (!TryNormalize(Vector3.Cross(b - a, c - a), out Vector3 face)) continue;

                    AddCorner(groups, vertexGroups[mesh][ia], b - a, c - a, face);
                    AddCorner(groups, vertexGroups[mesh][ib], c - b, a - b, face);
                    AddCorner(groups, vertexGroups[mesh][ic], a - c, b - c, face);
                }
            }

            var groupNormals = new Vector3[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                Group group = groups[i];
                if (TryNormalize(group.WeightedSum, out Vector3 normal)
                    || TryNormalize(group.SourceSum, out normal))
                {
                    groupNormals[i] = normal;
                }
                else if (group.HasSource)
                {
                    groupNormals[i] = group.FirstSource;
                }
                else if (group.HasFace)
                {
                    groupNormals[i] = group.FirstFace;
                }
                else
                {
                    groupNormals[i] = Vector3.UnitY;
                }
            }

            var result = new Vector3[verticesByMesh.Count][];
            for (int mesh = 0; mesh < result.Length; mesh++)
            {
                result[mesh] = new Vector3[verticesByMesh[mesh].Length];
                for (int i = 0; i < result[mesh].Length; i++)
                    result[mesh][i] = groupNormals[vertexGroups[mesh][i]];
            }
            return result;
        }

        static void AddCorner(List<Group> groups, int groupIndex, Vector3 edgeA, Vector3 edgeB, Vector3 face)
        {
            if (!TryNormalize(edgeA, out Vector3 unitA) || !TryNormalize(edgeB, out Vector3 unitB)) return;
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(unitA, unitB), -1f, 1f));
            if (!float.IsFinite(angle) || angle <= 0f) return;

            Group group = groups[groupIndex];
            group.WeightedSum += face * angle;
            if (!group.HasFace)
            {
                group.FirstFace = face;
                group.HasFace = true;
            }
            groups[groupIndex] = group;
        }

        static bool TryNormalize(Vector3 value, out Vector3 normal)
        {
            float lengthSquared = value.LengthSquared();
            if (!float.IsFinite(lengthSquared) || lengthSquared <= MinLengthSquared)
            {
                normal = default;
                return false;
            }
            normal = value / MathF.Sqrt(lengthSquared);
            return IsFinite(normal);
        }

        static bool IsFinite(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        struct Group
        {
            internal Vector3 WeightedSum;
            internal Vector3 SourceSum;
            internal Vector3 FirstSource;
            internal Vector3 FirstFace;
            internal bool HasSource;
            internal bool HasFace;
        }
    }
}
