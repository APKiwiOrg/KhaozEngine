using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;

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
    readonly Dictionary<string, Pose> _poses = [];
    readonly Dictionary<string, Func<List<OracleFace>>> _faces = [];
    IPhysicsQueryLease? _lease;

    /// <summary>A scene whose world is expressed against <paramref name="origin"/> before any static is added, so
    /// every builder still works in the local frame.</summary>
    internal FootSupportScene(SceneVariant variant, Vector3 origin = default)
    {
        _variant = variant;
        if (origin != Vector3.Zero) World.Rebase(origin);
    }

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
            StaticHandle box = World.AddStatic(new BoxShape(half), new Pose(centre, tilt));
            _statics.Add(name, box);
            _faces.Add(name, () => SupportNeighborhoodOracle.Box(box, half, new Pose(centre, tilt)));
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
        StaticHandle mesh = World.AddStatic(new TriangleMeshShape(vertices, indices), Pose.Identity);
        _statics.Add(name, mesh);
        _faces.Add(name, () => SupportNeighborhoodOracle.InstalledMesh(World, mesh));
        return this;
    }

    internal FootSupportScene Hull(string name, Vector3[] points)
    {
        StaticHandle hull = World.AddStatic(new ConvexHullShape(points), Pose.Identity);
        _statics.Add(name, hull);
        _faces.Add(name, () => SupportNeighborhoodOracle.InstalledHull(World, hull));
        return this;
    }

    /// <summary>The faces of static <paramref name="name"/> read in binary64 from what was installed: a slab box from
    /// its float half extents and pose, a mesh or a hull from the backend's shape. A hull must be the only
    /// static.</summary>
    internal List<OracleFace> InstalledFaces(string name) => _faces[name]();

    /// <summary>Any shape installed at <paramref name="pose"/>. The pose is kept for <see cref="PoseOf"/>.</summary>
    internal FootSupportScene Add(string name, PhysicsShape shape, Pose pose)
    {
        _statics.Add(name, World.AddStatic(shape, pose));
        _poses.Add(name, pose);
        return this;
    }

    /// <summary>The float pose static <paramref name="name"/> was installed with through <see cref="Add"/>.</summary>
    internal Pose PoseOf(string name) => _poses[name];

    /// <summary>The triangle <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/> wound so its front
    /// <c>Cross(C - A, B - A)</c> has a positive dot with <paramref name="outward"/>.</summary>
    internal static Vector3[] Facing(Vector3 a, Vector3 b, Vector3 c, Vector3 outward) =>
        Vector3.Dot(Vector3.Cross(c - a, b - a), outward) > 0 ? [a, b, c] : [a, c, b];

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

    internal static FootSupportScene Floor(SceneVariant variant, Vector3 origin = default) =>
        new FootSupportScene(variant, origin).Flat("floor", -4, 6, -5, 5, 0);

    internal static FootSupportScene PropFloor(SceneVariant variant) =>
        new FootSupportScene(variant).Flat("floor", -2, 2, -2, 2, 0.1f);

    /// <summary>A plane through the origin rising to +X by <paramref name="degrees"/>.</summary>
    internal static FootSupportScene Slope(SceneVariant variant, float degrees) =>
        new FootSupportScene(variant).Slab("slope", Vector3.Zero, Radians(degrees), 2, 2);

    internal static FootSupportScene Lip(SceneVariant variant, Vector3 origin = default) =>
        Floor(variant, origin).Flat("lip", 0, 2, -2, 2, LipTop);

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

    internal const float LedgeTop = 0.6f;
    internal const float EaveUnderside = 2.3f;

    /// <summary>A floor and a "ledge" over x in [1, 5] and z in [-2, 2] whose top at <see cref="LedgeTop"/> is above
    /// the step height. The mesh variant is the top face alone.</summary>
    internal static FootSupportScene Ledge(SceneVariant variant) =>
        Floor(variant).Flat("ledge", 1, 5, -2, 2, LedgeTop);

    /// <summary>A 0.05 m thick "slab" over x and z in [-3, 3] with its top at Y 0. The mesh variant is the top face
    /// alone.</summary>
    internal static FootSupportScene ThinSlab(SceneVariant variant) =>
        new FootSupportScene(variant).Slab("slab", Vector3.Zero, 0, 3, 3, 0.05f);

    /// <summary>A slab whose downward face is centred on <paramref name="underside"/> and rises to +X by
    /// <paramref name="degrees"/>. The box variant is <paramref name="thickness"/> thick above that face. The mesh
    /// variant is the face alone, facing down.</summary>
    internal static FootSupportScene Ceiling(this FootSupportScene scene, string name, Vector3 underside,
        float halfLength, float halfWidth, float degrees = 0, float thickness = 0.2f) =>
        scene.Slab(name, underside, MathF.PI + Radians(degrees), halfLength, halfWidth, thickness);

    /// <summary>A floor, a 4 m "wall" whose face stands at x 2, and an "eave" over the run to it: a ceiling 2 m
    /// long across x whose underside is centred at (1, <see cref="EaveUnderside"/>, 0) and rises toward the wall by
    /// <paramref name="degrees"/>.</summary>
    internal static FootSupportScene Eave(SceneVariant variant, float degrees) =>
        Floor(variant).Wall("wall", 2, 0.3f, 4).Ceiling("eave", new Vector3(1, EaveUnderside, 0), 1, 2, degrees);

    /// <summary>A floor and two 3 m walls meeting in a right angle: "wallX" with its face on the plane x 1 facing -X
    /// and "wallZ" with its face on the plane z 1 facing -Z. The box variant is solid boxes, the mesh variant each
    /// face alone.</summary>
    internal static FootSupportScene InnerCorner(SceneVariant variant)
    {
        FootSupportScene scene = Floor(variant);
        if (variant == SceneVariant.Box)
            return scene.Flat("wallX", 1, 2, -5, 2, 3).Flat("wallZ", -4, 2, 1, 2, 3);
        return scene.Mesh("wallX", Face(new(1, 0, -5), new(1, 0, 1), 3, -Vector3.UnitX))
            .Mesh("wallZ", Face(new(-4, 0, 1), new(1, 0, 1), 3, -Vector3.UnitZ));
    }

    // A vertical quad from the base edge a to b, rising by height, facing outward.
    static Vector3[] Face(Vector3 a, Vector3 b, float height, Vector3 outward)
    {
        var up = new Vector3(0, height, 0);
        return [.. FootSupportScene.Facing(a, b, a + up, outward),
            .. FootSupportScene.Facing(b, b + up, a + up, outward)];
    }

    /// <summary>A floor for x at most 0, then <paramref name="risers"/> risers of <paramref name="riser"/> with
    /// treads <paramref name="tread"/> deep. Riser k, counted from 1, stands at x <c>tread (k - 1)</c> and its
    /// tread tops out at <c>riser k</c>. The top tread is a 2 m landing. The box variant stacks solid boxes on
    /// Y 0, so every nosing belongs to one static. The mesh variant is one static of tread and riser quads.</summary>
    internal static FootSupportScene Stairs(SceneVariant variant, float tread, float riser, int risers,
        float halfWidth = 1)
    {
        float landingEnd = tread * (risers - 1) + 2;
        if (variant == SceneVariant.Box)
        {
            var boxes = new FootSupportScene(variant).Flat("floor", -4, 0, -5, 5, 0);
            for (int k = 1; k <= risers; k++)
                boxes.Flat($"step{k}", tread * (k - 1), landingEnd, -halfWidth, halfWidth, riser * k);
            return boxes;
        }
        var vertices = new List<Vector3>(FootSupportScene.Quad(new(-4, 0, -5), new(0, 0, -5), new(-4, 0, 5),
            new(0, 0, 5)));
        for (int k = 1; k <= risers; k++)
        {
            float x0 = tread * (k - 1), x1 = k == risers ? landingEnd : tread * k;
            float y0 = riser * (k - 1), y1 = riser * k;
            // The riser quad's low Y edge plays the low X edge, so it faces -X toward the climb.
            vertices.AddRange(FootSupportScene.Quad(new(x0, y0, -halfWidth), new(x0, y1, -halfWidth),
                new(x0, y0, halfWidth), new(x0, y1, halfWidth)));
            vertices.AddRange(FootSupportScene.Quad(new(x0, y1, -halfWidth), new(x1, y1, -halfWidth),
                new(x0, y1, halfWidth), new(x1, y1, halfWidth)));
        }
        return new FootSupportScene(variant).Mesh("stairs", [.. vertices]);
    }

    /// <summary>One mesh triangle "incline" through the origin rising to +X by <paramref name="degrees"/>, covering
    /// x in [-10.5, 10.5] along z 0. Its recorded top is the plane through its installed vertices.</summary>
    internal static FootSupportScene Incline(float degrees)
    {
        float grade = MathF.Tan(Radians(degrees));
        Vector3 a = new(-14, -14 * grade, -4), b = new(14, 14 * grade, -4), c = new(0, 0, 12);
        return new FootSupportScene(SceneVariant.Mesh).Mesh("incline", [a, b, c]).Top("incline", a, b, c);
    }

    /// <summary>A wall whose face stands at x <paramref name="x"/>, facing -X, from Y 0 to
    /// <paramref name="height"/> over z in [-<paramref name="halfWidth"/>, <paramref name="halfWidth"/>]. The box
    /// variant is <paramref name="thickness"/> deep toward +X. The mesh variant is the face alone.</summary>
    internal static FootSupportScene Wall(this FootSupportScene scene, string name, float x, float thickness,
        float height = 3, float halfWidth = 5) =>
        scene.Slab(name, new Vector3(x, height / 2, 0), MathF.PI / 2, height / 2, halfWidth, thickness);

    /// <summary>A sphere "sphere" of <paramref name="radius"/> at <paramref name="centre"/>. The box variant is the
    /// curved primitive. The mesh variant is a UV sphere of 8 rings and 16 segments wound outward, with a pole
    /// vertex at exactly <c>centre + (0, radius, 0)</c>.</summary>
    internal static FootSupportScene Sphere(SceneVariant variant, Vector3 centre, float radius)
    {
        var scene = new FootSupportScene(variant);
        if (variant == SceneVariant.Box) return scene.Add("sphere", new SphereShape(radius), Pose.At(centre));
        const int rings = 8, segments = 16;
        Vector3 Ring(int ring, int segment)
        {
            if (ring == 0) return centre + new Vector3(0, radius, 0);
            if (ring == rings) return centre - new Vector3(0, radius, 0);
            float polar = MathF.PI * ring / rings, azimuth = 2 * MathF.PI * (segment % segments) / segments;
            return centre + new Vector3(radius * MathF.Sin(polar) * MathF.Cos(azimuth), radius * MathF.Cos(polar),
                radius * MathF.Sin(polar) * MathF.Sin(azimuth));
        }
        var vertices = new List<Vector3>();
        void Triangle(Vector3 a, Vector3 b, Vector3 c) =>
            vertices.AddRange(FootSupportScene.Facing(a, b, c, (a + b + c) / 3 - centre));
        for (int ring = 0; ring < rings; ring++)
            for (int segment = 0; segment < segments; segment++)
            {
                Vector3 a = Ring(ring, segment), b = Ring(ring, segment + 1);
                Vector3 c = Ring(ring + 1, segment), d = Ring(ring + 1, segment + 1);
                if (ring > 0) Triangle(a, b, c);
                if (ring < rings - 1) Triangle(b, d, c);
            }
        return scene.Mesh("sphere", [.. vertices]);
    }

    /// <summary>A cylinder "cylinder" standing on its base at <paramref name="basePoint"/>, leaning
    /// <paramref name="leanDegrees"/> about Z. Lean 0 stands it upright, so its cap is at
    /// <c>basePoint.Y + length</c>.</summary>
    internal static FootSupportScene UprightCylinder(Vector3 basePoint, float radius, float length,
        float leanDegrees = 0)
    {
        Quaternion lean = leanDegrees == 0
            ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(leanDegrees));
        return new FootSupportScene(SceneVariant.Box)
            .Add("cylinder", new CylinderShape(radius, length), new Pose(basePoint, lean));
    }

    /// <summary>A cylinder "log" lying along X with its middle at <paramref name="centre"/>: rotated 90 degrees about
    /// Z, so its base, half a length along +X from the middle, carries the pose.</summary>
    internal static FootSupportScene LyingLog(Vector3 centre, float radius, float length) =>
        new FootSupportScene(SceneVariant.Box).Add("log", new CylinderShape(radius, length),
            new Pose(centre + new Vector3(length / 2, 0, 0),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)));

    /// <summary>A walkable "plateau" at Y 0 over x in [-2, 0] and a separate static "face" falling at 70 degrees from
    /// the plateau's edge at x 0 to Y -1. The box variant's face is a wedge hull whose back is the vertical plane x 0.
    /// The mesh variant's face is one quad.</summary>
    internal static FootSupportScene PlateauBesideSteepFace(SceneVariant variant)
    {
        var scene = new FootSupportScene(variant).Flat("plateau", -2, 0, -2, 2, 0);
        float foot = 1 / MathF.Tan(Radians(70));
        if (variant == SceneVariant.Mesh)
            return scene.Mesh("face", FootSupportScene.Quad(new(0, 0, -2), new(foot, -1, -2), new(0, 0, 2),
                new(foot, -1, 2)));
        return scene.Hull("face", [new(0, 0, -2), new(0, 0, 2), new(foot, -1, -2), new(foot, -1, 2),
            new(0, -1, -2), new(0, -1, 2)]);
    }

    /// <summary>Two coplanar floors at Y 0 that overlap over x in [-1, 1]: "west" over x in [-2, 1] and "east" over
    /// x in [-1, 2]. <paramref name="eastFirst"/> installs them in the other order.</summary>
    internal static FootSupportScene OverlappingCoplanarFloors(SceneVariant variant, bool eastFirst = false)
    {
        var scene = new FootSupportScene(variant);
        void West() => scene.Flat("west", -2, 1, -2, 2, 0);
        void East() => scene.Flat("east", -1, 2, -2, 2, 0);
        if (eastFirst) { East(); West(); }
        else { West(); East(); }
        return scene;
    }

    /// <summary>The number of triangles in <see cref="OverCapacityFan"/>, over the backend's 256 element cap.</summary>
    internal const int FanTriangles = 300;

    /// <summary>One flat mesh "fan" of <see cref="FanTriangles"/> up-facing triangles meeting at
    /// <paramref name="apex"/>, with a rim of radius 1. A probe touching the apex meets every triangle.</summary>
    internal static FootSupportScene OverCapacityFan(Vector3 apex = default)
    {
        Vector3 Rim(int i) => apex + new Vector3(MathF.Cos(2 * MathF.PI * (i % FanTriangles) / FanTriangles), 0,
            MathF.Sin(2 * MathF.PI * (i % FanTriangles) / FanTriangles));
        var vertices = new Vector3[3 * FanTriangles];
        for (int i = 0; i < FanTriangles; i++)
            FootSupportScene.Facing(apex, Rim(i), Rim(i + 1), Vector3.UnitY).CopyTo(vertices, 3 * i);
        return new FootSupportScene(SceneVariant.Mesh).Mesh("fan", vertices);
    }

    /// <summary>The number of triangles in <see cref="PartitionedTerrain"/>.</summary>
    internal const int TerrainTriangles = 128;

    /// <summary>The height of the <see cref="PartitionedTerrain"/> grid vertex at (x, z): a convex ridge along
    /// x -0.5, a concave valley along z 0.5, a 0.35 step rising across x in [0.25, 0.5] at about 50 degrees,
    /// and a 0.1 spike at (0.75, -0.5) where six triangles fan out.</summary>
    internal static float TerrainHeight(float x, float z) =>
        0.1f - 0.2f * MathF.Abs(x + 0.5f) + 0.2f * MathF.Abs(z - 0.5f) + (x >= 0.5f ? 0.35f : 0) +
        (x == 0.75f && z == -0.5f ? 0.1f : 0);

    /// <summary>One heightfield of <see cref="TerrainTriangles"/> up-facing triangles over x and z in [-1, 1] on a
    /// 0.25 grid, split into <paramref name="pieces"/> mesh statics "piece0", "piece1" and so on by triangle order.
    /// Every piece reads the same float vertices, so pieces share their boundary vertices exactly.
    /// <see cref="TerrainTriangles"/> pieces give one static per triangle.</summary>
    internal static FootSupportScene PartitionedTerrain(int pieces)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pieces, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pieces, TerrainTriangles);
        var grid = new Vector3[9, 9];
        for (int i = 0; i < 9; i++)
            for (int j = 0; j < 9; j++)
            {
                float x = -1 + 0.25f * i, z = -1 + 0.25f * j;
                grid[i, j] = new Vector3(x, TerrainHeight(x, z), z);
            }
        var triangles = new List<Vector3>(3 * TerrainTriangles);
        for (int j = 0; j < 8; j++)
            for (int i = 0; i < 8; i++)
                triangles.AddRange(
                    FootSupportScene.Quad(grid[i, j], grid[i + 1, j], grid[i, j + 1], grid[i + 1, j + 1]));
        var scene = new FootSupportScene(SceneVariant.Mesh);
        var piece = new List<Vector3>();
        for (int p = 0, t = 0; p < pieces; p++)
        {
            piece.Clear();
            for (int end = (p + 1) * TerrainTriangles / pieces; t < end; t++)
                piece.AddRange(triangles.GetRange(3 * t, 3));
            scene.Mesh($"piece{p}", [.. piece]);
        }
        return scene;
    }

    /// <summary>A "floor" at Y 0 over x in [<paramref name="toeX"/> - 8, <paramref name="toeX"/>] and a "face" rising
    /// to +X by <paramref name="degrees"/> from its toe at (<paramref name="toeX"/>, 0) to <paramref name="height"/>,
    /// both over z in [-<paramref name="halfWidth"/>, <paramref name="halfWidth"/>]. The box variant's face is a slab
    /// whose top is the face. The mesh variant is the faces alone.</summary>
    internal static FootSupportScene RisingFace(SceneVariant variant, float degrees, float toeX, float height,
        float halfWidth = 4)
    {
        float angle = Radians(degrees), half = height / (2 * MathF.Sin(angle));
        return new FootSupportScene(variant).Flat("floor", toeX - 8, toeX, -halfWidth, halfWidth, 0)
            .Slab("face", new Vector3(toeX + half * MathF.Cos(angle), half * MathF.Sin(angle), 0), angle, half,
                halfWidth);
    }

    /// <summary>A level "top" at Y 0 over x in [<paramref name="edgeX"/> - 8, <paramref name="edgeX"/>], then a
    /// <paramref name="lip"/> drop at the edge onto a "face" falling to +X by <paramref name="degrees"/> through
    /// <paramref name="depth"/>, and a level "pit" 20 m long at its toe. Zero degrees is a level "shelf" 8 m long at
    /// the lip's foot instead. Everything spans z in [-4, 4]. The mesh variant is the faces alone.</summary>
    internal static FootSupportScene LipOntoFace(SceneVariant variant, float edgeX, float lip, float degrees,
        float depth)
    {
        var scene = new FootSupportScene(variant).Flat("top", edgeX - 8, edgeX, -4, 4, 0);
        if (degrees == 0) return scene.Flat("shelf", edgeX, edgeX + 8, -4, 4, -lip);
        float angle = Radians(degrees), half = depth / (2 * MathF.Sin(angle)), run = depth / MathF.Tan(angle);
        return scene
            .Slab("face", new Vector3(edgeX + half * MathF.Cos(angle), -lip - half * MathF.Sin(angle), 0), -angle,
                half, 4)
            .Flat("pit", edgeX + run, edgeX + run + 20, -4, 4, -lip - depth);
    }

    /// <summary>The crease spacing of <see cref="CreasedCliffHeight"/>.</summary>
    internal const float CreaseSpacing = 4f;

    /// <summary>A piecewise planar cliff creased every <see cref="CreaseSpacing"/> along both axes: its x gradient
    /// alternates 0.5 and 1.5 and its z gradient -4.1 and -2.5 from one crease to the next, so each cell is one of
    /// four planes of 68.6, 71.1, 76.4 and 77.1 degrees and the cliff rises toward -Z.</summary>
    internal static float CreasedCliffHeight(float x, float z) => Creased(x, 0.5f, 1.5f) + Creased(z, -4.1f, -2.5f);

    /// <summary>The normal of the plane <see cref="CreasedCliffHeight"/> has at (x, z), the low crease's plane on a
    /// crease.</summary>
    internal static Vector3 CreasedCliffNormal(float x, float z) =>
        Vector3.Normalize(new Vector3(-CreasedGradient(x, 0.5f, 1.5f), 1, -CreasedGradient(z, -4.1f, -2.5f)));

    static float CreasedGradient(float t, float a, float b)
    {
        float period = 2 * CreaseSpacing;
        return t - MathF.Floor(t / period) * period < CreaseSpacing ? a : b;
    }

    // The integral of a gradient that alternates a and b every crease.
    static float Creased(float t, float a, float b)
    {
        float period = 2 * CreaseSpacing;
        float k = MathF.Floor(t / period), r = t - k * period;
        return k * (a + b) * CreaseSpacing + (r < CreaseSpacing ? a * r : a * CreaseSpacing + b * (r - CreaseSpacing));
    }

    /// <summary>One mesh "cliff" of <see cref="CreasedCliffHeight"/> over x in [-16, 16] and z in [-4, 28], two
    /// triangles to a cell between creases, so every triangle lies in its cell's plane.</summary>
    internal static FootSupportScene CreasedCliff()
    {
        Vector3 Vertex(int i, int j)
        {
            float x = CreaseSpacing * i, z = CreaseSpacing * j;
            return new Vector3(x, CreasedCliffHeight(x, z), z);
        }
        var vertices = new List<Vector3>();
        for (int i = -4; i < 4; i++)
            for (int j = -1; j < 7; j++)
                vertices.AddRange(FootSupportScene.Quad(Vertex(i, j), Vertex(i + 1, j), Vertex(i, j + 1),
                    Vertex(i + 1, j + 1)));
        return new FootSupportScene(SceneVariant.Mesh).Mesh("cliff", [.. vertices]);
    }

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
