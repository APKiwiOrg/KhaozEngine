using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D.Rendering;
using SharpGLTF.Schema2;
// KhaozEngine.Render3D now defines its own Material struct; alias the glTF one to disambiguate.
using GltfMaterial = SharpGLTF.Schema2.Material;

namespace KhaozEngine.Render3D
{
    /// <summary>One decoded material texture: tightly-packed RGBA8 pixels (row-major, top-left origin,
    /// <c>Rgba.Length == Width * Height * 4</c>) plus its dimensions. Carries no GPU resources; upload it with
    /// <see cref="Scene3D.LoadTexture(byte[],int,int)"/> (or via <see cref="Scene3D.LoadSurfaceMaps"/>).</summary>
    public readonly struct DecodedImage
    {
        /// <summary>Tightly-packed RGBA8 pixels, row-major, top-left origin (length = Width*Height*4).</summary>
        public byte[] Rgba { get; }
        public int Width { get; }
        public int Height { get; }
        public DecodedImage(byte[] rgba, int width, int height) { Rgba = rgba; Width = width; Height = height; }
    }

    /// <summary>The optional textures auto-read from a glTF material by <see cref="GltfLoader.LoadWithMaterial"/> /
    /// <see cref="GltfLoader.LoadSkinnedWithMaterial"/>, each decoded to raw RGBA8 (the loader has no GPU device, so
    /// it never creates a <see cref="Scene3D.TextureHandle"/>; the upload stays in <see cref="Scene3D"/>). Any map a
    /// material doesn't reference - or whose image is missing/external-unresolved/undecodable - is left
    /// <c>null</c> (absent, never a throw), so a material with no textures yields an all-null
    /// <see cref="GltfMaterialMaps"/>. <see cref="Roughness"/> is glTF's packed ORM-style texture passed
    /// through unchanged (the model shader samples <c>.g</c> for roughness); <see cref="Normal"/> is the
    /// tangent-space RGB normal map unchanged. Feed the bundle to <see cref="Scene3D.LoadSurfaceMaps"/> (or the
    /// <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/> overload) to upload only the present maps.</summary>
    public readonly struct GltfMaterialMaps
    {
        /// <summary>Decoded baseColor (albedo) texture, or <c>null</c> if the material has none.</summary>
        public DecodedImage? Albedo { get; }
        /// <summary>Decoded tangent-space normal map (RGB unchanged), or <c>null</c> if the material has none.</summary>
        public DecodedImage? Normal { get; }
        /// <summary>Decoded glTF metallic-roughness texture, passed through unchanged (roughness in <c>.g</c>), or
        /// <c>null</c> if the material has none.</summary>
        public DecodedImage? Roughness { get; }
        public GltfMaterialMaps(DecodedImage? albedo, DecodedImage? normal, DecodedImage? roughness)
        {
            Albedo = albedo; Normal = normal; Roughness = roughness;
        }
        /// <summary>True when the material referenced (and the loader decoded) no textures at all.</summary>
        public bool IsEmpty => Albedo is null && Normal is null && Roughness is null;
    }

    /// <summary>One material sub-range of a multi-material prop loaded by
    /// <see cref="GltfLoader.LoadPartsWithMaterials"/> (and normalized by <see cref="PropLoader.LoadPropParts"/>):
    /// the welded geometry of the primitives that reference a single glTF material (<see cref="Mesh"/>) paired with
    /// that material's decoded <see cref="Maps"/>. A whole prop is a list of these - a tree trunk part (bark
    /// texture) + a leaf part (foliage texture), each drawn with its own texture binding. Upload each part with
    /// <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/> (or the turn-key <see cref="Scene3D.LoadProp"/>).
    /// A single-material asset yields exactly one part whose <see cref="Mesh"/> is byte-identical to
    /// <see cref="GltfLoader.Load"/>'s.</summary>
    public readonly struct GltfMeshPart
    {
        /// <summary>The welded geometry of one material's primitives.</summary>
        public GltfMesh Mesh { get; }
        /// <summary>That material's decoded baseColor/normal/roughness textures (all-absent when it has none).</summary>
        public GltfMaterialMaps Maps { get; }
        public GltfMeshPart(GltfMesh mesh, GltfMaterialMaps maps) { Mesh = mesh; Maps = maps; }
    }

    /// <summary>Loads a glTF/GLB at runtime via SharpGLTF into a flat-shaded <see cref="GltfMesh"/>.
    /// Reads POSITION/NORMAL/TEXCOORD_0/TANGENT; a missing TANGENT is computed from UV+position by
    /// MeshAssembler. By default material textures (normal/roughness) are NOT auto-read - bind them explicitly via
    /// <see cref="Scene3D.SurfaceMaps"/>. Opt into auto-read with <see cref="LoadWithMaterial"/> /
    /// <see cref="LoadSkinnedWithMaterial"/>, which also return the material's decoded
    /// <see cref="GltfMaterialMaps"/>.</summary>
    public static class GltfLoader
    {
        public static GltfMesh Load(string path) => BuildRigid(ModelRoot.Load(path), path);

        /// <summary>Load a rigid glb/glTF as ONE <see cref="GltfMesh"/> per logical node-with-mesh (object),
        /// world-transform baked exactly as <see cref="Load"/> bakes it, in stable logical-node-then-mesh order.
        /// Unlike <see cref="Load"/> (which flattens the whole scene into one mesh) this preserves the authoring
        /// object boundaries, so an authored collision proxy modelled as separate convex blocks bakes one convex
        /// piece per block. A mesh referenced by no node is loaded once at identity (parity with <see cref="Load"/>).
        /// Deterministic group order (logical-node index, then any un-noded meshes) so a re-bake is reproducible.</summary>
        public static IReadOnlyList<GltfMesh> LoadGroups(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
            var groups = new List<GltfMesh>();

            // One group per (node -> mesh), in logical-node order.
            foreach (var node in root.LogicalNodes)
            {
                if (node.Mesh is null) continue;
                var corners = new List<MeshCorner>();
                AppendMeshCorners(corners, node.Mesh, node.WorldMatrix);
                if (corners.Count > 0) groups.Add(MeshAssembler.Build(corners));
            }

            // Parity with Load: a mesh referenced by no node still contributes once, at identity.
            foreach (var mesh in root.LogicalMeshes)
            {
                bool placed = false;
                foreach (var node in root.LogicalNodes) { if (node.Mesh == mesh) { placed = true; break; } }
                if (placed) continue;
                var corners = new List<MeshCorner>();
                AppendMeshCorners(corners, mesh, Matrix4x4.Identity);
                if (corners.Count > 0) groups.Add(MeshAssembler.Build(corners));
            }

            if (groups.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);
            return groups;
        }

        /// <summary>Opt-in convenience: load a rigid glb/glTF AND auto-read its first textured material's baseColor,
        /// normal, and metallicRoughness textures, decoded to raw RGBA8 (no GPU - the returned
        /// <see cref="GltfMaterialMaps"/> holds CPU pixels). The <see cref="GltfMesh"/> is byte-identical to
        /// <see cref="Load"/>; only the extra maps are new. Upload them with <see cref="Scene3D.LoadSurfaceMaps"/>
        /// (or the <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/> overload). A material with no textures -
        /// or one whose images are missing/external-unresolved/undecodable - yields an all-absent
        /// <see cref="GltfMaterialMaps"/> (<see cref="GltfMaterialMaps.IsEmpty"/>), never a throw, so this degrades
        /// to the untextured render. Embedded GLB images and external image files (resolved by SharpGLTF relative to
        /// the glb on load) are both read.</summary>
        public static (GltfMesh Mesh, GltfMaterialMaps Maps) LoadWithMaterial(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
            GltfMesh mesh = BuildRigid(root, path);
            return (mesh, ReadMaterialMaps(FirstTexturedMaterial(root)));
        }

        /// <summary>Load a rigid glb/glTF as ONE flattened <see cref="GltfMesh"/> exactly like <see cref="Load"/>,
        /// except each source material that carries a <c>baseColorTexture</c> has that texture's alpha-weighted
        /// average albedo (<see cref="AverageAlbedo"/>) multiplied into its flattened per-vertex base colour. This is
        /// the flat path for a textured prop: it bakes a sensible single colour from the texture so the same
        /// textures-ON glb can render either textured (via <see cref="LoadPartsWithMaterials"/>) or flat (here) with
        /// no separate flattened asset. A material with NO baseColorTexture is untouched, so the whole-scene weld,
        /// topology, and every vertex attribute are byte-identical to <see cref="Load"/> for an untextured asset (the
        /// existing-goldens-hold guarantee). Throws <see cref="InvalidOperationException"/> if the glTF has no
        /// triangles.</summary>
        public static GltfMesh LoadFlattenedAlbedo(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
            return BuildRigid(root, path, MakeAlbedoFlattenResolver());
        }

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
            => BuildParts(ModelRoot.Load(path), path);

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
                        AppendMaterialCorners(corners, mesh, node.WorldMatrix, material);
                    }
                    if (!placed) AppendMaterialCorners(corners, mesh, Matrix4x4.Identity, material);
                }
                if (corners.Count > 0) parts.Add(new GltfMeshPart(MeshAssembler.Build(corners), ReadMaterialMaps(material)));
            }

            if (parts.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);
            return parts;
        }

        // The flat scene weld. <paramref name="baseColorFor"/> resolves a primitive's per-vertex base colour. When
        // null the default per-material factor (<see cref="ReadBaseColor"/>) is used, so Load / LoadWithMaterial stay
        // byte-identical. LoadFlattenedAlbedo passes a resolver that folds averaged albedo in.
        static GltfMesh BuildRigid(ModelRoot root, string path, Func<GltfMaterial?, Vector4>? baseColorFor = null)
        {
            var corners = new List<MeshCorner>();

            // Walk the scene graph, not the bare mesh list: glTF positions geometry via nodes, so a mesh's
            // POSITION/NORMAL/TANGENT are mesh-local and must be baked by the node's world transform (matching
            // BuildSkinned, which already bakes node.WorldMatrix). A mesh referenced by several nodes emits one
            // transformed copy per node (instancing); a mesh referenced by no node still loads once at identity,
            // so the historical mesh-walk output for pre-baked / single-node assets stays byte-identical.
            foreach (var mesh in root.LogicalMeshes)
            {
                bool placed = false;
                foreach (var node in root.LogicalNodes)
                {
                    if (node.Mesh != mesh) continue;
                    placed = true;
                    AppendMeshCorners(corners, mesh, node.WorldMatrix, baseColorFor);
                }
                if (!placed) AppendMeshCorners(corners, mesh, Matrix4x4.Identity, baseColorFor);
            }
            if (corners.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);

            return MeshAssembler.Build(corners);
        }

        // Append one mesh's primitives as transformed triangle corners. POSITION goes through the node world
        // matrix; NORMAL and TANGENT.xyz go through the normal matrix (transpose of the inverse upper-3x3),
        // renormalized, so they stay correct under non-uniform scale (TANGENT.w / bitangent sign preserved). An
        // exact-identity world matrix is a no-op fast path - raw accessor values pass straight through, so an
        // identity-node asset is byte-identical to the old mesh-walk loader. Per-primitive material base color
        // is read per primitive exactly as before, so LoadWithMaterial's material mapping stays aligned with the
        // transformed corners. SharpGLTF exposes the standard glTF attributes by name (same accessor pattern as
        // POSITION); a missing NORMAL/TANGENT is left null for MeshAssembler to compute from winding / UV.
        static void AppendMeshCorners(List<MeshCorner> corners, Mesh mesh, Matrix4x4 world,
                                      Func<GltfMaterial?, Vector4>? baseColorFor = null)
        {
            (bool identity, Matrix4x4 normalMatrix) = TransformFor(world);
            foreach (var prim in mesh.Primitives)
                AppendPrimitiveCorners(corners, prim, world, normalMatrix, identity, baseColorFor);
        }

        // As AppendMeshCorners, but only primitives that reference exactly <paramref name="material"/>
        // (reference-equality; a null target matches primitives with no material) contribute. Used by
        // LoadPartsWithMaterials to split a multi-material prop into one welded sub-mesh per source material,
        // reusing the identical transform + corner-emit math, so a single-material part is byte-identical to the
        // flattened Load path.
        static void AppendMaterialCorners(List<MeshCorner> corners, Mesh mesh, Matrix4x4 world, GltfMaterial? material)
        {
            (bool identity, Matrix4x4 normalMatrix) = TransformFor(world);
            foreach (var prim in mesh.Primitives)
                if (ReferenceEquals(prim.Material, material))
                    AppendPrimitiveCorners(corners, prim, world, normalMatrix, identity);
        }

        // The identity fast-path flag + normal matrix for a node world transform. Normal matrix =
        // transpose(inverse(world)); TransformNormal uses its upper 3x3 = (A^-1)^T, the correct map for
        // normals/tangents under non-uniform scale. A non-invertible (zero-scale) matrix falls back to the world
        // matrix itself (degenerate either way). An exact-identity world matrix is a no-op fast path.
        static (bool Identity, Matrix4x4 NormalMatrix) TransformFor(Matrix4x4 world)
        {
            if (world.IsIdentity) return (true, Matrix4x4.Identity);
            return (false, Matrix4x4.Invert(world, out Matrix4x4 inv) ? Matrix4x4.Transpose(inv) : world);
        }

        // Emit one primitive's triangles as transformed MeshCorners. POSITION goes through the node world matrix;
        // NORMAL and TANGENT.xyz go through the normal matrix, renormalized, so they stay correct under non-uniform
        // scale (TANGENT.w / bitangent sign preserved). Under an identity transform raw accessor values pass
        // straight through, so an identity-node asset is byte-identical to the old mesh-walk loader. Per-primitive
        // material base color is read per primitive exactly as before. SharpGLTF exposes the standard glTF
        // attributes by name; a missing NORMAL/TANGENT is left null for MeshAssembler to compute from winding / UV.
        static void AppendPrimitiveCorners(List<MeshCorner> corners, MeshPrimitive prim, Matrix4x4 world,
                                           Matrix4x4 normalMatrix, bool identity,
                                           Func<GltfMaterial?, Vector4>? baseColorFor = null)
        {
            var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (pos == null) return;
            // Source normals are honoured so the artist's hard edges survive; TANGENT is a vec4 (xyz =
            // tangent direction, w = bitangent sign per glTF spec). When absent, MeshAssembler computes them.
            var srcNormals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var srcTangents = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();
            // Default: the material's flat base-color factor. LoadFlattenedAlbedo passes a resolver that multiplies
            // the averaged albedo in. A null resolver keeps the historical per-material factor byte-for-byte.
            Vector4 baseColor = baseColorFor != null ? baseColorFor(prim.Material) : ReadBaseColor(prim.Material);

            Vector3 Pos(int i) => identity ? pos[i] : Vector3.Transform(pos[i], world);
            Vector3? Norm(int i)
            {
                if (srcNormals == null || i >= srcNormals.Count) return null;
                Vector3 n = srcNormals[i];
                if (identity) return n;
                Vector3 t = Vector3.TransformNormal(n, normalMatrix);
                float len2 = t.LengthSquared();
                return len2 > 1e-12f ? t / MathF.Sqrt(len2) : n;   // degenerate transform => keep source dir
            }
            Vector2 Uv(int i) => texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero;
            Vector4? Tan(int i)
            {
                if (srcTangents == null || i >= srcTangents.Count) return null;
                Vector4 src = srcTangents[i];
                if (identity) return src;
                Vector3 t = Vector3.TransformNormal(new Vector3(src.X, src.Y, src.Z), normalMatrix);
                float len2 = t.LengthSquared();
                if (len2 > 1e-12f) t /= MathF.Sqrt(len2);
                return new Vector4(t, src.W);   // keep the bitangent-sign handedness
            }

            foreach (var (a, b, c) in prim.GetTriangleIndices())
            {
                corners.Add(new MeshCorner(Pos(a), Norm(a), baseColor, Uv(a), Tan(a)));
                corners.Add(new MeshCorner(Pos(b), Norm(b), baseColor, Uv(b), Tan(b)));
                corners.Add(new MeshCorner(Pos(c), Norm(c), baseColor, Uv(c), Tan(c)));
            }
        }

        /// <summary>Load a rigged glb/glTF as a <see cref="SkinnedGltfMesh"/>: reads POSITION/NORMAL/TEXCOORD_0/TANGENT
        /// plus JOINTS_0/WEIGHTS_0 and the skin's inverse-bind matrices + rest-pose joint world transforms. A missing
        /// TANGENT is computed from UV+position (Lengyel, accumulated over the index list then Gram-Schmidt
        /// orthogonalized against the normal) so a normal-mapped skinned mesh perturbs correctly; a mesh with no UV
        /// gradient keeps a zero tangent (lit by the geometric normal). Material textures are NOT auto-read (bind a
        /// PNG albedo separately, as with <see cref="Load"/>, or opt into auto-read with
        /// <see cref="LoadSkinnedWithMaterial"/>). Throws if the mesh has no skin/joint data (use
        /// <see cref="Load"/> for rigid meshes). Indexed directly (no re-weld) so joints/weights stay aligned to
        /// their vertices; emits 32-bit indices so rigs past the 65,536-vertex ceiling load
        /// (<see cref="SkinnedGltfMesh"/> picks the GPU index width).</summary>
        public static SkinnedGltfMesh LoadSkinned(string path) => BuildSkinned(ModelRoot.Load(path), path);

        /// <summary>Opt-in convenience: load a rigged glb/glTF AND auto-read its first textured material's
        /// baseColor/normal/metallicRoughness textures, decoded to raw RGBA8 (no GPU). The
        /// <see cref="SkinnedGltfMesh"/> is identical to <see cref="LoadSkinned"/>; only the extra
        /// <see cref="GltfMaterialMaps"/> are new. Upload them with <see cref="Scene3D.LoadSurfaceMaps"/> and pass
        /// the result to <see cref="Scene3D.LoadSkinnedMesh(SkinnedGltfMesh,Scene3D.SurfaceMaps)"/>. A material with
        /// no (or missing/undecodable) textures yields an all-absent <see cref="GltfMaterialMaps"/>, never a
        /// throw.</summary>
        public static (SkinnedGltfMesh Mesh, GltfMaterialMaps Maps) LoadSkinnedWithMaterial(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
            SkinnedGltfMesh mesh = BuildSkinned(root, path);
            return (mesh, ReadMaterialMaps(FirstTexturedMaterial(root)));
        }

        static SkinnedGltfMesh BuildSkinned(ModelRoot root, string path)
        {
            var verts = new List<SkinnedVertex>();
            var indices = new List<uint>();
            var srcTangent = new List<Vector4?>();   // glTF TANGENT per vertex if present; null => compute
            Skin? skin = null;

            foreach (var mesh in root.LogicalMeshes)
            foreach (var prim in mesh.Primitives)
            {
                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                var joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                if (pos == null || joints == null || weights == null) continue;

                // Single-skin assumption: the first skin found supplies InverseBind/RestPose for all meshes.
                // A glb where different meshes use different skins is not supported; later meshes would mis-bind against this skin.
                skin ??= root.LogicalNodes.FirstOrDefault(n => n.Mesh == mesh && n.Skin != null)?.Skin
                         ?? root.LogicalSkins.FirstOrDefault();

                var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                // TANGENT is a vec4 (xyz = tangent direction, w = bitangent sign per glTF spec); when absent,
                // it is computed below from UV+position.
                var tangents = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();
                Vector4 baseColor = ReadBaseColor(prim.Material);

                int baseIndex = verts.Count;
                for (int i = 0; i < pos.Count; i++)
                {
                    Vector4 w = SkinningMath.NormalizeWeights(weights[i]);
                    verts.Add(new SkinnedVertex
                    {
                        Position = pos[i],
                        Normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY,
                        Color = baseColor,
                        Uv = texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero,
                        BoneIndices = joints[i],
                        BoneWeights = w,
                    });
                    srcTangent.Add(tangents != null && i < tangents.Count ? tangents[i] : (Vector4?)null);
                }
                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    indices.Add((uint)(baseIndex + a));
                    indices.Add((uint)(baseIndex + b));
                    indices.Add((uint)(baseIndex + c));
                }
            }

            if (verts.Count == 0 || skin == null)
                throw new InvalidOperationException("glTF has no skinned mesh (JOINTS_0/WEIGHTS_0 + skin): " + path);

            ComputeSkinnedTangents(verts, indices, srcTangent);

            int boneCount = skin.JointsCount;

            // Reject a malformed/malicious rig at load. The CPU skinning path slices the bone palette to
            // exactly MaxBonesPerDraw and reads it unconditionally per vertex (SkinningMath.BlendSkinMatrix),
            // so an oversized joint count or an out-of-range JOINTS_0 index would otherwise throw mid-frame.
            if (boneCount > SkinningMath.MaxBonesPerDraw)
                throw new InvalidOperationException(
                    $"glTF skin has {boneCount} joints, over the {SkinningMath.MaxBonesPerDraw}-bone per-draw cap: {path}");
            foreach (SkinnedVertex v in verts)
                if (!SkinningMath.AreBoneIndicesValid(v.BoneIndices, boneCount))
                    throw new InvalidOperationException(
                        $"glTF JOINTS_0 references a bone index outside [0,{boneCount}): {path}");

            var inverseBind = new Matrix4x4[boneCount];
            var restPose = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                var (node, ibm) = skin.GetJoint(b);
                inverseBind[b] = ibm;
                restPose[b] = node.WorldMatrix;     // bind-pose joint world transform
            }

            Skeleton skeleton = BuildSkeleton(skin);
            return new SkinnedGltfMesh(verts.ToArray(), indices.ToArray(), inverseBind, restPose, skeleton);
        }

        /// <summary>Build the poseable joint hierarchy from a glTF skin: the skeleton node set is every joint plus
        /// all of its visual ancestors (so a static armature/root offset above the joints is composed in), ordered
        /// parents-before-children. Each node carries its rest-local TRS (from the node's local matrix) and its glTF
        /// logical index (the key an <see cref="AnimationClip"/> channel targets); <see cref="Skeleton.JointToNode"/>
        /// maps each skin bone (in joint order, aligned with InverseBind/RestPose) to its skeleton node.</summary>
        static Skeleton BuildSkeleton(Skin skin)
        {
            int jointCount = skin.JointsCount;
            var jointNodes = new Node[jointCount];
            for (int b = 0; b < jointCount; b++) jointNodes[b] = skin.GetJoint(b).Joint;

            // Node set = joints + every visual ancestor up to a scene root.
            var nodeSet = new HashSet<Node>();
            foreach (Node jn in jointNodes)
                for (Node? n = jn; n != null; n = n.VisualParent) nodeSet.Add(n);

            // Topological order: ascending by ancestor depth, so a parent always precedes its children.
            var ordered = nodeSet.ToList();
            ordered.Sort((a, b) => NodeDepth(a).CompareTo(NodeDepth(b)));
            var indexOf = new Dictionary<Node, int>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++) indexOf[ordered[i]] = i;

            var parents = new int[ordered.Count];
            var restLocal = new JointPose[ordered.Count];
            var nodeLogical = new int[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                Node node = ordered[i];
                Node? parent = node.VisualParent;
                parents[i] = parent != null && indexOf.TryGetValue(parent, out int pi) ? pi : -1;
                restLocal[i] = JointPose.FromMatrix(node.LocalMatrix);   // local transform relative to the parent
                nodeLogical[i] = node.LogicalIndex;
            }

            var jointToNode = new int[jointCount];
            for (int b = 0; b < jointCount; b++) jointToNode[b] = indexOf[jointNodes[b]];

            return new Skeleton(parents, restLocal, nodeLogical, jointToNode);
        }

        static int NodeDepth(Node node)
        {
            int d = 0;
            for (Node? n = node.VisualParent; n != null; n = n.VisualParent) d++;
            return d;
        }

        /// <summary>Read every glTF animation as an <see cref="AnimationClip"/> (additive: leaves
        /// <see cref="Load"/> / <see cref="LoadSkinned"/> unchanged). Each animation's channels are grouped by
        /// target node into a <see cref="JointTrack"/> (translation / rotation / scale), keyed by the node's glTF
        /// logical index so the clip resolves against the mesh <see cref="Skeleton"/>; per channel the sampler keys
        /// are read with their interpolation (STEP held, LINEAR interpolated, CUBICSPLINE reduced to its value keys
        /// and treated as linear). Clip duration is the latest key across all channels. A glb with no animations
        /// returns an empty list.</summary>
        public static IReadOnlyList<AnimationClip> LoadAnimations(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
            var clips = new List<AnimationClip>(root.LogicalAnimations.Count);
            foreach (SharpGLTF.Schema2.Animation anim in root.LogicalAnimations)
            {
                var byNode = new Dictionary<int, JointTrack>();
                float duration = 0f;
                foreach (AnimationChannel channel in anim.Channels)
                {
                    Node? target = channel.TargetNode;
                    if (target == null) continue;
                    int logical = target.LogicalIndex;
                    if (!byNode.TryGetValue(logical, out JointTrack? track))
                    {
                        track = new JointTrack(logical);
                        byNode[logical] = track;
                    }

                    var ts = channel.GetTranslationSampler();
                    if (ts != null) { track.Translation = ReadVector3Track(ts, ref duration); continue; }
                    var rs = channel.GetRotationSampler();
                    if (rs != null) { track.Rotation = ReadQuaternionTrack(rs, ref duration); continue; }
                    var ss = channel.GetScaleSampler();
                    if (ss != null) { track.Scale = ReadVector3Track(ss, ref duration); continue; }
                    // A morph-weights channel (or any other path) is out of scope for skeletal playback: skip it.
                }

                // Drop a node whose only channels were skipped (no TRS track).
                var tracks = byNode.Values.Where(t => t.Translation != null || t.Rotation != null || t.Scale != null).ToList();
                clips.Add(new AnimationClip(anim.Name ?? string.Empty, duration, tracks));
            }
            return clips;
        }

        static Vector3Track ReadVector3Track(IAnimationSampler<Vector3> s, ref float duration)
        {
            var times = new List<float>();
            var values = new List<Vector3>();
            if (s.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
                foreach (var (key, value) in s.GetCubicKeys()) { times.Add(key); values.Add(value.Value); }
            else
                foreach (var (key, value) in s.GetLinearKeys()) { times.Add(key); values.Add(value); }
            if (times.Count > 0) duration = MathF.Max(duration, times[times.Count - 1]);
            return new Vector3Track(times.ToArray(), values.ToArray(), MapInterp(s.InterpolationMode));
        }

        static QuaternionTrack ReadQuaternionTrack(IAnimationSampler<Quaternion> s, ref float duration)
        {
            var times = new List<float>();
            var values = new List<Quaternion>();
            if (s.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
                foreach (var (key, value) in s.GetCubicKeys()) { times.Add(key); values.Add(value.Value); }
            else
                foreach (var (key, value) in s.GetLinearKeys()) { times.Add(key); values.Add(value); }
            if (times.Count > 0) duration = MathF.Max(duration, times[times.Count - 1]);
            return new QuaternionTrack(times.ToArray(), values.ToArray(), MapInterp(s.InterpolationMode));
        }

        static InterpolationMode MapInterp(AnimationInterpolationMode m) =>
            m == AnimationInterpolationMode.STEP ? InterpolationMode.Step : InterpolationMode.Linear;

        // Resolve a per-vertex tangent for the skinned mesh and write it into each SkinnedVertex.Tangent. A glTF
        // TANGENT (srcTangent[i]) wins; otherwise accumulate the Lengyel UV-space direction over the triangle list
        // (directly indexed, no weld - shared corners share a global index, so their face contributions sum) and
        // Gram-Schmidt orthogonalize against the vertex normal. Degenerate UVs => zero tangent (geometric normal).
        // Mirrors MeshAssembler's tangent path via the shared TangentMath helper.
        static void ComputeSkinnedTangents(List<SkinnedVertex> verts, List<uint> indices, List<Vector4?> srcTangent)
        {
            int n = verts.Count;
            var tan1 = new Vector3[n];
            var tan2 = new Vector3[n];
            for (int t = 0; t + 2 < indices.Count; t += 3)
            {
                int i0 = (int)indices[t], i1 = (int)indices[t + 1], i2 = (int)indices[t + 2];
                TangentMath.FaceDirections(
                    verts[i0].Position, verts[i1].Position, verts[i2].Position,
                    verts[i0].Uv, verts[i1].Uv, verts[i2].Uv, out Vector3 sdir, out Vector3 tdir);
                tan1[i0] += sdir; tan1[i1] += sdir; tan1[i2] += sdir;
                tan2[i0] += tdir; tan2[i1] += tdir; tan2[i2] += tdir;
            }
            for (int i = 0; i < n; i++)
            {
                Vector3 nrm = verts[i].Normal;
                nrm = nrm.LengthSquared() > 1e-12f ? Vector3.Normalize(nrm) : Vector3.UnitY;
                SkinnedVertex v = verts[i];
                v.Tangent = TangentMath.Resolve(nrm, tan1[i], tan2[i], srcTangent[i]);
                verts[i] = v;
            }
        }

        static Vector4 ReadBaseColor(GltfMaterial? mat)
        {
            var fallback = new Vector4(0.8f, 0.8f, 0.8f, 1f);
            if (mat == null) return fallback;
            var ch = mat.FindChannel("BaseColor");
            if (ch == null) return fallback;
            return ch.Value.Color;
        }

        /// <summary>The alpha-weighted average albedo (RGB, normalized [0,1]) of a decoded baseColor image, for
        /// baking a textured material down to one flat colour. Texels with alpha &gt;= 0.5 are averaged. When NONE
        /// pass (a fully-transparent / all-cutout image) it falls back to the plain average of every texel's RGB so
        /// the colour is still defined. An empty image yields white (no tint). This is the shared math the flat prop
        /// loader (<see cref="LoadFlattenedAlbedo"/>) folds into each textured material's vertex colour, exposed so an
        /// offline baker or a future caller uses the identical rule.</summary>
        public static Vector3 AverageAlbedo(DecodedImage image)
        {
            byte[] px = image.Rgba;
            int texels = image.Width * image.Height;
            if (px == null || texels <= 0 || px.Length < texels * 4) return Vector3.One;

            long r = 0, g = 0, b = 0, opaque = 0;      // texels with alpha >= 0.5
            long ra = 0, ga = 0, ba = 0;               // all texels (fallback)
            for (int i = 0; i < texels; i++)
            {
                byte pr = px[i * 4], pg = px[i * 4 + 1], pb = px[i * 4 + 2], pa = px[i * 4 + 3];
                ra += pr; ga += pg; ba += pb;
                if (pa / 255f >= 0.5f) { r += pr; g += pg; b += pb; opaque++; }
            }
            if (opaque > 0) return new Vector3(r / (float)opaque, g / (float)opaque, b / (float)opaque) / 255f;
            return new Vector3(ra / (float)texels, ga / (float)texels, ba / (float)texels) / 255f;
        }

        // A per-load base-colour resolver for LoadFlattenedAlbedo: a material's flattened colour = its baseColor
        // factor, times the alpha-weighted average of its decoded baseColorTexture when it has one (RGB only, factor
        // alpha preserved). Memoized per material so a multi-primitive material decodes its albedo once, not per
        // primitive. A material with no baseColorTexture returns the factor unchanged, so the flattened mesh is
        // byte-identical to Load for untextured assets.
        static Func<GltfMaterial?, Vector4> MakeAlbedoFlattenResolver()
        {
            var cache = new Dictionary<GltfMaterial, Vector4>();
            return mat =>
            {
                Vector4 factor = ReadBaseColor(mat);
                if (mat == null) return factor;
                if (cache.TryGetValue(mat, out Vector4 cached)) return cached;
                DecodedImage? albedo = DecodeChannel(mat, "BaseColor");
                Vector4 eff = factor;
                if (albedo is { } img)
                {
                    Vector3 avg = AverageAlbedo(img);
                    eff = new Vector4(factor.X * avg.X, factor.Y * avg.Y, factor.Z * avg.Z, factor.W);
                }
                cache[mat] = eff;
                return eff;
            };
        }

        /// <summary>The first logical material that references at least one of the auto-read texture channels
        /// (BaseColor / Normal / MetallicRoughness), or the first primitive's material, or null. Single-material
        /// assumption mirrors the loaders flattening all primitives into one mesh: the auto-read path is a
        /// convenience for the common one-material asset, not a multi-material atlas system.</summary>
        static GltfMaterial? FirstTexturedMaterial(ModelRoot root)
        {
            foreach (var m in root.LogicalMaterials)
                if (HasAnyAutoReadTexture(m)) return m;
            // No textured material: fall back to the first primitive's material so an explicit-but-untextured
            // material still resolves (ReadMaterialMaps then just returns all-absent).
            return root.LogicalMeshes.SelectMany(me => me.Primitives).Select(p => p.Material).FirstOrDefault(m => m != null);
        }

        static bool HasAnyAutoReadTexture(GltfMaterial? mat)
        {
            if (mat == null) return false;
            return ChannelTexture(mat, "BaseColor") != null
                || ChannelTexture(mat, "Normal") != null
                || ChannelTexture(mat, "MetallicRoughness") != null;
        }

        /// <summary>Decode a material's baseColor / normal / metallicRoughness textures to raw RGBA8. Each absent or
        /// undecodable channel becomes a null map (graceful degrade, never a throw). metallicRoughness is passed
        /// through unchanged (the shader reads roughness from .g); normal stays tangent-space RGB.</summary>
        static GltfMaterialMaps ReadMaterialMaps(GltfMaterial? mat)
        {
            if (mat == null) return default;   // all-null
            return new GltfMaterialMaps(
                DecodeChannel(mat, "BaseColor"),
                DecodeChannel(mat, "Normal"),
                DecodeChannel(mat, "MetallicRoughness"));
        }

        /// <summary>The encoded image bytes (PNG/JPG/...) for a material channel's texture, or null if the channel
        /// has no texture / no primary image / the image is empty or unresolved (e.g. an external file that did not
        /// resolve relative to the glb).</summary>
        static ReadOnlyMemory<byte>? ChannelTexture(GltfMaterial mat, string key)
        {
            var ch = mat.FindChannel(key);
            var content = ch?.Texture?.PrimaryImage?.Content;
            if (content is not { IsValid: true } img) return null;
            return img.Content;
        }

        /// <summary>Decode one material channel's texture to RGBA8 via the engine's shared image decoder
        /// (<see cref="ImageRgba.Decode"/>); returns null when the channel has no usable image OR the bytes fail to
        /// decode (a corrupt/unsupported image is treated as absent, so the mesh just renders without that map).</summary>
        static DecodedImage? DecodeChannel(GltfMaterial mat, string key)
        {
            var bytes = ChannelTexture(mat, key);
            if (bytes is not { } b) return null;
            try
            {
                ImageRgba img = ImageRgba.Decode(b.Span);
                return new DecodedImage(img.Pixels, img.Width, img.Height);
            }
            catch
            {
                return null;   // undecodable image => map absent (degrade gracefully, no throw)
            }
        }
    }
}
