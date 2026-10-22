using System;
using System.Collections.Generic;
using System.IO;
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
            LoadSkinnedPartsWithMaterials(path, SkinningProfile.LinearBlend);

        /// <summary>Load material parts with one explicit deformation profile shared by every part.</summary>
        public static IReadOnlyList<GltfSkinnedMeshPart> LoadSkinnedPartsWithMaterials(string path, SkinningProfile profile) =>
            ValidateSkinningImport(path, profile, () => BuildSkinnedParts(LoadModel(path), path, profile));

        /// <summary>Load skinned material parts from binary glTF bytes without temporary files or source
        /// mutation. Uses the same geometry, material ordering and decoded maps as the path overload.
        /// The caller's <paramref name="identity"/> names the asset in validation diagnostics.</summary>
        /// <remarks>Attachment consumers must apply their embedded skinned asset policy before import.</remarks>
        /// <exception cref="InvalidDataException">The glTF is malformed. The message names
        /// <paramref name="identity"/> and the importer's diagnostic is the inner exception.</exception>
        /// <exception cref="InvalidOperationException">The glTF has no skin, exceeds the bone cap or fails index
        /// validation.</exception>
        public static IReadOnlyList<GltfSkinnedMeshPart> LoadSkinnedPartsWithMaterials(ReadOnlyMemory<byte> glb, string identity) =>
            LoadSkinnedPartsWithMaterials(glb, identity, SkinningProfile.LinearBlend);

        /// <summary>Load binary glTF material parts with the same explicit profile as the path overload.</summary>
        public static IReadOnlyList<GltfSkinnedMeshPart> LoadSkinnedPartsWithMaterials(
            ReadOnlyMemory<byte> glb, string identity, SkinningProfile profile) =>
            TranslateModelErrors(identity, () => ValidateSkinningImport(identity, profile,
                () => BuildSkinnedParts(LoadModel(glb, identity), identity, profile)));

        static IReadOnlyList<GltfSkinnedMeshPart> BuildSkinnedParts(ModelRoot root, string identity, SkinningProfile profile)
        {
            var meshes = DecodeSkinned(root, identity, splitByMaterial: true, profile);
            var parts = new List<GltfSkinnedMeshPart>(meshes.Count);
            foreach (var (mesh, material) in meshes)
                parts.Add(new GltfSkinnedMeshPart(mesh, ReadMaterialMaps(material)));
            return parts;
        }
    }
}
