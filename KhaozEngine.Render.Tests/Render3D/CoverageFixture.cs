using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D;

// Explicit public-safe geometry and command inputs shared with the solver tests.
internal sealed record CoverageFixture(
    CoverageSourceTopology Source, HostCoverageMap Map, ModelVertex[] Posed,
    Vector3[] RigidPositions, uint[] RigidIndices, uint[] SupportIndices,
    CoverageFixture.Command[] Commands)
{
    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    // The submitted surface is data for the oracle, not its expected geometry result.
    internal uint[] SurfaceIndices { get; init; } = [];
    internal bool[] ProtectedVertices { get; init; } = [];
    internal sealed record Command(uint Id, ulong EquipmentInstanceId, string Region,
        Matrix4x4 CoverageLocal, float CollarWidth, float MinZ, float MaxZ,
        float Clearance, float MaxWorldRecession);

    // Curved plate is thick, with a real static gap near its rim. No UV or texture is required.
    internal static CoverageFixture CurvedPanel(bool seams = true, int level = 2, bool lowerBulge = false,
        bool crossingSegment = false, bool secondRegion = false, bool challengingCollar = false)
    {
        var positions = new List<Vector3>();
        var skin = new List<SkinnedVertex>();
        var vertices = new List<CoverageSourceVertex>();
        var topology = new List<CoverageTopologyEntry>();
        var triangles = new List<CoverageSourceTriangle>();
        var roles = new List<HostCoverageRegionTriangles>();
        var refinements = new List<HostCoverageRefinement>();
        var strengths = new Dictionary<int, float>();
        int slices = seams ? 2 : 1;
        for (int slice = 0; slice < slices; slice++)
        {
            int firstX = seams ? slice * 3 : 0, lastX = seams ? firstX + 3 : 6;
            int start = positions.Count, width = lastX - firstX + 1;
            for (int y = 0; y <= 6; y++)
                for (int x = firstX; x <= lastX; x++)
                {
                    float px = (x - 3) * .4f, py = (y - 3) * .4f;
                    float height = challengingCollar ? .18f - .04f * (px * px + py * py) : x == 3 && y == 3 ? .3f : -.2f;
                    var p = new Vector3(px, py, height
                        + (lowerBulge && y <= 2 ? .1f : 0));
                    positions.Add(p);
                    float weight = x / 6f;
                    skin.Add(new SkinnedVertex
                    {
                        Position = p,
                        Normal = seams && slice == 1 ? Vector3.Normalize(new(.3f, 0, 1)) : Vector3.UnitZ,
                        Color = new(1, .5f, .25f, 1),
                        BoneIndices = new(0, 1, 0, 0),
                        BoneWeights = new(1 - weight, weight, 0, 0),
                        Tangent = new(1, 0, 0, -1),
                    });
                    vertices.Add(new(0, slice, positions.Count - start - 1, -1, slice, positions.Count - start - 1));
                    int surface = y * 7 + x;
                    topology.Add(new(0, slice, positions.Count - start - 1, surface));
                    strengths[surface] = x is 0 or 6 || y is 0 or 6 ? 0 : x >= 2 && x <= 4 && y >= 2 && y <= 4 ? 1 : .5f;
                }
            int ordinal = 0;
            for (int y = 0; y < 6; y++)
                for (int x = firstX; x < lastX; x++)
                {
                    int a = start + y * width + x - firstX, b = a + 1, c = a + width, d = c + 1;
                    Add(a, b, d); Add(a, d, c);
                    void Add(int i, int j, int k)
                    {
                        triangles.Add(new(0, slice, ordinal, -1, slice, i, j, k));
                        var range = new CoverageTriangleRange(0, slice, ordinal++, 1);
                        bool core = strengths[topology[i].SurfaceId] == 1 && strengths[topology[j].SurfaceId] == 1 && strengths[topology[k].SurfaceId] == 1;
                        roles.Add(new(range, core ? CoverageTriangleRole.Core : CoverageTriangleRole.Collar));
                        // Only the interior is refined. Protected neighbors get conforming splits.
                        if (x >= 1 && x <= 4 && y >= 1 && y <= 4) refinements.Add(new(range, level));
                    }
                }
        }
        if (crossingSegment)
        {
            int start = positions.Count;
            foreach (var p in new[] { new Vector3(-.8f, 0, -.2f), new Vector3(.8f, 0, .5f), new Vector3(0, .1f, .5f) })
            {
                positions.Add(p);
                skin.Add(new() { Position = p, Normal = Vector3.UnitY, Color = Vector4.One });
                vertices.Add(new(1, 0, positions.Count - start - 1, -1, slices, positions.Count - start - 1));
                topology.Add(new(1, 0, positions.Count - start - 1, 100 + positions.Count - start - 1));
            }
            triangles.Add(new(1, 0, 0, -1, slices, start, start + 1, start + 2));
        }
        var region = new HostCoverageRegion("r0", roles, strengths.Select(p => new CoverageSurfaceStrength(p.Key, p.Value)));
        var regions = new List<HostCoverageRegion> { region };
        var commands = new List<Command> { new(1, 1, "r0", Matrix4x4.Identity, .4f, -2, 2, .02f, .8f) };
        if (secondRegion)
        {
            // A translated independent patch, with its own zero boundary and source ordinals.
            var other = CurvedPanel(false, level);
            int start = positions.Count;
            for (int i = 0; i < other.Posed.Length; i++)
            {
                Vector3 p = other.Source.Positions[i] + new Vector3(4, 0, 0);
                positions.Add(p);
                var v = other.Source.SkinVertices[i]; v.Position = p; skin.Add(v);
                vertices.Add(new(2, 0, i, -1, slices + 1, i)); topology.Add(new(2, 0, i, 200 + i));
            }
            foreach (var t in other.Source.Triangles) triangles.Add(new(2, 0, t.Triangle, -1, slices + 1, start + t.A, start + t.B, start + t.C));
            regions.Add(new("r1", other.Map.Regions[0].Triangles.Select(t => new HostCoverageRegionTriangles(t.Triangles with { Mesh = 2 }, t.Role)),
                other.Map.Regions[0].Strengths.Select(s => s with { SurfaceId = s.SurfaceId + 200 })));
            refinements.AddRange(other.Map.Refinement.Select(r => r with { Triangles = r.Triangles with { Mesh = 2 } }));
            commands.Add(new(2, 2, "r1", Matrix4x4.CreateTranslation(4, 0, 0), .4f, -2, 2, .02f, .8f));
        }
        var source = new CoverageSourceTopology("synthetic-host", Hash, positions.ToArray(), vertices, triangles, skin.ToArray());
        var map = new HostCoverageMap(Hash, topology, regions, refinements, new("synthetic", "r0"));
        var plate = new[] { new Vector3(-1, -1, .1f), new Vector3(1, -1, .1f), new Vector3(1, 1, .1f),
            new Vector3(-1, 1, .1f), new Vector3(0, 0, .02f), new Vector3(-1, -1, .2f),
            new Vector3(1, -1, .2f), new Vector3(1, 1, .2f), new Vector3(-1, 1, .2f) };
        uint[] support = [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4];
        uint[] rigid = [.. support, 5, 7, 6, 5, 8, 7, 0, 5, 6, 0, 6, 1, 1, 6, 7, 1, 7, 2, 2, 7, 8, 2, 8, 3, 3, 8, 5, 3, 5, 0];
        return new(source, map, skin.Select(v => SkinningMath.SkinVertex(v, Pose)).ToArray(), plate, rigid, support, commands.ToArray());
    }

    internal static Matrix4x4[] Pose => [Matrix4x4.CreateScale(1.1f, .9f, 1.3f),
        Matrix4x4.CreateScale(.8f, 1.2f, .7f) * Matrix4x4.CreateRotationY(.2f) * Matrix4x4.CreateTranslation(.1f, 0, .03f)];

    internal static CoverageFixture ChallengingCollar() => CurvedPanel(challengingCollar: true);

    internal CoverageFixture SecondActor()
    {
        var posed = Posed.ToArray();
        for (int i = 0; i < posed.Length; i++) posed[i].Position += new Vector3(3, 0, 0);
        return this with
        {
            Posed = posed,
            Commands = Commands.Select(c => c with
            {
                CoverageLocal = c.CoverageLocal * Matrix4x4.CreateTranslation(3, 0, 0),
                EquipmentInstanceId = c.EquipmentInstanceId + 10,
            }).ToArray()
        };
    }

    internal static CoverageFixture ClosedEllipsoid(int segments = 12)
    {
        if (segments < 4 || segments % 2 != 0) throw new ArgumentOutOfRangeException(nameof(segments));
        var points = new List<Vector3> { new(0, 1.4f, 0), new(0, -1.4f, 0) };
        for (int ring = 1; ring < 6; ring++)
            for (int s = 0; s < segments; s++)
            {
                float a = MathF.PI * ring / 6, b = MathF.Tau * s / segments;
                points.Add(new(MathF.Sin(a) * MathF.Cos(b), 1.4f * MathF.Cos(a), .65f * MathF.Sin(a) * MathF.Sin(b)));
            }
        var faces = new List<CoverageSourceTriangle>();
        void Add(int a, int b, int c) => faces.Add(new(0, 0, faces.Count, -1, 0, a, b, c));
        for (int s = 0; s < segments; s++)
        {
            int next = (s + 1) % segments;
            Add(0, 2 + next, 2 + s); Add(1, 2 + 4 * segments + s, 2 + 4 * segments + next);
            for (int ring = 0; ring < 4; ring++)
            {
                int a = 2 + ring * segments + s, b = 2 + ring * segments + next;
                Add(a, b, a + segments); Add(b, b + segments, a + segments);
            }
        }
        var skin = points.Select(p => new SkinnedVertex { Position = p, Normal = Vector3.Normalize(p), Color = Vector4.One }).ToArray();
        var source = new CoverageSourceTopology("synthetic-ellipsoid", Hash, points.ToArray(),
            points.Select((_, i) => new CoverageSourceVertex(0, 0, i, -1, 0, i)), faces, skin);
        // Only the front sheet is eligible. The closed rear stays protected.
        bool Front(int i) => i < 2 || (i - 2) % segments <= segments / 2;
        bool Positive(int i) => i >= 2 && (i - 2) % segments > 0 && (i - 2) % segments < segments / 2;
        float Strength(int i) => !Positive(i) ? 0 : Math.Abs(points[i].Y) <= .8f && points[i].Z >= .28f ? 1 : .5f;
        var front = faces.Where(t => Front(t.A) && Front(t.B) && Front(t.C) && (Positive(t.A) || Positive(t.B) || Positive(t.C))).ToArray();
        var region = new HostCoverageRegion("r0", front.Select(t => new HostCoverageRegionTriangles(new(0, 0, t.Triangle, 1),
                Strength(t.A) == 1 && Strength(t.B) == 1 && Strength(t.C) == 1 ? CoverageTriangleRole.Core : CoverageTriangleRole.Collar)),
            front.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Select(i => new CoverageSurfaceStrength(i, Strength(i))));
        var map = new HostCoverageMap(Hash, points.Select((_, i) => new CoverageTopologyEntry(0, 0, i, i)), [region],
            [new(new(0, 0, 0, 1), 4)], new("synthetic", "r0"));
        var panel = CurvedPanel(false);
        return new(source, map, skin.Select(v => SkinningMath.SkinVertex(v, [Matrix4x4.Identity])).ToArray(),
            panel.RigidPositions, panel.RigidIndices, panel.SupportIndices, panel.Commands);
    }
}
