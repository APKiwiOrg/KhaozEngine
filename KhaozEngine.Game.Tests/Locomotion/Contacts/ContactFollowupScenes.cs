using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>The #1429 plank and #1455 lying-log traversal scenes. Each has a level floor at Y 0 and its obstacle
/// installed before any lease. The poses are the ones the footprint suites pin: Task 1's resting plate at yaw 0.77 and
/// the standard float quarter turns of a 1 m lying cylinder or capsule. Expected heights come from an identical
/// installation read by the independent footprint oracle, never from production selection.</summary>
internal static class ContactFollowupScenes
{
    internal const float PlateYaw = 0.77f;

    /// <summary>The plate's centre in XZ.</summary>
    internal static readonly Vector2 PlateCentre = new(0.1f, 0.05f);

    /// <summary>Compound children past the floor's leaf carry element IDs from this value.</summary>
    internal const int CompoundLeaf = 256;

    /// <summary>A 0.5 m square, 0.1 m thick plate at yaw 0.77 and <paramref name="pitch"/>, one bottom corner resting on
    /// the floor. It is one compound with the floor or a standalone static over a separate floor. The direction is its
    /// rotated local +Z in XZ, which runs along the pitch.</summary>
    internal static (FootSupportScene Scene, StaticHandle Obstacle, Vector2 Direction) Plate(float pitch, bool compound)
    {
        (PhysicsShape Shape, Pose Pose)[] installs = PlateInstalls(pitch, compound);
        var scene = new FootSupportScene(SceneVariant.Box);
        StaticHandle obstacle = default;
        for (int i = 0; i < installs.Length; i++)
        {
            string name = i == installs.Length - 1 ? "plate" : "floor";
            scene.Add(name, installs[i].Shape, installs[i].Pose);
            obstacle = scene[name];
        }
        Vector3 along = Vector3.Transform(Vector3.UnitZ, PlateRotation(pitch));
        return (scene, obstacle, Vector2.Normalize(new Vector2(along.X, along.Z)));
    }

    /// <summary>A 1 m lying cylinder or capsule of <paramref name="radius"/> on the floor, a quarter turn about X or Z as
    /// installed floats. The direction crosses it: +X for a turn about X, +Z for a turn about Z.</summary>
    internal static (FootSupportScene Scene, StaticHandle Obstacle, Vector2 Direction) Log(bool capsule, float radius,
        bool aboutX)
    {
        (PhysicsShape Shape, Pose Pose)[] installs = LogInstalls(capsule, radius, aboutX);
        var scene = new FootSupportScene(SceneVariant.Box);
        scene.Add("floor", installs[0].Shape, installs[0].Pose);
        scene.Add("log", installs[1].Shape, installs[1].Pose);
        return (scene, scene["log"], aboutX ? Vector2.UnitX : Vector2.UnitY);
    }

    /// <summary>The oracle's support for <paramref name="query"/> over an identical plate installation.</summary>
    internal static FootprintOracleSupport PlateSupport(float pitch, bool compound, in FootSupportQuery query) =>
        Oracle(PlateInstalls(pitch, compound), query);

    /// <summary>The oracle's support for <paramref name="query"/> over an identical log installation.</summary>
    internal static FootprintOracleSupport LogSupport(bool capsule, float radius, bool aboutX, in FootSupportQuery query) =>
        Oracle(LogInstalls(capsule, radius, aboutX), query, curvedFromInstalledParts: true);

    static Quaternion PlateRotation(float pitch) => Quaternion.CreateFromYawPitchRoll(PlateYaw, pitch, 0);

    // SupportFootprintTests.PitchedPlate's resting placement, compound or standalone. The plate is installed last.
    static (PhysicsShape, Pose)[] PlateInstalls(float pitch, bool compound)
    {
        float y = (float)(1 + Math.Cos(pitch) * 0.05 + Math.Abs(Math.Sin(pitch)) * 0.25);
        var local = new Pose(new(PlateCentre.X, y, PlateCentre.Y), PlateRotation(pitch));
        var floor = new BoxShape(new(2, 1, 2));
        var plate = new BoxShape(new(0.25f, 0.05f, 0.25f));
        if (compound)
            return [(new CompoundShape([new CompoundChild(floor, Pose.Identity), new CompoundChild(plate, local)]),
                Pose.At(new(0, -1, 0)))];
        return [(floor, Pose.At(new(0, -1, 0))),
            (plate, local with { Position = local.Position + new Vector3(0, -1, 0) })];
    }

    // SupportFootprintTests.InstallLyingLog over its floor.
    static (PhysicsShape, Pose)[] LogInstalls(bool capsule, float radius, bool aboutX)
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(aboutX ? Vector3.UnitX : Vector3.UnitZ, MathF.PI / 2);
        Vector3 centre = new(0, radius, 0);
        PhysicsShape log = capsule ? new CapsuleShape(radius, 1) : new CylinderShape(radius, 1);
        return [(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, -0.1f, 0))),
            (log, new(capsule ? centre : centre - Vector3.Transform(new Vector3(0, 0.5f, 0), rotation), rotation))];
    }

    // A log's parts are read from its installed float pose, including the composed centre, as
    // SupportFootprintTests.LyingLogCrest does, rather than from a double recomposition of the authored pose.
    static FootprintOracleSupport Oracle((PhysicsShape Shape, Pose Pose)[] installs, in FootSupportQuery query,
        bool curvedFromInstalledParts = false)
    {
        using var twin = new SupportFootprintScene();
        StaticHandle last = default;
        foreach (var (shape, pose) in installs) last = twin.Install(shape, pose);
        SupportFootprintRequest request = FootSupport.DescribeFootprint(query);
        var members = SupportFootprintTests.Members(twin, request);
        if (curvedFromInstalledParts)
        {
            members = members.Where(m => m.Static != last).ToList();
            foreach (var part in SupportFootprintCurvedProofOracle.Installed(twin.World, last))
                if (SupportFootprintOracle.Curved(part.Tangent, request) is { } member) members.Add(member);
            members = members.OrderBy(m => m.Static.Value).ThenBy(m => m.ElementId).ToList();
        }
        return SupportFootprintOracle.Select(members, request);
    }
}
