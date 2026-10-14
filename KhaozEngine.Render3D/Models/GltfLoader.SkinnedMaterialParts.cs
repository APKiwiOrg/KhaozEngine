using System;
using System.Collections.Generic;
using SharpGLTF.Schema2;

namespace KhaozEngine.Render3D
{
    public static partial class GltfLoader
    {
        /// <summary>Load directly indexed skinned geometry and decoded baseColor/normal/metallicRoughness maps
        /// as one part per source material, in stable first-use order walking meshes then primitives.
        /// Primitives without a material form their own untextured part. All parts share one skeleton and
        /// skin-order inverse binds and rest poses. Mesh-node transforms are not baked into skinned vertices.</summary>
        public static IReadOnlyList<GltfSkinnedMeshPart> LoadSkinnedPartsWithMaterials(string path) =>
            BuildSkinnedParts(LoadModel(path), path);

        /// <summary>Load skinned material parts from binary glTF bytes without temporary files or source
        /// mutation. Uses the same geometry, material ordering and decoded maps as the path overload.
        /// The caller's <paramref name="identity"/> names the asset in validation diagnostics.</summary>
        /// <remarks>Attachment consumers must apply their embedded skinned asset policy before import.</remarks>
        public static IReadOnlyList<GltfSkinnedMeshPart> LoadSkinnedPartsWithMaterials(ReadOnlyMemory<byte> glb, string identity) =>
            BuildSkinnedParts(LoadModel(glb, identity), identity);

        static IReadOnlyList<GltfSkinnedMeshPart> BuildSkinnedParts(ModelRoot root, string identity)
        {
            var meshes = DecodeSkinned(root, identity, splitByMaterial: true);
            var parts = new List<GltfSkinnedMeshPart>(meshes.Count);
            foreach (var (mesh, material) in meshes)
                parts.Add(new GltfSkinnedMeshPart(mesh, ReadMaterialMaps(material)));
            return parts;
        }
    }
}
