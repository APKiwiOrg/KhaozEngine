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
    /// <summary>One material sub-range of a multi-material prop loaded by
    /// <see cref="GltfLoader.LoadPartsWithMaterials(string)"/> (and normalized by <see cref="PropLoader.LoadPropParts"/>):
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
    /// Reads POSITION/NORMAL/TEXCOORD_0/TANGENT/COLOR_0. A missing TANGENT is computed from UV+position by
    /// MeshAssembler. COLOR_0 is a MULTIPLIER on the material's base colour per the glTF spec (rigid and skinned
    /// paths alike, normalized ubyte/ushort/float, vec3 or vec4), so a palette-painted kit piece keeps its
    /// authored colours with no texture, and an asset without the attribute loads byte-for-byte as before. On the
    /// rigid path a per-vertex colour also enters the weld key, so two coplanar faces in different palette shades
    /// stay two vertices at their shared edge instead of the first shade winning. A colour seam also splits the
    /// smooth-normal and tangent accumulation, exactly as a UV seam always has, so a mesh with no source NORMAL
    /// picks up a shading crease where two shades meet (glTF exporters almost always write NORMAL, which wins
    /// over the computed one). By
    /// default material textures (normal/roughness) are NOT auto-read - bind them explicitly via
    /// <see cref="Scene3D.SurfaceMaps"/>. Opt into auto-read with <see cref="LoadWithMaterial(string)"/> /
    /// <see cref="LoadSkinnedWithMaterial"/>, which also return the material's decoded
    /// <see cref="GltfMaterialMaps"/>.</summary>
    public static partial class GltfLoader
    {
        public static GltfMesh Load(string path) => BuildRigid(LoadModel(path), path);

        /// <summary>Load a rigid glb/glTF as ONE <see cref="GltfMesh"/> per logical node-with-mesh (object),
        /// world-transform baked exactly as <see cref="Load"/> bakes it, in stable logical-node-then-mesh order.
        /// Unlike <see cref="Load"/> (which flattens the whole scene into one mesh) this preserves the authoring
        /// object boundaries, so an authored collision proxy modelled as separate convex blocks bakes one convex
        /// piece per block. A mesh referenced by no node is loaded once at identity (parity with <see cref="Load"/>).
        /// Deterministic group order (logical-node index, then any un-noded meshes) so a re-bake is reproducible.</summary>
        public static IReadOnlyList<GltfMesh> LoadGroups(string path)
        {
            ModelRoot root = LoadModel(path);
            var groups = new List<GltfMesh>();

            // One group per (node -> mesh), in logical-node order.
            foreach (var node in root.LogicalNodes)
            {
                if (node.Mesh is null) continue;
                var corners = new List<MeshCorner>();
                AppendMeshCorners(corners, node.Mesh, node.WorldMatrix, path);
                if (corners.Count > 0) groups.Add(MeshAssembler.Build(corners));
            }

            // Parity with Load: a mesh referenced by no node still contributes once, at identity.
            foreach (var mesh in root.LogicalMeshes)
            {
                bool placed = false;
                foreach (var node in root.LogicalNodes) { if (node.Mesh == mesh) { placed = true; break; } }
                if (placed) continue;
                var corners = new List<MeshCorner>();
                AppendMeshCorners(corners, mesh, Matrix4x4.Identity, path);
                if (corners.Count > 0) groups.Add(MeshAssembler.Build(corners));
            }

            if (groups.Count == 0) throw new GltfImportStateException(GltfImportRefusal.NoTriangles, "glTF has no triangles: " + path) { Identity = path };
            return groups;
        }

        /// <summary>Every logical node with a non-empty name, empties included, in logical-node order, each with
        /// its world transform exactly as <see cref="Load"/> bakes geometry. Unnamed nodes are skipped but still
        /// place their named descendants. Duplicate names are all returned. Needs no geometry.</summary>
        public static IReadOnlyList<GltfNamedNode> LoadNamedNodes(string path)
            => GltfNamedNodeReader.Read(LoadModel(path));

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
            => LoadWithMaterial(path, null);

        internal static (GltfMesh Mesh, GltfMaterialMaps Maps) LoadWithMaterial(string path, string? entryId)
        {
            ModelRoot root = LoadModel(path, entryId);
            GltfMesh mesh = BuildRigid(root, path, entryId: entryId);
            return (mesh, ReadMaterialMaps(FirstTexturedMaterial(root), AssetDirectory(path)));
        }

        /// <summary>Load a rigid glb/glTF as ONE flattened <see cref="GltfMesh"/> exactly like <see cref="Load"/>,
        /// except each source material that carries a <c>baseColorTexture</c> has that texture's alpha-weighted
        /// average albedo (<see cref="AverageAlbedo"/>) multiplied into its flattened per-vertex base colour. This is
        /// the flat path for a textured prop: it bakes a sensible single colour from the texture so the same
        /// textures-ON glb can render either textured (via <see cref="LoadPartsWithMaterials(string)"/>) or flat (here) with
        /// no separate flattened asset. A material with NO baseColorTexture is untouched, so the whole-scene weld,
        /// topology, and every vertex attribute are byte-identical to <see cref="Load"/> for an untextured asset (the
        /// existing-goldens-hold guarantee). Throws <see cref="GltfImportStateException"/> with NoTriangles if the glTF has no
        /// triangles.</summary>
        public static GltfMesh LoadFlattenedAlbedo(string path) => LoadFlattenedAlbedo(path, null);

        internal static GltfMesh LoadFlattenedAlbedo(string path, string? entryId)
        {
            ModelRoot root = LoadModel(path, entryId);
            return BuildRigid(root, path, MakeAlbedoFlattenResolver(AssetDirectory(path)), entryId);
        }

        /// <summary><see cref="LoadFlattenedAlbedo(string)"/> over a binary glTF held in memory. Only
        /// <paramref name="glb"/> is parsed: there is no directory to resolve a relative external buffer or image
        /// against, so a caller that must not read outside its bytes refuses external URIs first.
        /// <paramref name="identity"/> is the raw MeshIndex or GltfImport refusal Identity where the path overload uses the file.
        /// Array-backed bytes are read in place without a copy, so they must not change during the call.</summary>
        /// <exception cref="GltfImportDataException">The glTF is malformed. Reason is
        /// <see cref="GltfImportRefusal.MalformedModel"/>, Identity is <paramref name="identity"/> and the
        /// importer's diagnostic is the inner exception. This type derives from Exception because
        /// InvalidDataException is sealed.</exception>
        /// <exception cref="GltfImportStateException">The glTF has no triangles or fails strict validation.</exception>
        /// <exception cref="MeshIndexStateException">A triangle index is outside its vertex domain.</exception>
        public static GltfMesh LoadFlattenedAlbedo(ReadOnlyMemory<byte> glb, string identity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            return TranslateModelErrors(identity,
                () => BuildRigid(LoadModel(glb, identity), identity, MakeAlbedoFlattenResolver(assetDirectory: null)));
        }

        // The flat scene weld. <paramref name="baseColorFor"/> resolves a primitive's per-vertex base colour. When
        // null the default per-material factor (<see cref="ReadBaseColor"/>) is used, so Load / LoadWithMaterial stay
        // byte-identical. LoadFlattenedAlbedo passes a resolver that folds averaged albedo in.
        static GltfMesh BuildRigid(ModelRoot root, string path, Func<GltfMaterial?, Vector4>? baseColorFor = null, string? entryId = null)
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
                    AppendMeshCorners(corners, mesh, node.WorldMatrix, path, baseColorFor, entryId);
                }
                if (!placed) AppendMeshCorners(corners, mesh, Matrix4x4.Identity, path, baseColorFor, entryId);
            }
            if (corners.Count == 0) throw new GltfImportStateException(GltfImportRefusal.NoTriangles, "glTF has no triangles: " + path) { Identity = path };

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
        static void AppendMeshCorners(List<MeshCorner> corners, Mesh mesh, Matrix4x4 world, string path,
                                      Func<GltfMaterial?, Vector4>? baseColorFor = null, string? entryId = null)
        {
            (bool identity, Matrix4x4 normalMatrix) = TransformFor(world);
            foreach (var prim in mesh.Primitives)
                AppendPrimitiveCorners(corners, prim, world, normalMatrix, identity, path, baseColorFor, entryId);
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
                                           Matrix4x4 normalMatrix, bool identity, string path,
                                           Func<GltfMaterial?, Vector4>? baseColorFor = null, string? entryId = null, Action<int, int, int>? triangleRead = null)
        {
            var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (pos == null) return;
            // Source normals are honoured so the artist's hard edges survive; TANGENT is a vec4 (xyz =
            // tangent direction, w = bitangent sign per glTF spec). When absent, MeshAssembler computes them.
            var srcNormals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var srcTangents = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();
            // Per-vertex COLOR_0, absent on most assets. AsColorArray normalizes the spec's ubyte/ushort/float
            // component types and widens a vec3 to vec4 with alpha 1, so the multiply below is uniform.
            var srcColors = prim.GetVertexAccessor("COLOR_0")?.AsColorArray();
            // Default: the material's flat base-color factor. LoadFlattenedAlbedo passes a resolver that multiplies
            // the averaged albedo in. A null resolver keeps the historical per-material factor byte-for-byte.
            Vector4 baseColor = baseColorFor != null ? baseColorFor(prim.Material) : ReadBaseColor(prim.Material);
            string assetIdentity = $"glTF asset '{path}'";

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
            // COLOR_0 is a MULTIPLIER on the material colour per the glTF spec, not a replacement, so a white
            // COLOR_0 changes nothing and an asset without the attribute keeps the flat factor byte-for-byte.
            Vector4 Color(int i) => srcColors != null && i < srcColors.Count ? baseColor * srcColors[i] : baseColor;
            bool perVertexColor = srcColors != null;
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
                int ai = MeshIndexValidation.Source(a, pos.Count, path, entryId, diagnosticIdentity: assetIdentity);
                int bi = MeshIndexValidation.Source(b, pos.Count, path, entryId, diagnosticIdentity: assetIdentity);
                int ci = MeshIndexValidation.Source(c, pos.Count, path, entryId, diagnosticIdentity: assetIdentity);
                // The source flag distinguishes quantized COLOR_0 paint from exact flat material colors.
                // Both kinds preserve their colour seams in the flattened weld.
                corners.Add(new MeshCorner(Pos(ai), Norm(ai), Color(ai), Uv(ai), Tan(ai), perVertexColor));
                corners.Add(new MeshCorner(Pos(bi), Norm(bi), Color(bi), Uv(bi), Tan(bi), perVertexColor));
                corners.Add(new MeshCorner(Pos(ci), Norm(ci), Color(ci), Uv(ci), Tan(ci), perVertexColor));
                triangleRead?.Invoke(ai, bi, ci);
            }
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
            var nodeNames = new string[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                Node node = ordered[i];
                Node? parent = node.VisualParent;
                parents[i] = parent != null && indexOf.TryGetValue(parent, out int pi) ? pi : -1;
                restLocal[i] = JointPose.FromMatrix(node.LocalMatrix);   // local transform relative to the parent
                nodeLogical[i] = node.LogicalIndex;
                nodeNames[i] = node.Name ?? string.Empty;
            }

            var jointToNode = new int[jointCount];
            for (int b = 0; b < jointCount; b++) jointToNode[b] = indexOf[jointNodes[b]];

            return new Skeleton(parents, restLocal, nodeLogical, jointToNode, nodeNames);
        }

        static int NodeDepth(Node node)
        {
            int d = 0;
            for (Node? n = node.VisualParent; n != null; n = n.VisualParent) d++;
            return d;
        }

        /// <summary>Read every glTF animation as an <see cref="AnimationClip"/> (additive: leaves
        /// <see cref="Load"/> / <see cref="LoadSkinned(string)"/> unchanged). Each animation's channels are grouped by
        /// target node into a <see cref="JointTrack"/> (translation / rotation / scale), keyed by the node's glTF
        /// logical index so the clip resolves against the mesh <see cref="Skeleton"/>; per channel the sampler keys
        /// are read with their interpolation (STEP held, LINEAR interpolated, CUBICSPLINE reduced to its value keys
        /// and treated as linear). Clip duration is the latest key across all channels. A glb with no animations
        /// returns an empty list.</summary>
        public static IReadOnlyList<AnimationClip> LoadAnimations(string path)
        {
            ModelRoot root = LoadModel(path);
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
        /// loader (<see cref="LoadFlattenedAlbedo(string)"/>) folds into each textured material's vertex colour, exposed so an
        /// offline baker or a future caller uses the identical rule.
        /// This mean is deliberately computed in gamma space, directly on the raw sRGB byte values, and must not be
        /// linearized. The engine's textured path uploads albedo as UNorm and the shader multiplies it by vertex
        /// colour without linearizing either input, so keeping this average in that same gamma-space representation
        /// is what makes the flat swatch match the visual average of the textured surface.</summary>
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
        static Func<GltfMaterial?, Vector4> MakeAlbedoFlattenResolver(string? assetDirectory)
        {
            var cache = new Dictionary<GltfMaterial, Vector4>();
            return mat =>
            {
                if (mat == null) return ReadBaseColor(mat);
                if (cache.TryGetValue(mat, out Vector4 cached)) return cached;
                Vector4 factor = ReadBaseColor(mat);
                DecodedImage? albedo = DecodeChannel(mat, "BaseColor", assetDirectory);
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
    }
}
