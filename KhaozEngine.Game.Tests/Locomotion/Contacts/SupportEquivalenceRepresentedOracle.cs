using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Locomotion.Contacts;

// Expected geometry comes only from installed shapes and the independent represented pose oracles.
// Production fields are the subjects of assertions, never inputs to the geometric derivation.
internal static class SupportEquivalenceRepresentedOracle
{
    internal static void AssertAnswer(SupportEquivalenceQueryAnchor q, BepuPhysicsWorld world, IPhysicsQueryLease lease)
    {
        var sim = (BepuPhysics.Simulation)typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        Assert.Equal(1, sim.Statics.Count);
        ref var installed = ref sim.Statics[0];
        StaticHandle owner = new(0);
        var pose = new Pose(installed.Pose.Position, installed.Pose.Orientation);
        List<OracleFace> faces = [];
        IReadOnlyList<SupportFootprintCurvedProofOracle.InstalledPart> curved = [];
        if (q.Row.StartsWith("curved/", StringComparison.Ordinal))
            curved = SupportFootprintCurvedProofOracle.Installed(world, owner);
        else if (q.Row.StartsWith("mesh/", StringComparison.Ordinal))
            faces = SupportNeighborhoodOracle.InstalledMesh(world, owner);
        else if (installed.Shape.Type == default(Box).TypeId)
        {
            ref Box box = ref sim.Shapes.GetShape<Box>(installed.Shape.Index);
            faces = SupportNeighborhoodOracle.Box(owner, new(box.HalfWidth, box.HalfHeight, box.HalfLength), pose);
        }
        else faces = SupportNeighborhoodOracle.InstalledHull(world, owner);
        OracleVector centre = OracleVector.From(q.Pose.Position);
        OracleVector half = new(0, (double)q.Length / 2, 0);
        OracleVector lower = centre - half, upper = centre + half;
        var elements = new SupportElement[256];
        var joins = new ulong[1024];
        var contributions = new SupportContribution[256];
        var capability = (IPhysicsSupportNeighborhood)world;
        var result = capability.QuerySupportNeighborhood(lease, new CapsuleShape(q.Radius, q.Length), q.Pose,
            q.Band, elements, joins, QueryFilter.StaticsOnly);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var actual = elements.AsSpan(0, result.Elements);
        if (curved.Count > 0)
            SupportNeighborhoodOracle.AssertTangentsMatch(actual, curved.Select(p => p.Tangent).ToArray(),
                lower, upper, q.Radius, q.Band);
        else SupportNeighborhoodOracle.AssertMatches(actual, faces, lower, upper, q.Radius, q.Band);
        var normals = new OracleVector[result.Elements];
        var witnesses = new OracleVector[result.Elements];
        var faceRows = new OracleFace?[result.Elements];
        for (int i = 0; i < result.Elements; i++)
        {
            var element = elements[i];
            if (curved.Count > 0)
            {
                var part = curved.Single(p => p.Tangent.ElementId == element.ElementId);
                OracleTangentPoint contact = SupportNeighborhoodOracle.TangentContact(part.Tangent, lower, upper);
                (normals[i], witnesses[i]) = (contact.Normal, contact.Witness);
            }
            else
            {
                OracleFace face = faces.Single(f => f.ElementId == element.ElementId);
                faceRows[i] = face;
                normals[i] = face.Normal;
                witnesses[i] = SupportNeighborhoodOracle.Contact(face, lower, upper, q.Radius).Witness;
                // Rational source plane, independent of rounded published vertices and normals.
                var exact = SupportFootprintExactOracle.Vertices(face);
                var n = SupportFootprintExactOracle.Normal(exact);
                Assert.True(InstalledPoseOracle.V.Dot(n, n) > R.Zero);
            }
        }
        for (int i = 0; i < result.Elements; i++)
        {
            Assert.True((OracleVector.From(elements[i].Normal) - normals[i]).Length <= elements[i].NormalError,
                $"{q.Row} normal {i} misses its published error");
            Assert.True((OracleVector.From(elements[i].Witness) - witnesses[i]).Length <= elements[i].PositionErrorMetres,
                $"{q.Row} witness {i} misses its published error");
        }
        var expectedJoins = new bool[result.Elements, result.Elements];
        for (int i = 0; i < result.Elements; i++)
            for (int j = 0; j < result.Elements; j++)
            {
                bool expected = i != j && faceRows[i] is { } a && faceRows[j] is { } b &&
                    SupportFootprintOracle.Joined(a, b, q.Band);
                expectedJoins[i, j] = expected;
                Assert.Equal(expected, (joins[i * result.JoinWordsPerRow + j / 64] & (1UL << (j % 64))) != 0);
            }
        foreach (float slope in new[] { q.Slope, q.AlternateSlope })
        {
            int count = SupportCertification.CertifyNeighborhood(capability, lease, result, elements, joins,
                q.Axis, slope, contributions);
            Assert.Equal(result.Elements, count);
            var kinds = normals.Select(n => n.Y <= 0 ? CertifiedSupportKind.Refused :
                n.Y >= slope ? CertifiedSupportKind.Walkable : CertifiedSupportKind.Steep).ToArray();
            for (int i = 0; i < count; i++)
            {
                CertifiedSupportKind kind = kinds[i];
                if (kind == CertifiedSupportKind.Steep && Enumerable.Range(0, count)
                    .Any(j => expectedJoins[i, j] && kinds[j] == CertifiedSupportKind.Walkable))
                    kind = CertifiedSupportKind.Refused;
                SupportContribution contribution = contributions[i];
                Assert.Equal(kind, contribution.Kind);
                if (kind == CertifiedSupportKind.Refused) { Assert.Equal(default, contribution); continue; }
                int[] candidates = Enumerable.Range(0, count).Where(j => j == i || expectedJoins[i, j] && kinds[j] == kind).ToArray();
                var plane = candidates.Select(Plane).Aggregate(Minimum);
                Assert.Contains(candidates, j => Plane(j).Lower <= plane.Upper && Plane(j).Upper >= plane.Lower &&
                    elements[j].Normal == contribution.Normal);
                plane = Minimum(plane, SupportFootprintExactOracle.Bounds.Exact(R.From(witnesses[i].Y)));
                Assert.True(R.From(contribution.Lower) <= plane.Lower && R.From(contribution.Upper) >= plane.Upper,
                    $"{q.Row} slope {slope:R}, contribution {i} [{contribution.Lower:R},{contribution.Upper:R}] misses [{plane.Lower},{plane.Upper}]");
                Assert.Equal(elements[i].ElementId, contribution.FeatureId);
                Assert.Equal(elements[i].Witness, contribution.Witness);
            }
        }

        SupportFootprintExactOracle.Bounds Plane(int index)
        {
            if (faceRows[index] is { } face) return SupportFootprintExactOracle.Plane(face, q.Axis.X, q.Axis.Y);
            OracleVector n = normals[index], w = witnesses[index];
            R value = R.From(w.Y) + (R.From(n.X) * (R.From(w.X) - R.From(q.Axis.X)) +
                R.From(n.Z) * (R.From(w.Z) - R.From(q.Axis.Y))) / R.From(n.Y);
            return SupportFootprintExactOracle.Bounds.Exact(value);
        }
    }

    static SupportFootprintExactOracle.Bounds Minimum(SupportFootprintExactOracle.Bounds a, SupportFootprintExactOracle.Bounds b) =>
        new(a.Lower < b.Lower ? a.Lower : b.Lower, a.Upper < b.Upper ? a.Upper : b.Upper);
}
