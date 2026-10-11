using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuBox = BepuPhysics.Collidables.Box;
using BepuCompound = BepuPhysics.Collidables.Compound;
using BepuHull = BepuPhysics.Collidables.ConvexHull;
using BepuStaticHandle = BepuPhysics.StaticHandle;
using BepuSim = BepuPhysics.Simulation;
using StaticHandle = KhaozEngine.Physics.StaticHandle;
using CompoundChild = KhaozEngine.Physics.CompoundChild;

namespace KhaozEngine.Tests.Physics;

public enum FootprintPartition { Boxes, Hulls, Compound, Mesh }

/// <summary>Installed float geometry. Capture reads shapes and poses only, never production support kernels.</summary>
internal sealed class SupportFootprintScene : IDisposable
{
    internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
    internal StaticHandle Crate { get; set; }
    internal List<OracleFace> Faces { get; } = [];
    internal List<OracleTangent> Curves { get; } = [];

    internal StaticHandle Install(PhysicsShape shape, Pose pose)
    {
        StaticHandle handle = World.AddStatic(shape, pose);
        Capture(handle);
        return handle;
    }

    internal void Capture(StaticHandle owner)
    {
        Faces.RemoveAll(f => f.Static == owner);
        Curves.RemoveAll(f => f.Static == owner);
        BepuSim sim = (BepuSim)typeof(BepuPhysicsWorld)
            .GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(World)!;
        var handles = (Dictionary<int, (BepuStaticHandle Handle, TypedIndex Shape)>)typeof(BepuPhysicsWorld)
            .GetField("_handles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(World)!;
        sim.Statics.GetDescription(handles[owner.Value].Handle, out StaticDescription description);
        TypedIndex shape = description.Shape;
        if (shape.Type == default(Mesh).TypeId)
        {
            Faces.AddRange(SupportNeighborhoodOracle.InstalledMesh(World, owner));
            return;
        }
        if (shape.Type == default(BepuCompound).TypeId)
        {
            ref BepuCompound compound = ref sim.Shapes.GetShape<BepuCompound>(shape.Index);
            for (int leaf = 0; leaf < compound.Children.Length; leaf++)
            {
                var child = compound.Children[leaf];
                BepuCompound.GetWorldPose(child.LocalPose, description.Pose, out RigidPose pose);
                CapturePolyhedron(sim, child.ShapeIndex, pose, owner, leaf);
            }
        }
        else CapturePolyhedron(sim, shape, description.Pose, owner, 0);
        Curves.AddRange(SupportNeighborhoodOracle.InstalledCurved(World, owner));
    }

    void CapturePolyhedron(BepuSim sim, TypedIndex shape, RigidPose installed, StaticHandle owner, int leaf)
    {
        Pose pose = new(installed.Position, installed.Orientation);
        if (shape.Type == default(BepuBox).TypeId)
        {
            ref BepuBox box = ref sim.Shapes.GetShape<BepuBox>(shape.Index);
            foreach (OracleFace face in SupportNeighborhoodOracle.Box(owner,
                         new Vector3(box.HalfWidth, box.HalfHeight, box.HalfLength), pose))
                Faces.Add(face with { ElementId = leaf * 256 + face.ElementId });
        }
        else if (shape.Type == default(BepuHull).TypeId)
        {
            ref BepuHull hull = ref sim.Shapes.GetShape<BepuHull>(shape.Index);
            for (int face = 0; face < hull.FaceToVertexIndicesStart.Length; face++)
            {
                hull.GetVertexIndicesForFace(face, out var indices);
                var vertices = new OracleVector[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    hull.GetPoint(indices[i], out Vector3 point);
                    vertices[i] = OracleVector.Rotate(pose.Orientation, OracleVector.From(point)) +
                        OracleVector.From(pose.Position);
                }
                Faces.Add(new(owner, leaf * 256 + face, vertices));
            }
        }
    }

    public void Dispose() => World.Dispose();
}

internal static class SupportFootprintScenes
{
    internal static SupportFootprintScene HiddenCrate(FootprintPartition partition, bool reverse = false)
    {
        var scene = new SupportFootprintScene();
        float angle = MathF.PI / 3;
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
        Vector3 top = new(0.2f, 0.25f, 0);
        var shapes = new (PhysicsShape Shape, Pose Pose)[3];
        shapes[0] = (new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        shapes[1] = (new BoxShape(new((0.12f - 0.05f) / 2, 0.05f, 0.1f)),
            Pose.At(new((0.12f + 0.05f) / 2, 0.05f, 0)));
        shapes[2] = (new BoxShape(new(0.05f / MathF.Cos(angle), 0.1f, 0.2f)),
            new(top + Vector3.Transform(new Vector3(0, -0.1f, 0), tilt), tilt));
        int[] order = reverse ? [2, 1, 0] : [0, 1, 2];
        if (partition == FootprintPartition.Compound)
        {
            var children = new CompoundChild[3];
            for (int i = 0; i < order.Length; i++)
                children[i] = new(shapes[order[i]].Shape, shapes[order[i]].Pose);
            scene.Crate = scene.Install(new CompoundShape(children), Pose.Identity);
            return scene;
        }
        foreach (int i in order)
        {
            (PhysicsShape shape, Pose pose) = shapes[i];
            if (partition == FootprintPartition.Hulls)
            {
                var box = (BoxShape)shape;
                shape = new ConvexHullShape(Corners(box.HalfExtents));
            }
            else if (partition == FootprintPartition.Mesh)
            {
                float Height(float x) => 0.25f + MathF.Tan(angle) * (x - 0.2f);
                Vector3[] quad = i switch
                {
                    0 => [new(-2, 0, -2), new(2, 0, -2), new(-2, 0, 2), new(2, 0, 2)],
                    1 => [new(0.05f, 0.1f, -0.1f), new(0.12f, 0.1f, -0.1f),
                        new(0.05f, 0.1f, 0.1f), new(0.12f, 0.1f, 0.1f)],
                    _ => [new(0.15f, Height(0.15f), -0.2f), new(0.25f, Height(0.25f), -0.2f),
                        new(0.15f, Height(0.15f), 0.2f), new(0.25f, Height(0.25f), 0.2f)],
                };
                shape = new TriangleMeshShape(quad, [0, 1, 2, 1, 3, 2]);
                pose = Pose.Identity;
            }
            StaticHandle handle = scene.Install(shape, pose);
            if (i == 1) scene.Crate = handle;
        }
        return scene;
    }

    internal static SupportFootprintScene Fan(int count)
    {
        if (count < 3) throw new ArgumentOutOfRangeException(nameof(count));
        var vertices = new Vector3[count * 3];
        for (int i = 0; i < count; i++)
        {
            float a = 2 * MathF.PI * i / count, b = 2 * MathF.PI * (i + 1) / count;
            vertices[3 * i] = Vector3.Zero;
            vertices[3 * i + 1] = new(2 * MathF.Cos(a), 0, 2 * MathF.Sin(a));
            vertices[3 * i + 2] = new(2 * MathF.Cos(b), 0, 2 * MathF.Sin(b));
        }
        var scene = new SupportFootprintScene();
        int[] indices = new int[vertices.Length];
        for (int i = 0; i < count; i++)
        {
            indices[3 * i] = 3 * i;
            indices[3 * i + 1] = 3 * i + 1;
            indices[3 * i + 2] = 3 * i + 2;
        }
        scene.Install(new TriangleMeshShape(vertices, indices), Pose.Identity);
        Assert.Equal(count, scene.Faces.Count);
        return scene;
    }

    internal static SupportFootprintScene Grid(int cells)
    {
        if (cells < 1) throw new ArgumentOutOfRangeException(nameof(cells));
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                float x0 = -0.2f + 0.4f * x / cells, x1 = -0.2f + 0.4f * (x + 1) / cells;
                float z0 = -0.2f + 0.4f * z / cells, z1 = -0.2f + 0.4f * (z + 1) / cells;
                int start = vertices.Count;
                vertices.AddRange([new(x0, 0, z0), new(x1, 0, z0), new(x0, 0, z1), new(x1, 0, z1)]);
                indices.AddRange([start, start + 1, start + 2, start + 1, start + 3, start + 2]);
            }
        var scene = new SupportFootprintScene();
        scene.Install(new TriangleMeshShape([.. vertices], [.. indices]), Pose.Identity);
        Assert.Equal(2 * cells * cells, scene.Faces.Count);
        return scene;
    }

    /// <summary>Exactly rawMembers admitted surfaces for the standard origin disc and [-0.4, 0.4] band.
    /// Thick slabs keep their bottom and side faces outside the volume. Curved rows count sphere parts.</summary>
    internal static SupportFootprintScene Cluster(int rawMembers, bool curved, FootprintPartition partition)
    {
        if (rawMembers < 1) throw new ArgumentOutOfRangeException(nameof(rawMembers));
        var scene = new SupportFootprintScene();
        PhysicsShape shape = curved ? new SphereShape(0.25f) : partition switch
        {
            FootprintPartition.Hulls => new ConvexHullShape(Corners(new(2, 1, 2))),
            FootprintPartition.Mesh => new TriangleMeshShape([new(-2, 0, -2), new(2, 0, -2), new(0, 0, 2)], [0, 1, 2]),
            _ => new BoxShape(new(2, 1, 2)),
        };
        Pose pose = Pose.At(new(0, curved ? -0.25f : partition == FootprintPartition.Mesh ? 0 : -1, 0));
        if (partition == FootprintPartition.Compound)
        {
            // Split at the released per-compound leaf limit, keeping the query's 257th pair reachable.
            for (int start = 0; start < rawMembers; start += 128)
            {
                var children = new CompoundChild[Math.Min(128, rawMembers - start)];
                for (int i = 0; i < children.Length; i++) children[i] = new(shape, pose);
                scene.Install(new CompoundShape(children), Pose.Identity);
            }
        }
        else for (int i = 0; i < rawMembers; i++) scene.Install(shape, pose);
        return scene;
    }

    static Vector3[] Corners(Vector3 half)
    {
        var points = new Vector3[8];
        for (int i = 0; i < points.Length; i++)
            points[i] = new((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z);
        return points;
    }
}
