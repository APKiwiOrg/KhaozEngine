using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class TerrainDependencyTests
{
    [Fact]
    public void WorkerUnionRetainsEveryObservedDependencyKind()
    {
        var e = Evidence();
        var worker = new ContactTerrainEvidenceReads(e);
        using (worker.Observe())
        {
            _ = e.Coverage(0, 0); _ = e.Column(0, 0).Length;
            _ = e.Face(0).Normal; _ = e.Face(0).Points.Length; _ = e.Joins(0).Length;
            ContactTerrainEvidenceReads.ReadBinding(e, 0, 0, 0, new(0, 0, 0, 0, 0));
        }
        var union = new ContactTerrainEvidenceReads(e);
        union.UnionWith(worker);
        Assert.Equal(new[] { (0, 0) }, union.Coverage);
        Assert.Equal(new[] { (0, 0) }, union.Columns);
        Assert.Equal(new[] { 0 }, union.FaceRecords);
        Assert.Equal(new[] { 0 }, union.Adjacency);
        Assert.Equal(new[] { e.Face(0).Key }, union.Normals);
        Assert.Equal(new[] { e.Face(0).Key }, union.Points);
        Assert.Equal(new DescentRawBinding(0, 0, 0, 0, 0), union.Bindings[(0, 0, 0)]);
        Assert.Throws<ArgumentException>(() => union.UnionWith(new(Evidence())));
    }

    [Theory]
    [InlineData("coverage")]
    [InlineData("column")]
    [InlineData("raw")]
    [InlineData("steep")]
    public void ExcludedEvidenceNeverMasqueradesAsAnAbsentSteepFace(string query)
    {
        var e = Evidence().RequireDependencies();
        var refusal = Assert.Throws<PhysicsNavDescentEvidenceException>(() =>
        {
            if (query == "coverage") _ = e.Coverage(1, 0);
            else if (query == "column") _ = e.Column(1, 0).Length;
            else if (query == "raw") _ = e.TryRaw(1, 0, 0, out _);
            else _ = ContactTerrainDropCertificate.HasSteep(e, new(2, 5, 0.75f, MathF.PI / 4),
                new(1.25f, 0, 0.5f), new(1.75f, 0, 0.5f));
        });
        Assert.Equal(PhysicsNavDescentEvidenceReason.DependencyMissing, refusal.Reason);
    }

    static PhysicsNavDescentEvidence Evidence()
    {
        var options = new PhysicsNavBakeOptions(0, 0, 2, 1, 1, 10, 14, 0.7f, 2, 16);
        DescentFace[] faces = [new(new("ground", 0, 0), new("world", "ground"), true,
            PhysicsEvidenceKind.Triangle, [new(0, 0, 0), new(0, 0, 1), new(1, 0, 0)], default, 0, 0)];
        return new(options, 2, 1, faces, [0, 1, 1], [new(0, 0)],
            [DescentCoverage.Complete, DescentCoverage.Excluded], [new(0, 0, 0, 0, 0)], [[]]);
    }
}
