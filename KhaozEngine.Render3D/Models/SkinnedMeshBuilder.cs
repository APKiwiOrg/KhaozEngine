using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>The axis a procedural skinned primitive runs along.</summary>
    public enum Axis { X, Y, Z }

    /// <summary>Builds procedural skinned primitives whose bones are defined in code (no authored glb rig). The
    /// dominant code-driven shape is an elongated tube that bends along its length: tentacle, cable, limb,
    /// antenna. Each ring of vertices is weighted to its 1-2 nearest bones with a smooth cross-boundary falloff,
    /// so bending reads as flesh rather than facets.</summary>
    public static class SkinnedMeshBuilder
    {
        /// <summary>Build a capped-open tube of <paramref name="length"/> along <paramref name="axis"/> with
        /// <paramref name="boneCount"/> bones evenly spaced from the base (axis 0) to the tip. The tube has
        /// <paramref name="ringSegments"/>+1 rings of <paramref name="radialSegments"/> vertices. Bone rest
        /// transforms are pure translations along the axis; the rest pose leaves the tube straight.</summary>
        public static SkinnedGltfMesh BuildTube(float radius, float length, int ringSegments, int radialSegments,
            int boneCount, Axis axis = Axis.Z)
        {
            if (ringSegments < 1) throw new ArgumentOutOfRangeException(nameof(ringSegments));
            if (radialSegments < 3) throw new ArgumentOutOfRangeException(nameof(radialSegments));
            if (boneCount < 1) throw new ArgumentOutOfRangeException(nameof(boneCount));

            // Bones: evenly spaced rest translations along the axis. InverseBind = inverse(restWorld).
            var restPose = new Matrix4x4[boneCount];
            var inverseBind = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                float t = boneCount == 1 ? 0f : (float)b / (boneCount - 1);
                restPose[b] = Matrix4x4.CreateTranslation(AlongAxis(axis, t * length));
                Matrix4x4.Invert(restPose[b], out inverseBind[b]);
            }

            var verts = new List<SkinnedVertex>();
            // Two in-plane axes perpendicular to the run axis, for the ring cross-section.
            (Vector3 u, Vector3 w) = PerpAxes(axis);
            int rings = ringSegments + 1;
            for (int r = 0; r < rings; r++)
            {
                float along = (float)r / ringSegments;            // 0..1 down the tube
                float axial = along * length;
                // Bone weighting: position the ring on the [0, boneCount-1] bone axis, weight the two straddling
                // bones by the fractional distance (linear blend), clamped at the ends.
                float bonePos = along * (boneCount - 1);
                int b0 = Math.Clamp((int)MathF.Floor(bonePos), 0, boneCount - 1);
                int b1 = Math.Min(b0 + 1, boneCount - 1);
                float frac = bonePos - b0;
                var indices = new Vector4(b0, b1, 0, 0);
                var weights = SkinningMath.NormalizeWeights(new Vector4(1f - frac, frac, 0, 0));

                for (int s = 0; s < radialSegments; s++)
                {
                    float a = (float)s / radialSegments * MathF.Tau;
                    Vector3 dir = u * MathF.Cos(a) + w * MathF.Sin(a);
                    Vector3 pos = AlongAxis(axis, axial) + dir * radius;
                    verts.Add(new SkinnedVertex
                    {
                        Position = pos,
                        Normal = dir,                              // outward radial normal
                        Color = new Vector4(0.8f, 0.8f, 0.8f, 1f), // default gray, like GltfLoader's base color
                        Uv = new Vector2(along, (float)s / radialSegments),
                        BoneIndices = indices,
                        BoneWeights = weights,
                    });
                }
            }

            // Indices: quad strip between successive rings (two triangles per quad), radial wrap.
            var idx = new List<ushort>();
            for (int r = 0; r < ringSegments; r++)
            for (int s = 0; s < radialSegments; s++)
            {
                int s1 = (s + 1) % radialSegments;
                int a = r * radialSegments + s;
                int b = r * radialSegments + s1;
                int c = (r + 1) * radialSegments + s;
                int d = (r + 1) * radialSegments + s1;
                // Counter-clockwise (outward-facing) winding matches the outward radial normals; back-face cull keeps the outer shell.
                idx.Add((ushort)a); idx.Add((ushort)b); idx.Add((ushort)c);
                idx.Add((ushort)b); idx.Add((ushort)d); idx.Add((ushort)c);
            }

            // Per-vertex tangents from the UV+position gradient (Lengyel, shared math), so the tube takes a
            // normal map on the skinned pass. The UV runs U along the tube and V around it, so the tangent
            // follows the run axis (roughly the bend direction) - a sensible TBN for a tentacle/cable.
            var vertsArr = verts.ToArray();
            ComputeTubeTangents(vertsArr, idx);
            return new SkinnedGltfMesh(vertsArr, idx.ToArray(), inverseBind, restPose);
        }

        // Accumulate the Lengyel UV-space tangent direction per vertex over the index list and Gram-Schmidt
        // orthogonalize against the radial normal. Mirrors GltfLoader.LoadSkinned's tangent pass.
        static void ComputeTubeTangents(SkinnedVertex[] verts, List<ushort> idx)
        {
            var tan1 = new Vector3[verts.Length];
            var tan2 = new Vector3[verts.Length];
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                TangentMath.FaceDirections(
                    verts[i0].Position, verts[i1].Position, verts[i2].Position,
                    verts[i0].Uv, verts[i1].Uv, verts[i2].Uv, out Vector3 sdir, out Vector3 tdir);
                tan1[i0] += sdir; tan1[i1] += sdir; tan1[i2] += sdir;
                tan2[i0] += tdir; tan2[i1] += tdir; tan2[i2] += tdir;
            }
            for (int i = 0; i < verts.Length; i++)
                verts[i].Tangent = TangentMath.Resolve(verts[i].Normal, tan1[i], tan2[i], null);
        }

        static Vector3 AlongAxis(Axis axis, float v) => axis switch
        {
            Axis.X => new Vector3(v, 0, 0),
            Axis.Y => new Vector3(0, v, 0),
            _ => new Vector3(0, 0, v),
        };

        static (Vector3, Vector3) PerpAxes(Axis axis) => axis switch
        {
            Axis.X => (Vector3.UnitY, Vector3.UnitZ),
            Axis.Y => (Vector3.UnitZ, Vector3.UnitX),
            _ => (Vector3.UnitX, Vector3.UnitY),
        };
    }
}
