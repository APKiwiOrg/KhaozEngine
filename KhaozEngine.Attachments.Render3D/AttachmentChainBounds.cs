using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Measures the load-time culling envelope in the garment's mesh space.</summary>
internal static class AttachmentChainBounds
{
    internal static MeshBounds? Measure(ChainMotionDefinition motion, AttachmentPartResources resources, SkinnedAttachmentUpload upload)
    {
        var names = new Dictionary<string, int>(resources.JointNames.Count, StringComparer.Ordinal);
        for (int bone = 0; bone < resources.JointNames.Count; bone++)
            names.TryAdd(resources.JointNames[bone], bone);

        var tracked = new bool[resources.JointNames.Count];
        var rest = new Matrix4x4[tracked.Length];
        var chains = new List<(ChainDefinition Definition, int[] Bones)>(motion.Chains.Count);
        foreach (ChainDefinition chain in motion.Chains)
        {
            if (Resolve(chain, names, resources.InverseBind, rest) is not { } bones) continue;
            foreach (int bone in bones) tracked[bone] = true;
            chains.Add((chain, bones));
        }
        if (chains.Count == 0) return null;

        float[] offsetSquares = WeightedOffsetSquares(upload, tracked, rest);
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach ((ChainDefinition chain, int[] bones) in chains)
        {
            Vector3 center = rest[bones[0]].Translation;
            float length = 0f, farthestSquared = offsetSquares[bones[0]];
            for (int index = 1; index < bones.Length; index++)
            {
                int bone = bones[index];
                length += Vector3.Distance(rest[bones[index - 1]].Translation, rest[bone].Translation);
                farthestSquared = MathF.Max(farthestSquared, offsetSquares[bone]);
            }

            float tipLength = Vector3.TransformNormal(chain.Tip, rest[bones[^1]]).Length();
            float radius = length + tipLength + MathF.Sqrt(farthestSquared);
            Vector3 extent = new(radius);
            Vector3 low = center - extent, high = center + extent;
            // A reach too large to represent as finite extents must keep the mesh visible.
            if (!Finite(low) || !Finite(high))
                return new MeshBounds(new Vector3(-float.MaxValue), new Vector3(float.MaxValue));
            min = Vector3.Min(min, low);
            max = Vector3.Max(max, high);
        }
        return new MeshBounds(min, max);
    }

    static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    // Binding owns chain refusals. Use only resolvable bind frames when estimating the load-time reach.
    static int[]? Resolve(ChainDefinition chain, Dictionary<string, int> names, IReadOnlyList<Matrix4x4> inverseBind,
        Matrix4x4[] rest)
    {
        var bones = new int[chain.Joints.Count];
        for (int index = 0; index < bones.Length; index++)
        {
            if (!names.TryGetValue(chain.Joints[index], out int bone)
                || !Matrix4x4.Invert(inverseBind[bone], out Matrix4x4 frame)
                || !AttachmentSetBinding.Finite(frame))
                return null;
            rest[bone] = frame;
            bones[index] = bone;
        }
        return bones;
    }

    static float[] WeightedOffsetSquares(SkinnedAttachmentUpload upload, bool[] tracked, Matrix4x4[] rest)
    {
        var farthest = new float[tracked.Length];
        for (int material = 0; material < upload.Handles.Count; material++)
        {
            foreach (ref readonly SkinnedVertex vertex in upload.SourceVertices(material))
            {
                for (int lane = 0; lane < 4; lane++)
                {
                    if (vertex.BoneWeights[lane] <= 0f) continue;
                    int bone = (int)vertex.BoneIndices[lane];
                    if (!tracked[bone]) continue;
                    float offsetSquared = Vector3.DistanceSquared(vertex.Position, rest[bone].Translation);
                    farthest[bone] = MathF.Max(farthest[bone], offsetSquared);
                }
            }
        }
        return farthest;
    }
}
