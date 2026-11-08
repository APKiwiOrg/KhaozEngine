using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactTerrainHeightBandTests
{
    [Fact]
    public void OutsideCorner29807DoesNotRefuseTheCertifiedCorridor()
    {
        // #1697. The offending cell corner is 0.637377m from the segment, outside its 0.6m projection.
        Vector3 from = new(-35.375f, 1.2349999f, -24.125f), to = new(-34.375f, 0.45624998f, -25.125f);
        var tuning = ContactTerrainFixture.Tuning;
        PhysicsEvidencePoint[][] faces = [
            [new(-35, 2, -23), new(-35, 1.409999966621399, -24), new(-36, 1.7699999809265137, -23)],
            [new(-35, 1.409999966621399, -24), new(-36, 1.25, -24), new(-36, 1.7699999809265137, -23)]];
        Assert.True(Certify(from, to, tuning, -35.5, -23.75, faces));
    }

    [Fact]
    public void InsideCorner28498StillRefusesTheCertifiedCorridor()
    {
        // #1697. This corner lies 0.530330m from the segment and about 0.055m above the band.
        Vector3 from = new(-37.375f, 1.9099998f, -34.125f), to = new(-36.375f, 0.69624996f, -33.125f);
        var tuning = ContactTerrainFixture.Tuning;
        PhysicsEvidencePoint[][] faces = [
            [new(-37, 1.5399999618530273, -34), new(-37, 1.8600000143051147, -35), new(-38, 2.419999837875366, -34)],
            [new(-37, 1.8600000143051147, -35), new(-38, 2.759999990463257, -35), new(-38, 2.419999837875366, -34)]];
        Assert.False(Certify(from, to, tuning, -38, -34.75, faces));
        Assert.True(Certify(from, to, tuning, -38, -34.75, (_, _) => (double)from.Y + tuning.StepHeight));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OneRepresentableHeightViolationInsideOrExactlyAtTheEdgeStillRefuses(bool edge, bool lower)
    {
        // #1697. Both closed height limits retain exact, zero-tolerance edge enforcement.
        Vector3 from = new(0, 0, 0), to = new(1, -1, 0);
        var tuning = ContactTerrainFixture.Tuning with { CapsuleRadius = 0.25f, StepHeight = 0.5f };
        double limit = lower ? -6 : 0.5, outside = lower ? Math.BitDecrement(limit) : Math.BitIncrement(limit);
        double z = edge ? 0.5 : 0.25;
        Assert.True(Certify(from, to, tuning, 0.25, z, (_, _) => limit));
        Assert.False(Certify(from, to, tuning, 0.25, z, (_, _) => outside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeightViolationsStrictlyBeyondTheExactEdgeDoNotRefuse(bool lower)
    {
        // #1697. A tangent point exactly on a height limit is valid.
        Vector3 from = new(0, 0, 0), to = new(1, -1, 0);
        var tuning = ContactTerrainFixture.Tuning with { CapsuleRadius = 0.25f, StepHeight = 0.5f };
        double limit = lower ? -6 : 0.5;
        Assert.True(Certify(from, to, tuning, 0.25, 0.5,
            (_, z) => limit + (lower ? -1 : 1) * (z - 0.5)));
    }

    [Fact]
    public void CircularCapRejectsViolationBetweenOutsidePolygonVertices()
    {
        // #1697. The violating strip's vertices miss the circular cap, but its edge interior enters it.
        Vector3 from = new(0, 0, 0.125f), to = new(1, -1, 0.125f);
        var tuning = ContactTerrainFixture.Tuning with { CapsuleRadius = 0.25f, StepHeight = 0.5f };
        Assert.False(Certify(from, to, tuning, -0.5, 0, (x, _) => 0.5 + (-0.49 - x)));
        Assert.True(Certify(from, to, tuning, -0.5, 0, (x, _) => 0.5 + (-0.51 - x)));
    }

    static bool Certify(Vector3 from, Vector3 to, MoveTuning tuning, double changedX, double changedZ,
        Func<double, double, double> height) => Certify(from, to, tuning, changedX, changedZ, Quad(changedX, changedZ, height));

    static PhysicsEvidencePoint[][] Quad(double x, double z, Func<double, double, double> height)
    {
        PhysicsEvidencePoint a = new(x, height(x, z), z), b = new(x, height(x, z + 0.25), z + 0.25),
            c = new(x + 0.25, height(x + 0.25, z), z), d = new(x + 0.25, height(x + 0.25, z + 0.25), z + 0.25);
        return [[a, b, c], [c, b, d]];
    }

    static bool Certify(Vector3 from, Vector3 to, MoveTuning tuning, double changedX, double changedZ,
        PhysicsEvidencePoint[][] changedFaces)
    {
        float minX = MathF.Floor(MathF.Min(from.X, to.X)) - 1, minZ = MathF.Floor(MathF.Min(from.Z, to.Z)) - 1;
        float maxX = MathF.Ceiling(MathF.Max(from.X, to.X)) + 1, maxZ = MathF.Ceiling(MathF.Max(from.Z, to.Z)) + 1;
        int width = (int)((maxX - minX) * 4), rows = (int)((maxZ - minZ) * 4), cells = width * rows;
        var options = new PhysicsNavBakeOptions(minX, minZ, maxX, maxZ, 0.25f, 10, 14, tuning.MaxSlopeRadians, cells, cells);
        var faces = new List<DescentFace>(); var entries = new List<DescentColumnFace>(); var starts = new int[cells + 1];
        var coverage = new DescentCoverage[cells];
        for (int i = 0; i < cells; i++)
        {
            double x = minX + (i % width) * 0.25, z = minZ + (i / width) * 0.25;
            var triangles = x == changedX && z == changedZ ? changedFaces : Quad(x, z, (_, _) => 0);
            foreach (var triangle in triangles)
            {
                entries.Add(new(faces.Count, 0));
                faces.Add(new(new("ground", faces.Count, 0), new("world", "ground"), true,
                    PhysicsEvidenceKind.Triangle, triangle, default, 0, 0));
            }
            starts[i + 1] = entries.Count; coverage[i] = DescentCoverage.Complete;
        }
        var joins = new int[faces.Count][]; Array.Fill(joins, Array.Empty<int>());
        var evidence = new PhysicsNavDescentEvidence(options, width, rows, faces.ToArray(), starts,
            entries.ToArray(), coverage, [], joins);
        var columns = PhysicsNavColumns.Own(options, width, rows, new int[cells + 1], [], []);
        var ground = new NavAreaFootprint(columns, options, tuning, default);
        return new ContactTerrainDropCorridor(evidence, ground, tuning, from, to, 6).Certify();
    }
}
