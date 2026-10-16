using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using SharpGLTF.Schema2;

namespace KhaozEngine.Render3D;

public static partial class GltfLoader
{
    /// <summary>Decode exact host bytes and validate their explicit coverage topology before GPU allocation.</summary>
    public static IReadOnlyList<GltfSkinnedMeshPart> LoadSkinnedPartsWithMaterials(ReadOnlyMemory<byte> glb,
        string identity, HostCoverageMap hostCoverageMap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(hostCoverageMap);
        byte[] owned = glb.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(owned));
        if (hash != hostCoverageMap.HostSha256) throw new InvalidDataException($"Coverage asset '{identity}': host GLB identity mismatch.");
        return TranslateCoverageErrors(identity, () =>
        {
            ModelRoot root = LoadModel(owned, identity);
            var collector = new CoverageImportCollector();
            var decoded = DecodeSkinned(root, identity, splitByMaterial: true, collector);
            var source = collector.Build(identity, hash, decoded.Select(pair => pair.Mesh).ToArray());
            HostCoveragePart.Validate(hostCoverageMap, source);
            var parts = new List<GltfSkinnedMeshPart>();
            for (int slice = 0; slice < decoded.Count; slice++)
            {
                var (mesh, material) = decoded[slice];
                var covered = new SkinnedGltfMesh(mesh.Vertices, mesh.Indices32, mesh.InverseBind, mesh.RestPose, mesh.Skeleton)
                { Coverage = HostCoveragePart.CreateSlice(hostCoverageMap, source, slice, mesh) };
                parts.Add(new GltfSkinnedMeshPart(covered, ReadMaterialMaps(material)));
            }
            return parts;
        });
    }

    /// <summary>Decode every displayed rigid node occurrence through the ordinary material importer.</summary>
    public static CoverageSourceTopology LoadCoverageSource(ReadOnlyMemory<byte> glb, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        byte[] owned = glb.ToArray();
        return TranslateCoverageErrors(identity, () =>
        {
            var collector = new CoverageImportCollector();
            BuildParts(LoadModel(owned, identity), identity, collector);
            return collector.Build(identity, Convert.ToHexStringLower(SHA256.HashData(owned)));
        });
    }

    static T TranslateCoverageErrors<T>(string identity, Func<T> read)
    {
        try { return TranslateModelErrors(identity, read); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException)
        {
            throw new InvalidDataException($"Coverage asset '{identity}': {error.Message}", error);
        }
    }

    sealed class CoverageImportCollector
    {
        readonly List<Vector3> positions = new();
        readonly List<CoverageSourceVertex> vertices = new();
        readonly List<CoverageSourceTriangle> triangles = new();
        readonly List<CoverageSkinInfluence> influences = new();
        readonly Dictionary<(int, int), CoverageSourceOccurrence> occurrences = new();
        readonly Dictionary<int, CoverageMaterialSlice> materials = new();
        Skin? skin;

        internal void VerifySkin(ModelRoot root, Mesh mesh, string identity)
        {
            var candidates = root.LogicalNodes.Where(node => node.Mesh == mesh && node.Skin != null).Select(node => node.Skin!).Distinct().ToArray();
            Skin? candidate = candidates.Length == 0 ? root.LogicalSkins.FirstOrDefault() : candidates[0];
            if (candidates.Length > 1 || candidate == null || skin != null && !ReferenceEquals(skin, candidate))
                throw new InvalidDataException($"Coverage asset '{identity}': one compatible skin is required.");
            skin = candidate;
        }

        internal int AddSkinned(Mesh mesh, MeshPrimitive primitive, int slice, int sliceVertex, IList<Vector3> sourcePositions, IList<Vector4> joints, IList<Vector4> weights)
        {
            int start = positions.Count;
            Material(primitive, slice);
            occurrences.TryAdd((mesh.LogicalIndex, -1), new(mesh.LogicalIndex, -1, Matrix4x4.Identity));
            int ordinal = mesh.Primitives.ToList().IndexOf(primitive);
            for (int i = 0; i < sourcePositions.Count; i++)
            {
                positions.Add(sourcePositions[i]);
                influences.Add(new(joints[i], weights[i]));
                vertices.Add(new(mesh.LogicalIndex, ordinal, i, -1, slice, sliceVertex + i));
            }
            return start;
        }

        internal void AddTriangle(Mesh mesh, MeshPrimitive primitive, int triangle, int slice, int start, int a, int b, int c) =>
            triangles.Add(new(mesh.LogicalIndex, mesh.Primitives.ToList().IndexOf(primitive), triangle, -1, slice, start + a, start + b, start + c));

        internal void AddRigid(Mesh mesh, MeshPrimitive primitive, int node, int slice, int triangle, int a, int b, int c,
            List<MeshCorner> corners, Matrix4x4 world)
        {
            int start = positions.Count;
            int corner = corners.Count - 3;
            Material(primitive, slice);
            occurrences.TryAdd((mesh.LogicalIndex, node), new(mesh.LogicalIndex, node, world));
            int ordinal = mesh.Primitives.ToList().IndexOf(primitive);
            int[] sourceIndices = { a, b, c };
            for (int i = 0; i < 3; i++)
            {
                positions.Add(corners[corner + i].Position);
                vertices.Add(new(mesh.LogicalIndex, ordinal, sourceIndices[i], node, slice, corner + i));
            }
            triangles.Add(new(mesh.LogicalIndex, ordinal, triangle, node, slice, start, start + 1, start + 2));
        }

        internal void CompleteRigidSlice(int slice, GltfMesh mesh)
        {
            for (int i = 0; i < vertices.Count; i++)
            {
                CoverageSourceVertex vertex = vertices[i];
                if (vertex.MaterialSlice != slice) continue;
                int displayed = checked((int)mesh.Indices32[vertex.SliceVertex]);
                vertices[i] = vertex with { SliceVertex = displayed };
                positions[i] = mesh.Vertices[displayed].Position;
            }
        }

        void Material(MeshPrimitive primitive, int slice) => materials.TryAdd(slice, new(slice, primitive.Material?.LogicalIndex ?? -1,
            primitive.Material == null || primitive.Material.Alpha == AlphaMode.OPAQUE ? 0 : 1));

        internal CoverageSourceTopology Build(string identity, string hash, SkinnedGltfMesh[]? meshes = null)
        {
            SkinnedVertex[] skinVertices = meshes == null ? Array.Empty<SkinnedVertex>()
                : vertices.Select(vertex => meshes[vertex.MaterialSlice].Vertices[vertex.SliceVertex]).ToArray();
            return new CoverageSourceTopology(identity, hash, positions.ToArray(), vertices, triangles, skinVertices,
                influences.ToArray(), occurrences.Values, materials.Values);
        }
    }
}
