using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class ArrivalStaticEvidenceTests(ITestOutputHelper output)
{
    [Fact]
    public void CertifiedSlopeSkipsExcludedGroundExpansionAndDistantOwners()
    {
        // #1753, #1754. Supported arrivals must share the indexed, exclusion-first clearance proof.
        using var scene = new FlatArrivalScene(slope: 0.125f, install: (world, builder) =>
        {
            var vertices = new Vector3[32 * 32 * 6];
            var indices = new int[vertices.Length];
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 32; x++)
                {
                    float left = -8 + x * 0.5f, near = -8 + z * 0.5f;
                    int at = (z * 32 + x) * 6;
                    vertices[at] = new(left, left * 0.125f, near);
                    vertices[at + 1] = new(left, left * 0.125f, near + 0.5f);
                    vertices[at + 2] = new(left + 0.5f, (left + 0.5f) * 0.125f, near + 0.5f);
                    vertices[at + 3] = vertices[at];
                    vertices[at + 4] = vertices[at + 2];
                    vertices[at + 5] = new(left + 0.5f, (left + 0.5f) * 0.125f, near);
                }
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            var ground = world.AddStatic(new TriangleMeshShape(vertices, indices), Pose.Identity);
            builder.Add(ground, 0, 2047, new("world", "ground"), new(-8, -1, -8), new(8, 1, 8)).Exclude(ground);
            for (int i = 0; i < 256; i++)
                world.AddStatic(new BoxShape(Vector3.One), Pose.At(new(10000 + i * 4, 0, 0)));
        });
        MoveTuning tuning = scene.Tuning;
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        const float dt = 0.1f;
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, dt, tuning);
        Assert.True(predicted.SupportGranted);
        Assert.True(predicted.Position.Y > body.Position.Y);
        var target = ReachTarget.Point(predicted.Position);
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0.1f, false, dt, scene.Context,
            Vector2.UnitX, predicted, ArrivalRangeCrossingTests.Admit, out int reference);
        Vector2 actual;
        using (var evidence = new StaticEvidenceDiagnostics())
        using (var arrival = new ArrivalPreviewDiagnostics())
        {
            actual = RangeApproachCore.StopAtRange(body, tuning, target, 0.1f, false, dt, scene.Context,
                Vector2.UnitX, predicted, ArrivalRangeCrossingTests.Admit);
            output.WriteLine($"reference={reference}, full={arrival.FullSteps}, certified={arrival.CertifiedTrials}, " +
                $"owners={evidence.OwnersVisited}, triangles={evidence.TrianglesExpanded}");
            Assert.True(reference > 0);
            Assert.Equal(0, arrival.FullSteps);
            Assert.Equal(reference, arrival.CertifiedTrials);
            Assert.Equal(0, evidence.TrianglesExpanded);
            Assert.Equal(1, evidence.OwnersVisited);
        }
        ArrivalRangeEquivalenceTests.Same(expected, actual);
        ArrivalRangeEquivalenceTests.Same(scene.Context.Step(body, expected, false, dt, tuning),
            scene.Context.Step(body, actual, false, dt, tuning));
    }
}
