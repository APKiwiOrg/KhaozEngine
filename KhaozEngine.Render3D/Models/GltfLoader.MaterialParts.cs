using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SharpGLTF.Schema2;
using GltfMaterial = SharpGLTF.Schema2.Material;

namespace KhaozEngine.Render3D
{
    public static partial class GltfLoader
    {
        /// <summary>Load a rigid glb/glTF as one welded <see cref="GltfMeshPart"/> per source material, each part
        /// carrying that material's geometry (node world transforms baked exactly as <see cref="Load"/>) plus its
        /// auto-read <see cref="GltfMaterialMaps"/> (baseColor/normal/metallicRoughness, decoded to raw RGBA8, no
        /// GPU). This is the multi-texture-per-primitive path: a tree whose bark and leaves are separate
        /// primitives/materials returns two parts, each drawable with its own texture, instead of the single
        /// flattened mesh <see cref="Load"/> / <see cref="LoadWithMaterial"/> produce. Parts are in stable
        /// first-use material order (the order materials are first referenced walking meshes then primitives), so a
        /// re-load is reproducible. A single-material asset yields exactly one part whose mesh is byte-identical to
        /// <see cref="Load"/>'s and whose maps equal <see cref="LoadWithMaterial"/>'s. Primitives with no material
        /// form their own (untextured) part. Throws <see cref="InvalidOperationException"/> if the glTF has no
        /// triangles. Upload with <see cref="Scene3D.LoadProp"/> (or one
        /// <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/> per part); normalize a prop kit asset to its
        /// real-world height first with <see cref="PropLoader.LoadPropParts"/>.</summary>
        public static IReadOnlyList<GltfMeshPart> LoadPartsWithMaterials(string path)
            => BuildParts(LoadModel(path), path);

        static IReadOnlyList<GltfMeshPart> BuildParts(ModelRoot root, string path)
        {
            // Distinct materials in first-use order (a null material - primitives with none - is a valid key). Walk
            // meshes then primitives, the same traversal BuildRigid uses, so the single-material part's corner order
            // (and thus its weld) matches Load exactly.
            var order = new List<GltfMaterial?>();
            bool sawNull = false;
            foreach (var mesh in root.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                {
                    GltfMaterial? mat = prim.Material;
                    if (mat == null) { if (!sawNull) { sawNull = true; order.Add(null); } continue; }
                    if (!order.Contains(mat)) order.Add(mat);
                }

            var parts = new List<GltfMeshPart>(order.Count);
            foreach (GltfMaterial? material in order)
            {
                var corners = new List<MeshCorner>();
                // Same scene-graph walk as BuildRigid (a mesh referenced by several nodes emits one copy per node;
                // an un-noded mesh loads once at identity), but restricted to this material's primitives.
                foreach (var mesh in root.LogicalMeshes)
                {
                    bool placed = false;
                    foreach (var node in root.LogicalNodes)
                    {
                        if (node.Mesh != mesh) continue;
                        placed = true;
                        AppendMaterialCorners(corners, mesh, node.WorldMatrix, path, material);
                    }
                    if (!placed) AppendMaterialCorners(corners, mesh, Matrix4x4.Identity, path, material);
                }
                if (corners.Count > 0) parts.Add(new GltfMeshPart(MeshAssembler.Build(corners), ReadMaterialMaps(material)));
            }

            if (parts.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);
            return parts;
        }

        /// <summary>Load material parts from binary glTF bytes without modifying the source. Uses the same
        /// material ordering, node-transform bake and decoded maps as the path overload. The caller's
        /// <paramref name="identity"/> names the asset in validation diagnostics.</summary>
        /// <remarks>Attachment consumers must apply their embedded rigid asset policy before import.</remarks>
        /// <exception cref="InvalidDataException">The glTF is malformed. The message names
        /// <paramref name="identity"/> and the importer's diagnostic is the inner exception.</exception>
        /// <exception cref="InvalidOperationException">The glTF has no triangles or fails index validation.</exception>
        public static IReadOnlyList<GltfMeshPart> LoadPartsWithMaterials(ReadOnlyMemory<byte> glb, string identity) =>
            TranslateModelErrors(identity, () => BuildParts(LoadModel(glb, identity), identity));

        // As AppendMeshCorners, but only primitives that reference exactly <paramref name="material"/>
        // (reference-equality; a null target matches primitives with no material) contribute. Used by
        // LoadPartsWithMaterials to split a multi-material prop into one welded sub-mesh per source material,
        // reusing the identical transform + corner-emit math, so a single-material part is byte-identical to the
        // flattened Load path.
        static void AppendMaterialCorners(List<MeshCorner> corners, Mesh mesh, Matrix4x4 world, string path,
                                          GltfMaterial? material)
        {
            (bool identity, Matrix4x4 normalMatrix) = TransformFor(world);
            foreach (var prim in mesh.Primitives)
                if (ReferenceEquals(prim.Material, material))
                    AppendPrimitiveCorners(corners, prim, world, normalMatrix, identity, path);
        }

    }
}
