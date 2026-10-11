// Foot support certified from the whole support neighborhood. Every expectation is derived from the installed
// geometry: planes through installed float vertices evaluated in double, or closed forms of the installed curved
// shapes. A sweep-dependent contact point is never pinned finer than half the contact skin.
using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class FootSupportNeighborhoodTests
{
    static readonly float CosMaxSlope = MathF.Cos(MathF.PI / 4f);
    const double HalfSkin = ShellMotion.ContactSkin / 2;

    static FootSupportQuery Query(float x, float z = 0, float feetY = 0, float footRadius = 0.2f) =>
        new(new Vector2(x, z), feetY, footRadius, 0.4f, 0.4f, CosMaxSlope);

    static SupportSample Find(FootSupportScene scene, FootSupportQuery query) =>
        FootSupport.Find(null, null, scene.World, scene.Lease, query);

    static void AssertSupport(SupportStatus status, double expected, StaticHandle? support, SupportSample sample)
    {
        Assert.True(sample.Status == status, $"Expected {status}, got {sample}");
        Assert.True(sample.HeightError <= HalfSkin, $"HeightError {sample.HeightError} in {sample}");
        Assert.True(Math.Abs(sample.Height - expected) <= sample.HeightError,
            $"Expected {expected:R}, got {sample.Height:R} +/- {sample.HeightError:R} in {sample}");
        Assert.Equal(support, sample.Static);
    }

    // Heights agree within the sum of their HeightErrors, plus any gap between the two installed geometries.
    static void AssertAgree(SupportSample expected, SupportSample actual, string label, double geometryGap = 0)
    {
        Assert.True(expected.Status == actual.Status, $"{label}: {expected} against {actual}");
        if (expected.Status is not (SupportStatus.Walkable or SupportStatus.Steep)) return;
        Assert.True(
            Math.Abs(expected.Height - actual.Height) <= expected.HeightError + actual.HeightError + geometryGap,
            $"{label}: {expected} against {actual}");
    }

    // #1340. The disc reaches the plateau's edge and the separate steep face below it. The steep face is not
    // walkable, so it cannot lower the plateau, and the walkable plateau wins over its lower steep contribution.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void PlateauBesideSeparateSteepFaceStaysWalkable(SceneVariant variant)
    {
        using FootSupportScene scene = PlateauBesideSteepFace(variant);
        AssertSupport(SupportStatus.Walkable, 0, scene["plateau"], Find(scene, Query(0.01f)));
    }

    // #1333. The same roof as one static and as two statics gives the axis-side plane either way. At 45 degrees
    // each roof plane sits on the slope limit, so both builds agree on whatever status that limit gives. The two
    // builds round their float vertices differently, and at 45 degrees a split box slab's end face is coplanar with
    // the other slab's top. So each build lies within its HeightError of the span of its installed planes facing
    // the axis side, and the builds agree within the sum of their HeightErrors plus the spread of those planes.
    [Theory]
    [InlineData(SceneVariant.Box, 10f)]
    [InlineData(SceneVariant.Box, 30f)]
    [InlineData(SceneVariant.Box, 45f)]
    [InlineData(SceneVariant.Mesh, 10f)]
    [InlineData(SceneVariant.Mesh, 30f)]
    [InlineData(SceneVariant.Mesh, 45f)]
    public void TwoStaticRidgeMatchesTheSingleStatic(SceneVariant variant, float degrees)
    {
        const float x = -0.1f;
        using FootSupportScene one = Ridge(variant, degrees), two = TwoStaticRidge(variant, degrees);
        FootSupportQuery query = Query(x, 0, 0.5f - 0.1f * MathF.Tan(Radians(degrees)));
        SupportSample single = Find(one, query), split = Find(two, query);
        Assert.True(single.Status is SupportStatus.Walkable or SupportStatus.Steep, $"{single}");
        double angle = Radians(degrees);
        var side = new OracleVector(-Math.Sin(angle), Math.Cos(angle), 0);
        (double Lo, double Hi) singlePlanes = AxisSidePlanes(one, ["roof"], side, x);
        (double Lo, double Hi) splitPlanes = AxisSidePlanes(two, ["left", "right"], side, x);
        AssertAgree(single, split, $"{degrees} degrees",
            Math.Max(singlePlanes.Hi, splitPlanes.Hi) - Math.Min(singlePlanes.Lo, splitPlanes.Lo));
        AssertWithin(singlePlanes, single);
        AssertWithin(splitPlanes, split);
    }

    // The span at the axis of every installed face of the named statics whose outward normal is the given direction
    // within binary32 rounding, evaluated in binary64.
    static (double Lo, double Hi) AxisSidePlanes(FootSupportScene scene, string[] names, OracleVector direction,
        double x, double z = 0)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (string name in names)
            foreach (OracleFace face in scene.InstalledFaces(name))
            {
                OracleVector n = face.Normal, v = face.Vertices[0];
                if (OracleVector.Dot(n, direction) < 1 - 1e-6) continue;
                double y = v.Y - (n.X * (x - v.X) + n.Z * (z - v.Z)) / n.Y;
                (lo, hi) = (Math.Min(lo, y), Math.Max(hi, y));
            }
        Assert.True(lo <= hi, "An installed face faces the axis side.");
        return (lo, hi);
    }

    static void AssertWithin((double Lo, double Hi) planes, SupportSample sample)
    {
        Assert.True(sample.HeightError <= HalfSkin, $"HeightError {sample.HeightError} in {sample}");
        Assert.True(sample.Height >= planes.Lo - sample.HeightError && sample.Height <= planes.Hi + sample.HeightError,
            $"Expected [{planes.Lo:R}, {planes.Hi:R}], got {sample.Height:R} +/- {sample.HeightError:R} in {sample}");
    }

    // A walkable 40 degree face rises to a crest at (0, 0.5) and a steep 60 degree face falls beyond it. The disc
    // reaches the crest, so both faces are members and joined. The steep face's own plane and witness lie at or
    // above the crest, but the phase 1 steep rule caps it by every joined rising plane, so it never lifts the body
    // above the walkable face's plane at the axis.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SteepFaceBeyondAWalkableCrestDoesNotLift(SceneVariant variant)
    {
        const float x = -0.1f;
        float walkable = Radians(40), steep = Radians(60);
        Vector3 Point(float px, float py, float pz) => new(px, py, pz);
        float leftY = 0.5f - MathF.Tan(walkable), rightX = 0.4f, rightY = 0.5f - rightX * MathF.Tan(steep);
        using var scene = new FootSupportScene(variant);
        if (variant == SceneVariant.Mesh)
            scene.Mesh("crest", [
                .. FootSupportScene.Quad(Point(-1, leftY, -2), Point(0, 0.5f, -2), Point(-1, leftY, 2),
                    Point(0, 0.5f, 2)),
                .. FootSupportScene.Quad(Point(0, 0.5f, -2), Point(rightX, rightY, -2), Point(0, 0.5f, 2),
                    Point(rightX, rightY, 2)),
            ]);
        else
            scene.Hull("crest", [Point(0, 0.5f, -2), Point(0, 0.5f, 2), Point(-1, leftY, -2), Point(-1, leftY, 2),
                Point(rightX, rightY, -2), Point(rightX, rightY, 2), Point(-1, -0.4f, -2), Point(-1, -0.4f, 2),
                Point(rightX, -0.4f, -2), Point(rightX, -0.4f, 2)]);
        var side = new OracleVector(-Math.Sin(walkable), Math.Cos(walkable), 0);
        SupportSample sample = Find(scene, Query(x, 0, 0.5f - 0.1f * MathF.Tan(walkable)));
        Assert.True(sample.Status == SupportStatus.Walkable, $"Expected Walkable, got {sample}");
        AssertWithin(AxisSidePlanes(scene, ["crest"], side, x), sample);
        Assert.Equal(scene["crest"], sample.Static);
    }

    // A walkable 30 degree slope W2 rises to an edge at the origin, an 8 mm bevel W rises at 15 degrees, and a steep
    // 60 degree face S falls beyond it. The axis is 47.8 mm over W2, where the disc rests on the bevel 4 mm from each
    // of its edges, about 40 micrometres from W2 and S. All three are members. W joins both, but W2 and S are 8 mm
    // apart and not joined. S is joined to a walkable polygon, so it contributes nothing, and the support is W2's
    // plane at the axis rather than W's.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void NarrowBevelDoesNotLiftToTheSteepFace(SceneVariant variant)
    {
        float slope = Radians(30), bevel = Radians(15), steep = Radians(60);
        const float width = 0.008f, foot = 0.2f;
        float x = -(foot * MathF.Sin(bevel) - width / 2);
        Vector3 Point(float px, float py, float pz) => new(px, py, pz);
        float bevelY = width * MathF.Tan(bevel), footX = width + 0.4f, footY = bevelY - 0.4f * MathF.Tan(steep);
        float eaveY = -MathF.Tan(slope);
        using var scene = new FootSupportScene(variant);
        if (variant == SceneVariant.Mesh)
            scene.Mesh("bevel", [
                .. FootSupportScene.Quad(Point(-1, eaveY, -2), Point(0, 0, -2), Point(-1, eaveY, 2), Point(0, 0, 2)),
                .. FootSupportScene.Quad(Point(0, 0, -2), Point(width, bevelY, -2), Point(0, 0, 2),
                    Point(width, bevelY, 2)),
                .. FootSupportScene.Quad(Point(width, bevelY, -2), Point(footX, footY, -2), Point(width, bevelY, 2),
                    Point(footX, footY, 2)),
            ]);
        else
            scene.Hull("bevel", [Point(-1, eaveY, -2), Point(-1, eaveY, 2), Point(0, 0, -2), Point(0, 0, 2),
                Point(width, bevelY, -2), Point(width, bevelY, 2), Point(footX, footY, -2), Point(footX, footY, 2),
                Point(-1, -1, -2), Point(-1, -1, 2), Point(footX, -1, -2), Point(footX, -1, 2)]);
        var side = new OracleVector(-Math.Sin(slope), Math.Cos(slope), 0);
        (double Lo, double Hi) planes = AxisSidePlanes(scene, ["bevel"], side, x);
        SupportSample sample = Find(scene, Query(x, 0, (float)planes.Lo, foot));
        Assert.True(sample.Status == SupportStatus.Walkable, $"Expected Walkable, got {sample}");
        AssertWithin(planes, sample);
        Assert.Equal(scene["bevel"], sample.Static);
    }

    // A one-sided vertical mesh fin in the plane x 0.005 spans Y 0.1 to 0.3 over a floor at 0, its top edge 5 mm
    // from the axis, wound toward the axis or away from it. The probes do not stop on a triangle that does not face
    // against the sweep, so they pass the fin and certify the floor.
    [Theory]
    [InlineData(SceneVariant.Box, true)]
    [InlineData(SceneVariant.Box, false)]
    [InlineData(SceneVariant.Mesh, true)]
    [InlineData(SceneVariant.Mesh, false)]
    public void VerticalFinOverTheAxisSeesTheFloor(SceneVariant variant, bool facesAxis)
    {
        Vector3 outward = facesAxis ? -Vector3.UnitX : Vector3.UnitX;
        Vector3 a = new(0.005f, 0.1f, -2), b = new(0.005f, 0.3f, -2);
        Vector3 c = new(0.005f, 0.1f, 2), d = new(0.005f, 0.3f, 2);
        using FootSupportScene scene = new FootSupportScene(variant).Flat("floor", -4, 6, -5, 5, 0)
            .Mesh("fin", [.. FootSupportScene.Facing(a, b, c, outward), .. FootSupportScene.Facing(b, d, c, outward)]);
        AssertSupport(SupportStatus.Walkable, 0, scene["floor"], Find(scene, Query(0)));
    }

    // The leg probe rests on a tread at Y 0, inside a band reaching 0.1 up, while its lower cap passes 50 micrometres
    // from the nosing of a step whose top at 0.15 lies wholly above the band. The probe's contact is in the band, so
    // the step's member is not a candidate and hides nothing, and the tread is the support.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void NosingBesideAnInBandTreadIsIgnored(SceneVariant variant)
    {
        // The nosing (x, 0.15) lies 0.2 + 5e-5 from the lower cap centre (0, 0.2) of the probe resting at Y 0.
        float nosing = (float)Math.Sqrt(0.20005 * 0.20005 - 0.05 * 0.05);
        using FootSupportScene scene = new FootSupportScene(variant)
            .Flat("tread", -2, 2, -2, 2, 0).Flat("step", nosing, 2, -2, 2, 0.15f);
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.1f, 0.4f, CosMaxSlope);
        AssertSupport(SupportStatus.Walkable, 0, scene["tread"], Find(scene, query));
    }

    // A shelf at Y 0.4007 lies in the 1 mm sweep margin above the band top at 0.4 and stops both probes. Their
    // contacts are above the band, so the floor under the shelf is hidden and the support refuses.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void ContactAboveTheBandRefuses(SceneVariant variant)
    {
        using FootSupportScene scene = new FootSupportScene(variant)
            .Flat("floor", -4, 6, -5, 5, 0).Flat("shelf", -1, 1, -1, 1, 0.4007f);
        SupportSample sample = Find(scene, Query(0));
        Assert.True(sample.Status == SupportStatus.Refused, $"Expected Refused, got {sample}");
    }

    // A four-sided apex at (0, 0.5, 0) whose faces fall 10 degrees. The axis is over the +X face, and the disc
    // reaches the apex, where every face meets every other. The convex joins cap each face at the lowest plane.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void ConvexApexGivesTheMinimumPlane(SceneVariant variant)
    {
        float eave = 0.5f - MathF.Tan(Radians(10));
        Vector3 apex = new(0, 0.5f, 0);
        Vector3[] corners = [new(1, eave, -1), new(1, eave, 1), new(-1, eave, 1), new(-1, eave, -1)];
        using var scene = new FootSupportScene(variant);
        var faces = new Vector3[12];
        for (int i = 0; i < 4; i++)
        {
            scene.Top($"face{i}", apex, corners[i], corners[(i + 1) % 4]);
            FootSupportScene.Facing(apex, corners[i], corners[(i + 1) % 4], Vector3.UnitY).CopyTo(faces, 3 * i);
        }
        if (variant == SceneVariant.Mesh) scene.Mesh("apex", faces);
        else scene.Hull("apex", [apex, .. corners, .. Array.ConvertAll(corners, c => c - new Vector3(0, 0.2f, 0))]);
        const float x = 0.05f, z = 0.03f;
        double expected = double.PositiveInfinity;
        for (int i = 0; i < 4; i++) expected = Math.Min(expected, scene.TopHeightAt($"face{i}", x, z));
        AssertSupport(SupportStatus.Walkable, expected, scene["apex"], Find(scene, Query(x, z, 0.5f)));
    }

    // A saddle vertex at the origin: ridges rise along +X and -X, valleys fall along +Z and -Z. Faces across a ridge
    // are joined and faces across a valley are not, so the face under the axis gives its own plane.
    [Theory]
    [InlineData(0.06f, 0.02f, 0)]
    [InlineData(-0.03f, 0.05f, 1)]
    [InlineData(-0.02f, -0.07f, 2)]
    [InlineData(0.05f, -0.04f, 3)]
    public void SaddleGivesEachFacesPlane(float x, float z, int face)
    {
        Vector3[] ring = [new(1, 0.2f, 0), new(0, -0.2f, 1), new(-1, 0.2f, 0), new(0, -0.2f, -1)];
        using var scene = new FootSupportScene(SceneVariant.Mesh);
        var triangles = new Vector3[12];
        for (int i = 0; i < 4; i++)
        {
            scene.Top($"face{i}", Vector3.Zero, ring[i], ring[(i + 1) % 4]);
            FootSupportScene.Facing(Vector3.Zero, ring[i], ring[(i + 1) % 4], Vector3.UnitY).CopyTo(triangles, 3 * i);
        }
        scene.Mesh("saddle", triangles);
        AssertSupport(SupportStatus.Walkable, scene.TopHeightAt($"face{face}", x, z), scene["saddle"],
            Find(scene, Query(x, z)));
    }

    // A ramp rising 0.5 over 2 sunk 0.05 into a floor at Y 0, so its top crosses the floor at x 0.2. The planes
    // cross, so they are never joined and the higher surface at the axis wins. At the valley axis the leg probe is
    // tangent to both surfaces.
    [Theory]
    [InlineData(SceneVariant.Box, "valley")]
    [InlineData(SceneVariant.Box, "ramp")]
    [InlineData(SceneVariant.Mesh, "valley")]
    [InlineData(SceneVariant.Mesh, "ramp")]
    public void SunkRampTakesTheHigherSurface(SceneVariant variant, string where)
    {
        const float rise = 0.5f, sink = 0.05f;
        float angle = MathF.Atan2(rise, 2);
        using FootSupportScene scene = new FootSupportScene(variant)
            .Flat("floor", -2, 2, -2, 2, 0)
            .Slab("ramp", new Vector3(1, rise / 2 - sink, 0), angle, MathF.Sqrt(4 + rise * rise) / 2, 2);
        float x = where == "ramp" ? 0.3f : (float)(sink / Math.Tan(angle) - 0.2 * Math.Tan(angle / 2.0));
        double floor = scene.TopHeightAt("floor", x, 0), ramp = scene.TopHeightAt("ramp", x, 0);
        AssertSupport(SupportStatus.Walkable, Math.Max(floor, ramp), scene[floor >= ramp ? "floor" : "ramp"],
            Find(scene, Query(x)));
    }

    // An upright cylinder's cap is a walkable tangent element at the base plus the length. Leaning 30 degrees, its
    // side's upper line has a normal 30 degrees above horizontal, steep, and the tangent plane there contains that
    // line, so the support is the line's height under the axis.
    [Fact]
    public void CylinderCapIsWalkableAndSideIsSteep()
    {
        using (FootSupportScene upright = UprightCylinder(new Vector3(0, 0, 0), 0.4f, 0.5f))
            AssertSupport(SupportStatus.Walkable, 0.5f, upright["cylinder"], Find(upright, Query(0.1f, 0, 0.5f)));

        const float radius = 0.3f, length = 2;
        using FootSupportScene leaning = UprightCylinder(Vector3.Zero, radius, length, 30);
        Pose pose = leaning.PoseOf("cylinder");
        FootSupportScene.Point axis = FootSupportScene.Point.Rotate(pose.Orientation, 0, 1, 0);
        FootSupportScene.Point centre = FootSupportScene.Point.Rotate(pose.Orientation, 0, length / 2.0, 0) +
            pose.Position;
        // The upward unit normal perpendicular to the axis in the XY plane, and the side line through centre + r n.
        double norm = Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y);
        double nx = axis.Y / norm, ny = -axis.X / norm;
        double lineX = centre.X + radius * nx, lineY = centre.Y + radius * ny;
        const float x = -0.25f;
        double expected = lineY + (x - lineX) * axis.Y / axis.X;
        Assert.True(ny > 0 && ny < CosMaxSlope, $"The side normal Y {ny} is steep.");
        AssertSupport(SupportStatus.Steep, expected, leaning["cylinder"], Find(leaning, Query(x, 0, 1)));
    }

    // A log of radius 0.3 lying along X. Its top line is a walkable tangent element at its centre plus the radius.
    [Fact]
    public void LyingLogSupportsItsTopLine()
    {
        const float radius = 0.3f, length = 2;
        using FootSupportScene scene = LyingLog(new Vector3(0, 0.3f, 0), radius, length);
        Pose pose = scene.PoseOf("log");
        FootSupportScene.Point centre = FootSupportScene.Point.Rotate(pose.Orientation, 0, length / 2.0, 0) +
            pose.Position;
        AssertSupport(SupportStatus.Walkable, centre.Y + radius, scene["log"], Find(scene, Query(0.25f, 0, 0.6f)));
    }

    // The axis passes 0.35 from the centre of a sphere of radius 0.25, beyond the thin axis probe. The disc meets
    // the sphere where sin(phi) = 0.35 / (0.25 + 0.2), about 51 degrees from the top, so the contact normal is
    // steep. Its tangent plane at the axis is R cos(phi) - r sin(phi)^2 / cos(phi) above the centre. The contact
    // point comes from a sweep, so the height is held to half the contact skin.
    [Fact]
    public void SphereFlankBeyondTheSlopeLimitIsSteep()
    {
        const float radius = 0.25f, foot = 0.2f, offset = 0.35f;
        var centre = new Vector3(0, -0.25f, 0);
        using FootSupportScene scene = Sphere(SceneVariant.Box, centre, radius);
        double sin = offset / ((double)radius + foot), cos = Math.Sqrt(1 - sin * sin);
        Assert.True(cos < CosMaxSlope, "The contact normal is steep.");
        double expected = centre.Y + radius * cos - foot * sin * sin / cos;
        SupportSample sample = Find(scene, Query(offset, 0, 0, foot));
        Assert.True(sample.Status == SupportStatus.Steep, $"Expected Steep, got {sample}");
        Assert.True(sample.HeightError <= HalfSkin, $"{sample}");
        Assert.True(Math.Abs(sample.Height - expected) <= HalfSkin, $"Expected {expected:R}, got {sample}");
        Assert.Equal(scene["sphere"], sample.Static);
    }

    // Two coplanar floors overlap under the axis. Both are members of each probe, and the support is the same
    // value on a repeat and when the floors are installed in the other order. The axis lies in the high X triangle
    // of either mesh quad, so the element is the same whichever floor the tie order names.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void OverlappingCoplanarStaticsAgree(SceneVariant variant)
    {
        using FootSupportScene scene = OverlappingCoplanarFloors(variant);
        SupportSample first = Find(scene, Query(0.4f, 0.4f)), second = Find(scene, Query(0.4f, 0.4f));
        AssertSupport(SupportStatus.Walkable, 0, first.Static, first);
        Assert.Contains(first.Static, new StaticHandle?[] { scene["west"], scene["east"] });
        Assert.Equal(first, second);
        using FootSupportScene reversed = OverlappingCoplanarFloors(variant, eastFirst: true);
        SupportSample other = Find(reversed, Query(0.4f, 0.4f));
        Assert.Equal(first with { Static = null }, other with { Static = null });
    }

    // Three hundred triangles meet at the apex under the axis, over the backend's element capacity. The
    // neighborhood refuses rather than truncating, and the refusal is the support's answer.
    [Fact]
    public void OverCapacityFanRefuses()
    {
        using FootSupportScene scene = OverCapacityFan();
        SupportSample sample = Find(scene, Query(0));
        Assert.True(sample.Status == SupportStatus.Refused, $"Expected Refused, got {sample}");
    }

    // One mesh: a flat floor quad whose diagonal from (2, 0, -2) to (-2, 0, 2) is shared by a third triangle, a
    // walkable fin rising toward (1, 0.5, 1), plus a duplicate of one floor triangle. Each triangle is certified
    // on its own, so the support is the higher surface at the axis.
    [Theory]
    [InlineData(0.1f, 0.1f)]
    [InlineData(-0.3f, -0.1f)]
    [InlineData(-0.05f, -0.05f)]
    public void NonManifoldMeshCertifiesPerTriangle(float x, float z)
    {
        Vector3 a = new(-2, 0, -2), b = new(2, 0, -2), c = new(-2, 0, 2), d = new(2, 0, 2), e = new(1, 0.5f, 1);
        Vector3[] floor = FootSupportScene.Quad(a, b, c, d);
        using FootSupportScene scene = new FootSupportScene(SceneVariant.Mesh)
            .Mesh("mesh", [.. floor, .. FootSupportScene.Facing(b, c, e, Vector3.UnitY), .. floor[..3]])
            .Top("floor", a, b, c).Top("fin", b, c, e);
        // The fin covers only the side of the diagonal where x + z is positive.
        double fin = x + z > 0 ? scene.TopHeightAt("fin", x, z) : double.NegativeInfinity;
        double expected = Math.Max(scene.TopHeightAt("floor", x, z), fin);
        AssertSupport(SupportStatus.Walkable, expected, scene["mesh"], Find(scene, Query(x, z)));
    }

    // Invariant 9. One heightfield with a ridge, a valley, a step and a fan, built as one static, as four and as one
    // static per triangle, gives the same support over a 0.05 grid of axes.
    [Fact]
    public void SupportIsIndependentOfPartition()
    {
        using FootSupportScene whole = PartitionedTerrain(1), quarters = PartitionedTerrain(4),
            each = PartitionedTerrain(TerrainTriangles);
        int walkable = 0;
        for (int i = -15; i <= 15; i++)
            for (int j = -15; j <= 15; j++)
            {
                var query = new FootSupportQuery(new Vector2(0.05f * i, 0.05f * j), 0.25f, 0.2f, 0.6f, 0.6f,
                    CosMaxSlope);
                SupportSample reference = Find(whole, query);
                Assert.True(reference.Status != SupportStatus.Refused, $"{query.Axis}: {reference}");
                if (reference.Status == SupportStatus.Walkable) walkable++;
                AssertAgree(reference, Find(quarters, query), $"Four pieces at {query.Axis}");
                AssertAgree(reference, Find(each, query), $"One static per triangle at {query.Axis}");
            }
        Assert.True(walkable > 0);
    }
}
