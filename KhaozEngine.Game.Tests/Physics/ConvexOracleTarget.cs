using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;

namespace KhaozEngine.Tests.Physics;

// These snapshots read Bepu's primitives and topology directly. No installed sweep geometry,
// production transform reader, feature enumerator or query proposal supplies oracle geometry.
internal sealed record ConvexOracleTarget(V[] Vertices, int[][] Faces)
{
    internal static ConvexOracleTarget Capture(Shapes shapes, TypedIndex index, RigidPose pose)
    {
        if (index.Type == default(Compound).TypeId)
        {
            ref Compound compound = ref shapes.GetShape<Compound>(index.Index);
            if (compound.Children.Length != 1) throw new ArgumentException("A snapshot needs an exact dispatched leaf.");
            var child = compound.Children[0];
            Pose composed = Composed(new(child.LocalPose.Position, child.LocalPose.Orientation), new(pose.Position, pose.Orientation));
            return Capture(shapes, child.ShapeIndex, new(composed.Position, composed.Orientation));
        }
        var points = new List<Vector3>();
        int[][] faces;
        if (index.Type == default(Box).TypeId)
        {
            ref Box box = ref shapes.GetShape<Box>(index.Index);
            for (int i = 0; i < 8; i++) points.Add(new((i & 1) == 0 ? -box.HalfWidth : box.HalfWidth,
                (i & 2) == 0 ? -box.HalfHeight : box.HalfHeight, (i & 4) == 0 ? -box.HalfLength : box.HalfLength));
            faces = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];
        }
        else if (index.Type == default(ConvexHull).TypeId)
        {
            ref ConvexHull hull = ref shapes.GetShape<ConvexHull>(index.Index);
            faces = new int[hull.FaceToVertexIndicesStart.Length][];
            for (int face = 0; face < faces.Length; face++)
            {
                int start = hull.FaceToVertexIndicesStart[face];
                int end = face + 1 == faces.Length ? hull.FaceVertexIndices.Length : hull.FaceToVertexIndicesStart[face + 1];
                faces[face] = new int[end - start];
                for (int i = start; i < end; i++)
                {
                    hull.GetPoint(hull.FaceVertexIndices[i], out Vector3 p);
                    int id = points.IndexOf(p);
                    if (id < 0) { id = points.Count; points.Add(p); }
                    faces[face][i - start] = id;
                }
            }
        }
        else throw new ArgumentException("Only finite box and hull snapshots are implemented.");
        M represented = Represented(pose.Orientation);
        V translation = V.From(pose.Position);
        return new(Array.ConvertAll(points.ToArray(), p => represented.Apply(V.From(p)) + translation), faces);
    }
}

internal readonly record struct ConvexOracleEvent(bool IsComplete, double Lower, double Upper,
    ConvexSweepStatus Status = ConvexSweepStatus.Unresolved);
