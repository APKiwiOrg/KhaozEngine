using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>How a scene installs each slab: a solid box, or the two-triangle mesh of that box's top face.</summary>
public enum SceneVariant { Box, Mesh }

/// <summary>Real installed Bepu statics for the foot support suite. Every slab is named so a test can assert
/// which static supports the body.</summary>
internal sealed class FootSupportScene : IDisposable
{
    readonly SceneVariant _variant;
    readonly Dictionary<string, StaticHandle> _statics = [];
    readonly Dictionary<string, Point[]> _tops = [];
    IPhysicsQueryLease? _lease;

    internal FootSupportScene(SceneVariant variant) => _variant = variant;

    internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);

    /// <summary>The scene's read lease, acquired on first use and released with the scene.</summary>
    internal IPhysicsQueryLease Lease => _lease ??= World.AcquireQueryReadLease();

    internal StaticHandle this[string name] => _statics[name];

    /// <summary>A level slab over x in [x0, x1] and z in [z0, z1]. A raised slab rests on Y 0, so its box
    /// centre and half height are exact halves of the top.</summary>
    internal FootSupportScene Flat(string name, float x0, float x1, float z0, float z1, float topY) =>
        Slab(name, new Vector3((x0 + x1) / 2, topY, (z0 + z1) / 2), 0, (x1 - x0) / 2, (z1 - z0) / 2,
            topY > 0 ? topY : 0.2f);

    /// <summary>A slab whose top face is centred on <paramref name="top"/> and rises to +X by
    /// <paramref name="angle"/> radians about Z.</summary>
    internal FootSupportScene Slab(string name, Vector3 top, float angle, float halfLength, float halfWidth,
        float thickness = 0.2f)
    {
        Quaternion tilt = angle == 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
        if (_variant == SceneVariant.Box)
        {
            Vector3 centre = top + Vector3.Transform(new Vector3(0, -thickness / 2, 0), tilt);
            var half = new Vector3(halfLength, thickness / 2, halfWidth);
            _statics.Add(name, World.AddStatic(new BoxShape(half), new Pose(centre, tilt)));
            // The installed top face, from the float pose and half extents, evaluated in double.
            Point BoxCorner(float x, float z) => Point.Rotate(tilt, x * half.X, half.Y, z * half.Z) + centre;
            _tops.Add(name, [BoxCorner(1, -1), BoxCorner(1, 1), BoxCorner(-1, 1)]);
            return this;
        }
        Vector3 Corner(float x, float z) => top + Vector3.Transform(new Vector3(x, 0, z), tilt);
        Vector3[] quad = Quad(Corner(-halfLength, -halfWidth), Corner(halfLength, -halfWidth),
            Corner(-halfLength, halfWidth), Corner(halfLength, halfWidth));
        _tops.Add(name, [new(quad[3]), new(quad[4]), new(quad[5])]);
        return Mesh(name, quad);
    }

    /// <summary>The installed top plane of slab <paramref name="name"/> at (x, z), evaluated in double through
    /// the slab's float vertices as installed. For a mesh that is the high X triangle of its quad.</summary>
    internal double TopHeightAt(string name, double x, double z)
    {
        Point[] p = _tops[name];
        Point u = p[1] - p[0], v = p[2] - p[0];
        double nx = u.Y * v.Z - u.Z * v.Y, ny = u.Z * v.X - u.X * v.Z, nz = u.X * v.Y - u.Y * v.X;
        return p[0].Y - (nx * (x - p[0].X) + nz * (z - p[0].Z)) / ny;
    }

    /// <summary>Records the plane through three installed float vertices as the top of <paramref name="name"/>,
    /// for a static whose builder does not record one.</summary>
    internal FootSupportScene Top(string name, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        _tops.Add(name, [new(p0), new(p1), new(p2)]);
        return this;
    }

    /// <summary>A binary64 point. Float inputs convert exactly.</summary>
    internal readonly record struct Point(double X, double Y, double Z)
    {
        internal Point(Vector3 v) : this(v.X, v.Y, v.Z) { }

        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Point operator +(Point a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        // v + 2w (q x v) + 2 q x (q x v) for the float quaternion as installed.
        internal static Point Rotate(Quaternion q, double x, double y, double z)
        {
            double qx = q.X, qy = q.Y, qz = q.Z, qw = q.W;
            double tx = 2 * (qy * z - qz * y), ty = 2 * (qz * x - qx * z), tz = 2 * (qx * y - qy * x);
            return new(x + qw * tx + (qy * tz - qz * ty), y + qw * ty + (qz * tx - qx * tz),
                z + qw * tz + (qx * ty - qy * tx));
        }
    }

    /// <summary>The two triangles of a quad over XZ. <paramref name="a"/> is the low X, low Z corner,
    /// <paramref name="b"/> high X, low Z, <paramref name="c"/> low X, high Z and <paramref name="d"/> high X,
    /// high Z. This winding faces up, matching the installed corner-feature fixtures.</summary>
    internal static Vector3[] Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => [a, b, c, b, d, c];

    /// <summary>One static triangle mesh of consecutive vertex triples.</summary>
    internal FootSupportScene Mesh(string name, Vector3[] vertices)
    {
        int[] indices = new int[vertices.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        _statics.Add(name, World.AddStatic(new TriangleMeshShape(vertices, indices), Pose.Identity));
        return this;
    }

    internal FootSupportScene Hull(string name, Vector3[] points)
    {
        _statics.Add(name, World.AddStatic(new ConvexHullShape(points), Pose.Identity));
        return this;
    }

    public void Dispose()
    {
        try { _lease?.Dispose(); }
        finally { World.Dispose(); }
    }
}

/// <summary>The suite's scenes. Each takes the variant so a case runs on boxes and meshes alike.</summary>
internal static class FootSupportScenes
{
    internal const float LipTop = 0.0425f;
    internal const float CrateTop = 0.3f;
    internal const float BankTop = 0.25f;

    static FootSupportScene Floor(SceneVariant variant) =>
        new FootSupportScene(variant).Flat("floor", -4, 6, -5, 5, 0);

    internal static FootSupportScene PropFloor(SceneVariant variant) =>
        new FootSupportScene(variant).Flat("floor", -2, 2, -2, 2, 0.1f);

    /// <summary>A plane through the origin rising to +X by <paramref name="degrees"/>.</summary>
    internal static FootSupportScene Slope(SceneVariant variant, float degrees) =>
        new FootSupportScene(variant).Slab("slope", Vector3.Zero, Radians(degrees), 2, 2);

    internal static FootSupportScene Lip(SceneVariant variant) =>
        Floor(variant).Flat("lip", 0, 2, -2, 2, LipTop);

    internal static FootSupportScene Crate(SceneVariant variant) =>
        Floor(variant).Flat("crate", 0, 1, -1, 1, CrateTop);

    /// <summary>Treads 0.35 deep with 0.25 risers. Tread k tops out at 0.25 k from x 0.35 (k - 1).</summary>
    internal static FootSupportScene Treads(SceneVariant variant)
    {
        var scene = new FootSupportScene(variant);
        for (int k = 1; k <= 3; k++)
        {
            float start = 0.35f * (k - 1);
            float end = variant == SceneVariant.Box || k == 3 ? 2 : 0.35f * k;
            scene.Flat($"tread{k}", start, end, -1, 1, 0.25f * k);
        }
        return scene;
    }

    /// <summary>A floor for x at most 0, then a ramp from (0, 0) to (2, <paramref name="rise"/>). The ramp is
    /// installed first when <paramref name="rampFirst"/> is set.</summary>
    internal static FootSupportScene Ramp(SceneVariant variant, float rise, bool rampFirst = false)
    {
        var scene = new FootSupportScene(variant);
        void Floor() => scene.Flat("floor", -2, 0, -2, 2, 0);
        void Ramp() => scene.Slab("ramp", new Vector3(1, rise / 2, 0), MathF.Atan2(rise, 2),
            MathF.Sqrt(4 + rise * rise) / 2, 2);
        if (rampFirst) { Ramp(); Floor(); }
        else { Floor(); Ramp(); }
        return scene;
    }

    /// <summary>One static "roof" with two planes at <paramref name="degrees"/> meeting at a ridge of height 0.5
    /// along x 0, each 2 m along its slope. The box variant is a convex hull, the mesh variant one mesh. The
    /// recorded top of "roof" is its low X plane.</summary>
    internal static FootSupportScene Ridge(SceneVariant variant, float degrees)
    {
        float angle = Radians(degrees);
        float eaveX = 2 * MathF.Cos(angle), eaveY = 0.5f - 2 * MathF.Sin(angle);
        Vector3 Point(float x, float y, float z) => new(x, y, z);
        var scene = new FootSupportScene(variant)
            .Top("roof", Point(0, 0.5f, -2), Point(0, 0.5f, 2), Point(-eaveX, eaveY, 2));
        if (variant == SceneVariant.Mesh)
            return scene.Mesh("roof", [
                .. FootSupportScene.Quad(Point(-eaveX, eaveY, -2), Point(0, 0.5f, -2), Point(-eaveX, eaveY, 2),
                    Point(0, 0.5f, 2)),
                .. FootSupportScene.Quad(Point(0, 0.5f, -2), Point(eaveX, eaveY, -2), Point(0, 0.5f, 2),
                    Point(eaveX, eaveY, 2)),
            ]);
        float baseY = eaveY - 0.2f;
        return scene.Hull("roof", [
            Point(0, 0.5f, -2), Point(0, 0.5f, 2),
            Point(-eaveX, eaveY, -2), Point(-eaveX, eaveY, 2), Point(eaveX, eaveY, -2), Point(eaveX, eaveY, 2),
            Point(-eaveX, baseY, -2), Point(-eaveX, baseY, 2), Point(eaveX, baseY, -2), Point(eaveX, baseY, 2),
        ]);
    }

    /// <summary>The same roof as two separate slabs, "left" and "right", meeting at the ridge.</summary>
    internal static FootSupportScene TwoStaticRidge(SceneVariant variant, float degrees)
    {
        float angle = Radians(degrees);
        Vector3 half = new(MathF.Cos(angle), MathF.Sin(angle), 0);
        return new FootSupportScene(variant)
            .Slab("left", new Vector3(-half.X, 0.5f - half.Y, 0), angle, 1, 2)
            .Slab("right", new Vector3(half.X, 0.5f - half.Y, 0), -angle, 1, 2);
    }

    internal static FootSupportScene Bank(SceneVariant variant) =>
        Floor(variant).Flat("bank", 1, 5, -4, 4, BankTop);

    internal static float Radians(float degrees) => degrees * MathF.PI / 180f;
}

/// <summary>An <see cref="IPhysicsWorld"/> decorator that hides every optional capability of its inner world.
/// The scene owns the inner world, so disposing the decorator does nothing.</summary>
internal sealed class FeaturelessWorld(IPhysicsWorld inner) : IPhysicsWorld
{
    public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) =>
        inner.AddStatic(shape, pose, material);
    public void RemoveStatic(StaticHandle handle) => inner.RemoveStatic(handle);
    public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
        PhysicsMaterial? material = null) => inner.AddDynamic(shape, pose, body, material);
    public void RemoveDynamic(DynamicBodyHandle handle) => inner.RemoveDynamic(handle);
    public Pose GetDynamicPose(DynamicBodyHandle handle) => inner.GetDynamicPose(handle);
    public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) =>
        inner.GetDynamicVelocity(handle, out linear, out angular);
    public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) =>
        inner.SetDynamicVelocity(handle, linear, angular);
    public bool IsAwake(DynamicBodyHandle handle) => inner.IsAwake(handle);
    public ConstraintHandle AddConstraint(in ConstraintDescription description) => inner.AddConstraint(description);
    public void RemoveConstraint(ConstraintHandle handle) => inner.RemoveConstraint(handle);
    public void SetConstraintTarget(ConstraintHandle handle, float target) => inner.SetConstraintTarget(handle, target);
    public void Step(float dt) => inner.Step(dt);
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit,
        QueryFilter filter = default) => inner.Raycast(origin, direction, maxDistance, out hit, filter);
    public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance,
        out SweepHit hit, QueryFilter filter = default) =>
        inner.SweepCapsule(capsule, pose, direction, maxDistance, out hit, filter);
    public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) =>
        inner.ComputePenetration(capsule, pose, out mtv);
    public void Dispose() { }
}
