using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void ExactEdgeOperandsAreSharedAcrossBothDiscsAndBothProposals()
    {
        Vector3[] vertices = [new(-1, -0.25f, -1), new(1, 0.25f, -1),
            new(1, 0.25f, 1), new(-1, -0.25f, 1)];
        SupportPolygon polygon = PlanarSource(vertices);
        var request = Request();
        int setups = 0;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (phase == ContactAllocationPhase.SupportEdgeSetup && entering) setups++;
        });
        try
        {
            Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(polygon, request, out var member));
            Assert.True(member.HasDiscMaximum);
            Assert.Equal(GeometrySign.Positive, SupportFootprintProposalGeometry.Lowest(polygon,
                request.AxisProposal, out _));
            Assert.Equal(GeometrySign.Positive, SupportFootprintProposalGeometry.Lowest(polygon,
                request.FootProposal, out _));
            Assert.Equal(1, setups);
            var face = new OracleFace(polygon.Static, polygon.ElementId,
                vertices.Select(v => new OracleVector(v.X, v.Y, v.Z)).ToArray());
            CheckMaximum(member, SupportFootprintOracle.Polygon(face, request)!);
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
    }

    [Fact]
    public void ExactEdgeOperandsKeepDistinctAxesAndInterleavedRequestsIndependent()
    {
        Vector3[] Shape() => [new(-1, -0.25f, -1), new(1, 0.25f, -1),
            new(1, 0.25f, 1), new(-1, -0.25f, 1)];
        SupportPolygon polygon = PlanarSource(Shape());
        SupportFootprintRequest initial = Request();
        SupportFootprintRequest separateAxes = initial with
        {
            AxisProposal = initial.AxisProposal with { Start = initial.AxisProposal.Start with { Position = new(0.125f, 0.45f, 0) } },
            FootProposal = initial.FootProposal with { Start = initial.FootProposal.Start with { Position = new(0, 0.7f, 0.125f) } },
        };
        SupportFootprintMember? first = null;
        foreach (SupportFootprintRequest request in new[] { initial, separateAxes, Request(x: 0.125f, radius: 0.25f), initial })
        {
            Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(polygon, request, out var member));
            var fresh = PlanarSource(Shape());
            Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(fresh, request, out var rebuilt));
            Assert.Equal(rebuilt, member);
            foreach (SupportFootprintProposal proposal in new[] { request.AxisProposal, request.FootProposal })
            {
                var unbound = PlanarSource(Shape());
                GeometrySign expected = SupportFootprintProposalGeometry.LowestExhaustive(unbound, proposal, out var control);
                Assert.Equal(expected, SupportFootprintProposalGeometry.LowestExhaustive(polygon, proposal, out var reused));
                Assert.Equal(control, reused);
            }
            if (first is null) first = member;
            else if (request == initial) Assert.Equal(first.Value, member);
        }

        int setups = 0;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (phase == ContactAllocationPhase.SupportEdgeSetup && entering) setups++;
        });
        try
        {
            SupportFootprintPolygon.Measure(polygon, separateAxes, out _);
            SupportFootprintProposalGeometry.LowestExhaustive(polygon, separateAxes.AxisProposal, out _);
            SupportFootprintProposalGeometry.LowestExhaustive(polygon, separateAxes.FootProposal, out _);
            Assert.Equal(3, setups);
            SupportFootprintProposalGeometry.LowestExhaustive(polygon, separateAxes.AxisProposal, out _);
            SupportFootprintProposalGeometry.LowestExhaustive(polygon, separateAxes.FootProposal, out _);
            Assert.Equal(3, setups);
            // A changed proposal is outside the exact request and keeps uncached operand evaluation.
            var foreign = separateAxes.FootProposal with { Distance = MathF.BitIncrement(separateAxes.FootProposal.Distance) };
            SupportFootprintProposalGeometry.LowestExhaustive(polygon, foreign, out _);
            Assert.Equal(7, setups);
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
    }
}
