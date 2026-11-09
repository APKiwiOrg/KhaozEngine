using System.Numerics;

namespace KhaozEngine.Render3D;

public static partial class GltfLoader
{
    // SharpGLTF refuses invalid influences before the decoder runs. Inject values at the same defensive guard
    // without weakening the public import validation or constructing a mutable unchecked model for tests.
    internal static void ValidateSkinJointIndicesForTests(Vector4 boneIndices, int jointCount,
        string identity, int vertexIndex, int partIndex)
        => ValidateSkinJointIndices(boneIndices, jointCount, identity, vertexIndex, partIndex);

    static void ValidateSkinJointIndices(Vector4 boneIndices, int jointCount,
        string identity, int vertexIndex, int partIndex)
    {
        if (!SkinningMath.AreBoneIndicesValid(boneIndices, jointCount))
            throw new GltfImportStateException(GltfImportRefusal.SkinJointIndexOutsideRange,
                $"glTF JOINTS_0 references a bone index outside [0,{jointCount}): {identity}")
            {
                Identity = identity,
                JointCount = jointCount,
                BoneIndices = boneIndices,
                VertexIndex = vertexIndex,
                PartIndex = partIndex
            };
    }
}
