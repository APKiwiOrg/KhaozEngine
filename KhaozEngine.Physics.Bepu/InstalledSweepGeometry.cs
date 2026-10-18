using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Private installation topology. Only the writer publishes the separate posed value.</summary>
internal sealed class InstalledSweepGeometry
{
    readonly Vector3[] _vertices;
    readonly int[][] _faces = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4],
        [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];
    readonly (int A, int B)[] _edges;
    internal Shapes Owner { get; }
    internal TypedIndex Root { get; }
    internal Vector3 Half { get; }
    RigidPose _pose;
    InstalledPoseOperator _operator;
    bool _posed;

    internal InstalledSweepGeometry(Shapes owner, TypedIndex root, Vector3 half)
    {
        Owner = owner; Root = root; Half = half;
        _vertices = new Vector3[8];
        _edges = new (int, int)[12];
        int count = 0;
        for (int i = 0; i < 8; i++)
        {
            _vertices[i] = new((i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((i & bit) == 0) _edges[count++] = (i, i | bit);
        }
    }

    internal Vector3 Vertex(int index) => _vertices[index];
    internal int FaceVertex(int face, int index) => _faces[face][index];
    internal (int A, int B) Edge(int index) => _edges[index];
    internal void PublishPose(RigidPose pose, InstalledPoseOperator map)
    { _pose = pose; _operator = map; _posed = true; }
    internal bool TryPose(RigidPose pose, out InstalledPoseOperator map)
    {
        map = default;
        if (!_posed || pose.Position != _pose.Position || pose.Orientation != _pose.Orientation) return false;
        map = _operator;
        return map.IsResolved;
    }
}

/// <summary>One registration's immutable topology and current mutation-proved pose.</summary>
internal readonly struct InstalledSweepLeaf(InstalledSweepGeometry geometry, RigidPose pose, InstalledPoseOperator map, bool operational = false)
{
    internal InstalledSweepGeometry Geometry { get; } = geometry;
    internal RigidPose Pose { get; } = pose;
    internal InstalledPoseOperator Map { get; } = map;
    internal bool Operational { get; } = operational;
}
