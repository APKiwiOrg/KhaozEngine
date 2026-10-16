using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCertificationTests
{
    [Theory]
    [InlineData("unknown-region", CoverageFailureCode.UnknownRegion)]
    [InlineData("stale-identity", CoverageFailureCode.IdentityMismatch)]
    [InlineData("stale-frame", CoverageFailureCode.StaleFrame)]
    [InlineData("overlap", CoverageFailureCode.OverlappingConformers)]
    [InlineData("capacity", CoverageFailureCode.CapacityExceeded)]
    [InlineData("escaped-core", CoverageFailureCode.CoreEscaped)]
    [InlineData("cap-contact", CoverageFailureCode.CapContact)]
    [InlineData("budget", CoverageFailureCode.RecessionExceeded)]
    [InlineData("vertical", CoverageFailureCode.FoldedPatch)]
    [InlineData("support-gap", CoverageFailureCode.UnsupportedSupport)]
    [InlineData("inside-solid", CoverageFailureCode.EquipmentIntersection)]
    [InlineData("rear-host", CoverageFailureCode.HostIntersection)]
    [InlineData("mirror", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("singular", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("projective", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("nonfinite", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("unabsorbed-error", CoverageFailureCode.UnsupportedTransform)]
    public void Invalid_pose_returns_its_specific_refusal(string kind, CoverageFailureCode expected)
    {
        var (topology, posed, actor, commands, scratch) = Scenario(kind);
        var sentinel = new ModelVertex(new(7, 7, 7), Vector3.UnitY, Vector4.One);
        var final = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
        Assert.False(CoverageEvaluator.TryEvaluateGeometry(topology, posed, actor, commands, scratch, final, out var certificate, out var failure));
        Assert.Equal(expected, failure.Code);
        Assert.Equal(topology.HostIdentity, failure.HostIdentity);
        Assert.Equal(default, certificate);
        Assert.All(final, v => Assert.Equal(sentinel, v));
        if (kind is "capacity") { Assert.Equal(2, failure.Count); Assert.Equal(1, failure.Limit); }
        // The later command by ID is the one that claims an already claimed region.
        else if (kind is "overlap") { Assert.Equal(2u, failure.CommandId); Assert.Equal(2ul, failure.EquipmentInstanceId); }
        else if (kind is not ("mirror" or "singular"))
        {
            // The refusal names the command, and its equipment instance, that cannot be served.
            Assert.Equal(1u, failure.CommandId); Assert.Equal(1ul, failure.EquipmentInstanceId);
        }
        if (kind is "escaped-core" or "budget" or "rear-host" or "vertical") Assert.True(failure.Count > 0);
    }

    [Fact]
    public void Inward_side_is_certified_against_the_real_solid_for_either_winding()
    {
        foreach (bool outward in new[] { false, true })
        {
            var shell = CoverageCommands.Gabled(.1f, .02f, .3f, 1, 1, outward);
            var solid = CoverageCommands.Solid("synthetic-shell", shell.Positions, shell.Indices);
            Assert.True(CoverageSupportSurface.Compile(solid, [new(0, 0, 0, 4)], Matrix4x4.Identity).InwardOfClosedSolid);
            // The flat back is a valid height field, but it is the outer side of the solid.
            var error = Assert.Throws<InvalidDataException>(() => CoverageSupportSurface.Compile(solid, [new(0, 0, 4, 4)], Matrix4x4.Identity));
            Assert.Contains("outer side", error.Message);
        }
        // The panel plate is wound with normals into the solid. Its actual inward side still certifies.
        Assert.True(CoverageCommands.Plate(CoverageFixture.CurvedPanel()).InwardOfClosedSolid);
        // An open sheet keeps its query contract but carries no solid certificate.
        var fixture = CoverageFixture.CurvedPanel();
        var open = CoverageSupportSurface.Compile(CoverageCommands.Solid("synthetic-sheet", fixture.RigidPositions, fixture.SupportIndices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        Assert.False(open.InwardOfClosedSolid);
    }

    [Fact]
    public void Clear_vertices_do_not_certify_an_uncleared_triangle_interior()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 2);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        float clearance = fixture.Commands[0].Clearance;
        // Independent pointwise witness: each receding core point sits exactly one clearance under the actual sheet.
        var candidate = before.ToArray();
        for (int i = 0; i < candidate.Length; i++)
        {
            if (topology.Vertices[i].Strength < 1) continue;
            float target = (float)(SheetHeight(fixture, candidate[i].Position.X, candidate[i].Position.Y) - clearance * Math.Sqrt(1 + .08 * .08)) - 1e-6f;
            if (candidate[i].Position.Z > target) candidate[i].Position.Z = target;
        }
        int moved = 0;
        for (int i = 0; i < candidate.Length; i++)
        {
            if (candidate[i].Position == before[i].Position) continue;
            moved++;
            Assert.True(SheetHeight(fixture, candidate[i].Position.X, candidate[i].Position.Y) - candidate[i].Position.Z >= clearance);
        }
        Assert.True(moved > 1);
        Assert.True(CoverageCommands.Witness(fixture, topology, before, candidate).ClearanceViolations > 0);
        Assert.False(CoverageCertification.ValidateGeometry(topology, before, candidate, Matrix4x4.Identity, commands, new CoverageScratch(topology), out var failure));
        Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Code);
        Assert.True(CoverageEvaluator.TryEvaluateGeometry(topology, fixture.Posed, Matrix4x4.Identity, commands, new CoverageScratch(topology),
            new ModelVertex[topology.VertexCount], out failure), failure.ToString());
    }

    [Fact]
    public void Valid_endpoints_do_not_whitelist_an_invalid_intermediate_blend()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var scratch = new CoverageScratch(topology);
        Matrix4x4[] Palette(float angle) => CoverageFixture.Pose.Select(m => m * Matrix4x4.CreateRotationZ(angle)).ToArray();
        Matrix4x4[] first = Palette(MathF.PI / 2), second = Palette(-MathF.PI / 2);
        var blended = first.Zip(second, (a, b) => Matrix4x4.Lerp(a, b, .5f)).ToArray();
        var final = new ModelVertex[topology.VertexCount];
        foreach (var palette in new[] { first, second })
            Assert.True(CoverageEvaluator.TryEvaluateGeometry(topology, Skin(fixture, palette), Matrix4x4.Identity, commands, scratch, final, out var accepted),
                accepted.ToString());
        Assert.False(CoverageEvaluator.TryEvaluateGeometry(topology, Skin(fixture, blended), Matrix4x4.Identity, commands, scratch, final, out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
    }

    [Fact]
    public void Certification_rejects_tampered_final_surfaces()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var scratch = new CoverageScratch(topology);
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluateGeometry(topology, fixture.Posed, Matrix4x4.Identity, commands, scratch, final, out var failure));
        Assert.True(CoverageCertification.ValidateGeometry(topology, before, final, Matrix4x4.Identity, commands, scratch, out failure));

        var tampered = final.ToArray();
        int protectedVertex = Enumerable.Range(0, final.Length).First(i => topology.Vertices[i].Strength == 0);
        tampered[protectedVertex].Normal = -tampered[protectedVertex].Normal;
        Assert.Equal(CoverageFailureCode.FoldedPatch, Refusal(tampered));

        tampered = final.ToArray();
        var weld = Enumerable.Range(0, final.Length).Where(i => final[i].Position != before[i].Position)
            .GroupBy(i => topology.Vertices[i].PositionGroup).First(g => g.Count() > 1).ToArray();
        tampered[weld[0]].Position.Z -= .001f;
        Assert.Equal(CoverageFailureCode.FoldedPatch, Refusal(tampered));

        Assert.Equal(CoverageFailureCode.EquipmentIntersection, Refusal(before));
        var tight = commands.Select(c => c with { MaxWorldRecession = .05f }).ToArray();
        Assert.False(CoverageCertification.ValidateGeometry(topology, before, final, Matrix4x4.Identity, tight, scratch, out failure));
        Assert.Equal(CoverageFailureCode.RecessionExceeded, failure.Code);
        Assert.Equal(1u, failure.CommandId);

        CoverageFailureCode Refusal(ModelVertex[] surface)
        {
            Assert.False(CoverageCertification.ValidateGeometry(topology, before, surface, Matrix4x4.Identity, commands, scratch, out var refused));
            return refused.Code;
        }
    }

    static (CoverageRefinement, ModelVertex[], Matrix4x4, CoverageCommandPose[], CoverageScratch) Scenario(string kind)
    {
        if (kind == "inside-solid") return InsideSolid();
        var fixture = kind switch
        {
            "overlap" or "capacity" => CoverageFixture.CurvedPanel(level: 1, secondRegion: kind == "capacity"),
            "rear-host" => WithRearSheet(CoverageFixture.CurvedPanel(level: 1), -.16f, .12f),
            _ => CoverageFixture.CurvedPanel(level: 1),
        };
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var support = kind == "support-gap" ? OpenPlate(fixture) : CoverageCommands.Plate(fixture);
        Matrix4x4 actor = kind switch
        {
            "mirror" => Matrix4x4.CreateScale(-1, 1, 1),
            "singular" => Matrix4x4.CreateScale(1, 0, 1),
            _ => Matrix4x4.Identity,
        };
        var commands = CoverageCommands.Poses(fixture, topology, support, actor);
        var posed = fixture.Posed;
        var scratch = new CoverageScratch(topology, kind == "capacity" ? 1 : CoverageScratch.MaxCommands);
        var c = commands[0];
        Matrix4x4 equipment = c.EquipmentWorld;
        switch (kind)
        {
            case "unknown-region": commands[0] = c with { RegionIndex = fixture.Map.Regions.Count }; break;
            case "stale-identity": commands[0] = c with { Topology = CoverageRefinement.Build(fixture.Source, fixture.Map) }; break;
            case "stale-frame": commands[0] = c with { CoverageWorld = equipment * Matrix4x4.CreateTranslation(0, 0, .05f) }; break;
            case "overlap": commands = [c, c with { Id = 2, EquipmentInstanceId = 2 }]; break;
            case "escaped-core": commands[0] = Placed(c, Matrix4x4.CreateTranslation(.9f, 0, 0)); break;
            case "cap-contact": commands[0] = c with { MaxZ = .3f }; break;
            case "budget": commands[0] = c with { MaxWorldRecession = .1f }; break;
            case "rear-host": commands[0] = Placed(c, Matrix4x4.CreateTranslation(0, 0, -.15f)); break;
            case "vertical": posed = Skin(fixture, CoverageFixture.Pose.Select(m => m * Matrix4x4.CreateRotationX(MathF.PI / 2)).ToArray()); break;
            case "projective":
                equipment.M14 = .1f;
                commands[0] = c with { EquipmentWorld = equipment };
                break;
            case "nonfinite":
                var coverage = c.CoverageWorld; coverage.M41 = float.NaN;
                commands[0] = c with { CoverageWorld = coverage };
                break;
            case "unabsorbed-error": commands[0] = c with { Clearance = 1e-6f }; break;
        }
        return (topology, posed, actor, commands, scratch);
    }

    static CoverageCommandPose Placed(CoverageCommandPose command, Matrix4x4 offset)
    {
        Matrix4x4 equipment = command.EquipmentWorld * offset;
        return command with { EquipmentWorld = equipment, CoverageWorld = command.Support.CoverageLocal * equipment };
    }

    static ModelVertex[] Skin(CoverageFixture fixture, Matrix4x4[] palette) =>
        fixture.Source.SkinVertices.ToArray().Select(v => SkinningMath.SkinVertex(v, palette)).ToArray();

    // The displayed plate with one side face missing. Containment of that open shell cannot be certified.
    static CoverageSupportSurface OpenPlate(CoverageFixture fixture) => CoverageSupportSurface.Compile(
        CoverageCommands.Solid("synthetic-open-plate", fixture.RigidPositions, fixture.RigidIndices[..^3]),
        [new(0, 0, 0, fixture.SupportIndices.Length / 3)], Matrix4x4.Identity);

    // Barycentric height of the fixture's actual inward support triangles, independent of production queries.
    static double SheetHeight(CoverageFixture fixture, double x, double y)
    {
        for (int s = 0; s < fixture.SupportIndices.Length; s += 3)
        {
            Vector3 a = fixture.RigidPositions[fixture.SupportIndices[s]], b = fixture.RigidPositions[fixture.SupportIndices[s + 1]],
                c = fixture.RigidPositions[fixture.SupportIndices[s + 2]];
            double area = Orient(a.X, a.Y, b.X, b.Y, c.X, c.Y);
            double u = Orient(b.X, b.Y, c.X, c.Y, x, y) / area, v = Orient(c.X, c.Y, a.X, a.Y, x, y) / area, w = Orient(a.X, a.Y, b.X, b.Y, x, y) / area;
            if (u >= -1e-12 && v >= -1e-12 && w >= -1e-12) return u * a.Z + v * b.Z + w * c.Z;
        }
        throw new InvalidOperationException("Point is outside the support sheet.");
        static double Orient(double ax, double ay, double bx, double by, double cx, double cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    }

    // A protected flat sheet behind the bulge apex, clear of the unconformed panel. Zero skin weights pose it by identity.
    static CoverageFixture WithRearSheet(CoverageFixture fixture, float z, float half)
    {
        var positions = fixture.Source.Positions.ToArray().ToList();
        var skin = fixture.Source.SkinVertices.ToArray().ToList();
        var vertices = fixture.Source.Vertices.ToList();
        var triangles = fixture.Source.Triangles.ToList();
        var topology = fixture.Map.Topology.ToList();
        int slice = vertices.Max(v => v.MaterialSlice) + 1, start = positions.Count;
        foreach (var p in new Vector3[] { new(-half, -half, z), new(half, -half, z), new(half, half, z), new(-half, half, z) })
        {
            int ordinal = positions.Count - start;
            positions.Add(p);
            skin.Add(new SkinnedVertex { Position = p, Normal = Vector3.UnitZ, Color = Vector4.One });
            vertices.Add(new(4, 0, ordinal, -1, slice, ordinal));
            topology.Add(new(4, 0, ordinal, 400 + ordinal));
        }
        triangles.Add(new(4, 0, 0, -1, slice, start, start + 1, start + 2));
        triangles.Add(new(4, 0, 1, -1, slice, start, start + 2, start + 3));
        var source = new CoverageSourceTopology("synthetic-host", CoverageFixture.Hash, positions.ToArray(), vertices, triangles, skin.ToArray());
        var map = new HostCoverageMap(CoverageFixture.Hash, topology, fixture.Map.Regions, fixture.Map.Refinement, fixture.Map.Authoring);
        var rear = fixture with { Source = source, Map = map, Posed = skin.Select(v => SkinningMath.SkinVertex(v, CoverageFixture.Pose)).ToArray() };
        var refined = CoverageRefinement.Build(source, map);
        var before = CoverageCommands.Surface(refined, rear.Posed);
        Assert.Equal(0, CoverageCommands.Witness(rear, refined, before, before).NonadjacentIntersections);
        return rear;
    }

    // A small collar-only host patch wholly inside a thick box, far from every boundary face of that solid.
    static (CoverageRefinement, ModelVertex[], Matrix4x4, CoverageCommandPose[], CoverageScratch) InsideSolid()
    {
        var positions = new List<Vector3>();
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++) positions.Add(new((x - 1) * .5f, (y - 1) * .5f, .5f));
        var triangles = new List<CoverageSourceTriangle>();
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
            {
                int a = y * 3 + x;
                triangles.Add(new(0, 0, triangles.Count, -1, 0, a, a + 1, a + 4));
                triangles.Add(new(0, 0, triangles.Count, -1, 0, a, a + 4, a + 3));
            }
        var skin = positions.Select(p => new SkinnedVertex { Position = p, Normal = Vector3.UnitZ, Color = Vector4.One, BoneWeights = new(1, 0, 0, 0) }).ToArray();
        var source = new CoverageSourceTopology("synthetic-inside", CoverageFixture.Hash, positions.ToArray(),
            positions.Select((_, i) => new CoverageSourceVertex(0, 0, i, -1, 0, i)), triangles, skin);
        var region = new HostCoverageRegion("r0", [new(new(0, 0, 0, 8), CoverageTriangleRole.Collar)],
            positions.Select((_, i) => new CoverageSurfaceStrength(i, i == 4 ? .25f : 0)));
        var map = new HostCoverageMap(CoverageFixture.Hash, positions.Select((_, i) => new CoverageTopologyEntry(0, 0, i, i)), [region], [],
            new("synthetic", "r0"));
        var topology = CoverageRefinement.Build(source, map);
        var box = CoverageCommands.Gabled(0, 0, 1, 2, 2, outward: true);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("synthetic-box", box.Positions, box.Indices), [new(0, 0, 0, 4)], Matrix4x4.Identity);
        var command = new CoverageCommandPose(1, 1, topology, 0, support, Matrix4x4.Identity, Matrix4x4.Identity, -2, 2, .4f, .02f, .8f);
        var posed = skin.Select(v => SkinningMath.SkinVertex(v, [Matrix4x4.Identity])).ToArray();
        return (topology, posed, Matrix4x4.Identity, [command], new CoverageScratch(topology));
    }
}
