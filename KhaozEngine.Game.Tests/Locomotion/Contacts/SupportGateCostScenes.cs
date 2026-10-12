using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;

namespace KhaozEngine.Tests.Locomotion.Contacts;

internal sealed record SupportCostRow(string Name, bool Dense = false, bool Explicit = false,
    bool ExpectedRefusal = false, bool KnownIncomplete = false)
{
    public Vector3 Feet { get; init; }
    public Vector2 Direction { get; init; } = Vector2.UnitX;
    public float TerrainHeight { get; init; } = -10;
    public float TerrainSlope { get; init; }
    public bool Swimming { get; init; }
    public float ClampMaximumX { get; init; } = float.NaN;
    internal float HeightAt(float x) => Name switch
    {
        "wall retry" => MathF.Max(0, 2 * (x - 0.05f)),
        "slide opposing face" => 2 * MathF.Abs(x),
        "explicit rounding handoff" => x < 128.02f ? -5 : 2.1f,
        "explicit changed-axis rounding" => x < -256.02f ? 1.6f : -5,
        _ => TerrainHeight + TerrainSlope * x,
    };
    internal Vector3 NormalAt(float x) => Name switch
    {
        "wall retry" => x <= 0.05f ? Vector3.UnitY : Vector3.Normalize(new(-2, 1, 0)),
        "slide opposing face" => Vector3.Normalize(new(x >= 0 ? -2 : 2, 1, 0)),
        _ => Normal,
    };
    public float VerticalVelocity { get; init; }
    public bool Grounded { get; init; } = true;
    public bool Analytic { get; init; }
    public Vector3 Normal { get; init; } = Vector3.UnitY;
    public float WaterSurface { get; init; } = float.NaN;
    public float WaterMinX { get; init; } = float.NegativeInfinity;
    public WaterTraversalMode WaterMode { get; init; } = WaterTraversalMode.SurfaceSwimmer;
    public bool FinalClamp { get; init; }
    public bool StairSignal { get; init; }
    public bool MustPass { get; init; } = true;
    public float Dt => 1f / 30f;
    internal MoveState Input => new()
    {
        Position = Feet + new Vector3(0, 0.9f + (Explicit ? 0.001f : 0), 0),
        Grounded = Grounded,
        VerticalVelocity = VerticalVelocity,
        HorizontalVelocity = Direction * 6,
        TimeSinceGrounded = Grounded ? 0 : 1,
        ClimbRate = StairSignal ? 1 : 0,
        ClimbRateEwma = StairSignal ? 1 : 0,
        StepDeltaY = StairSignal ? 0.1f : 0,
        Swimming = Swimming,
        WaterExcursion = Swimming ? WaterExcursionState.Surface : WaterExcursionState.None,
    };
    public MoveState InputState => Input;
    public FootSupportQuery Query => new(new(Feet.X, Feet.Z), Feet.Y, 0.2f, 0.4f, 0.4f,
        MathF.Cos(MathF.PI / 4));
}

internal static class SupportGateCostScenes
{
    internal static readonly MoveTuning Tuning = MoveTuning.Default;
    internal static IEnumerable<object[]> Rows()
    {
        foreach (string shape in new[] { "box", "hull", "mesh" })
        {
            yield return [new SupportCostRow("flat " + shape)];
            yield return [new SupportCostRow("stairs " + shape) { Feet = new(0.525f, 0.5f, 0), StairSignal = true, MustPass = false }];
        }
        yield return [new SupportCostRow("analytic only") { Analytic = true, TerrainHeight = 0 }];
        yield return [new SupportCostRow("analytic plus props") { Analytic = true, TerrainHeight = 0 }];
        yield return [new SupportCostRow("hidden crate") { MustPass = false }];
        foreach (string shape in new[] { "sphere", "capsule", "cylinder" })
            yield return [new SupportCostRow(shape) { MustPass = false }];
        // The complete footprint column over the dense grid holds more than 256 relevant triangles.
        yield return [new SupportCostRow("grid 100", true, ExpectedRefusal: true) { MustPass = false }];
        foreach (int count in new[] { 96, 256, 257, 300 })
            yield return [new SupportCostRow("fan " + count, true, ExpectedRefusal: count > 256)
            { MustPass = count <= 256 }];
        foreach (int count in new[] { 8, 32, 128, 256 })
            foreach (string partition in new[] { "mesh", "compound", "split" })
                yield return [new SupportCostRow($"cluster {count} {partition}", true)];
        yield return [new SupportCostRow("stacked irrelevant layers")];
        yield return [new SupportCostRow("stationary") { Direction = Vector2.Zero }];
        yield return [new SupportCostRow("initial")];
        yield return [new SupportCostRow("wall retry") { Analytic = true, TerrainHeight = 0, Direction = Vector2.Normalize(new(1, 1)), MustPass = false }];
        yield return [new SupportCostRow("pushed seat") { MustPass = false }];
        yield return [new SupportCostRow("descending landing") { Feet = new(0, 0.35f, 0), Grounded = false,
            VerticalVelocity = -15, MustPass = false }];
        yield return [new SupportCostRow("slide opposing face") { Analytic = true, TerrainHeight = 0,
            Feet = new(0.05f, 0.1f, 0), Normal = Vector3.Normalize(new(-2, 1, 0)), MustPass = false }];
        yield return [new SupportCostRow("explicit entry", Explicit: true) { Analytic = true, TerrainHeight = 0 }];
        yield return [new SupportCostRow("explicit rounding handoff", Explicit: true) { Analytic = true,
            TerrainHeight = -5, Feet = new(128, 2.3f, 0), Grounded = false, Swimming = true, VerticalVelocity = -15, WaterSurface = 3, MustPass = false }];
        // Leave the shore before landing so water-origin continuation retains the unrounded integral.
        yield return [new SupportCostRow("explicit changed-axis rounding", Explicit: true) { Analytic = true,
            TerrainHeight = -5, Feet = new(-256, 1.92f, 0), Direction = -Vector2.UnitX, Grounded = false,
            Swimming = true, VerticalVelocity = -15, WaterSurface = 3, WaterMinX = -256.02f }];
        yield return [new SupportCostRow("explicit wade", Explicit: true) { Analytic = true, TerrainHeight = 0,
            WaterSurface = 0.4f }];
        yield return [new SupportCostRow("explicit swim", Explicit: true) { Analytic = true, TerrainHeight = -5,
            WaterSurface = 3, Feet = new(0, 1.92f, 0), Grounded = false, Swimming = true, MustPass = false }];
        yield return [new SupportCostRow("explicit medium descent retry", Explicit: true) { Analytic = true,
            TerrainHeight = 0.4f, TerrainSlope = -1, Normal = Vector3.Normalize(new(1, 1, 0)),
            Feet = new(0, 0.4f, 0), WaterSurface = 0.25f, WaterMode = WaterTraversalMode.DryOnly }];
        yield return [new SupportCostRow("explicit dry swim handoff", Explicit: true) { Analytic = true,
            TerrainHeight = 0, WaterSurface = 3, WaterMinX = 0.1f, MustPass = false }];
        yield return [new SupportCostRow("explicit dry wade handoff", Explicit: true) { Analytic = true,
            TerrainHeight = 0, WaterSurface = 0.4f, WaterMinX = 0.1f, MustPass = false }];
        yield return [new SupportCostRow("final clamp") { Feet = new(0.3f, 0, 0), Direction = Vector2.Zero,
            FinalClamp = true, ClampMaximumX = 0.19f }];
    }

    internal static SupportFootprintScene Build(SupportCostRow row)
    {
        if (row.Name == "hidden crate") return SupportFootprintScenes.HiddenCrate(FootprintPartition.Boxes);
        if (row.Name == "grid 100") return SupportFootprintScenes.Grid(100);
        if (row.Name.StartsWith("fan ", StringComparison.Ordinal))
            return SupportFootprintScenes.Fan(int.Parse(row.Name.AsSpan(4)));
        if (row.Name.StartsWith("cluster ", StringComparison.Ordinal))
        {
            string[] parts = row.Name.Split(' ');
            int count = int.Parse(parts[1]);
            if (parts[2] != "mesh") return SupportFootprintScenes.Cluster(count, false,
                parts[2] == "compound" ? FootprintPartition.Compound : FootprintPartition.Boxes);
            using SupportFootprintScene captured = SupportFootprintScenes.Cluster(count, false, FootprintPartition.Mesh);
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            foreach (OracleFace face in captured.Faces)
            {
                int first = vertices.Count;
                foreach (OracleVector p in face.Vertices) vertices.Add(new((float)p.X, (float)p.Y, (float)p.Z));
                indices.AddRange([first, first + 2, first + 1]);
            }
            var oneMesh = new SupportFootprintScene();
            oneMesh.Install(new TriangleMeshShape([.. vertices], [.. indices]), Pose.Identity);
            return oneMesh;
        }
        var scene = new SupportFootprintScene();
        if (row.Name is "analytic only" or "explicit entry" or "explicit rounding handoff" or "explicit changed-axis rounding" or
            "explicit wade" or "explicit swim" or "explicit medium descent retry" or "explicit dry swim handoff" or
            "explicit dry wade handoff" or "slide opposing face" or "wall retry")
            return scene;
        string kind = row.Name.EndsWith("hull", StringComparison.Ordinal) ? "hull"
            : row.Name.EndsWith("mesh", StringComparison.Ordinal) ? "mesh" : "box";
        if (row.Name.StartsWith("stairs ", StringComparison.Ordinal))
        {
            Flat(scene, kind, -4, 0, 0);
            for (int i = 0; i < 6; i++) Flat(scene, kind, i * 0.35f, (i + 1) * 0.35f, (i + 1) * 0.25f);
        }
        else if (row.Name is "sphere" or "capsule" or "cylinder")
        {
            PhysicsShape shape = row.Name switch
            {
                "sphere" => new SphereShape(0.5f),
                "capsule" => new CapsuleShape(0.5f, 1),
                _ => new CylinderShape(0.5f, 1),
            };
            scene.Install(shape, Pose.At(new(0, row.Name == "sphere" ? -0.5f : -1, 0)));
        }
        else
        {
            Flat(scene, kind, -4, 4, 0);
            if (row.Name == "analytic plus props") Flat(scene, "box", 2, 3, 0.1f);
            if (row.Name == "pushed seat")
            {
                Flat(scene, "box", 0.3f, 3, 0.25f);
                scene.Install(new BoxShape(new(4, 0.1f, 4)), Pose.At(new(0, 1.92f, 0)));
            }
            if (row.Name == "stacked irrelevant layers")
                for (int i = 0; i < 8; i++) Flat(scene, "box", -4, 4, -3 - i * 2);
        }
        return scene;
    }

    static void Flat(SupportFootprintScene scene, string kind, float x0, float x1, float y)
    {
        PhysicsShape shape;
        Pose pose;
        if (kind == "mesh")
        {
            shape = new TriangleMeshShape([new(x0, y, -2), new(x1, y, -2), new(x0, y, 2), new(x1, y, 2)],
                [0, 1, 2, 1, 3, 2]);
            pose = Pose.Identity;
        }
        else
        {
            Vector3 half = new((x1 - x0) / 2, 1, 2);
            if (kind == "hull")
            {
                var points = new Vector3[8];
                for (int i = 0; i < 8; i++) points[i] = new((i & 1) == 0 ? -half.X : half.X,
                    (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
                shape = new ConvexHullShape(points);
            }
            else shape = new BoxShape(half);
            pose = Pose.At(new((x0 + x1) / 2, y - 1, 0));
        }
        scene.Install(shape, pose);
    }

    internal static FootprintOracleSupport Expected(SupportFootprintScene scene, SupportCostRow row) =>
        Expected(scene, row, row.Query);

    internal static FootprintOracleSupport Expected(SupportFootprintScene scene, SupportCostRow row, FootSupportQuery q)
    {
        SupportFootprintRequest request = SupportFootprintTests.Request(q.Axis.X, q.Axis.Y, q.FootRadius,
            q.CosMaxSlope, (double)q.FeetY - q.ReachDown, (double)q.FeetY + q.ReachUp);
        var members = SupportFootprintTests.Members(scene, request);
        FootprintOracleSupport expected = SupportFootprintOracle.Select(members, request);
        float height = row.HeightAt(q.Axis.X);
        Vector3 normal = row.NormalAt(q.Axis.X);
        if (row.Analytic && height >= request.BandLower && height <= request.BandUpper &&
            (expected.Status == SupportStatus.None || height >= expected.Height))
            return new(normal.Y >= q.CosMaxSlope ? SupportStatus.Walkable : SupportStatus.Steep,
                height, height, height, OracleVector.From(normal), new(q.Axis.X, height, q.Axis.Y), null, -1);
        return expected;
    }
}
