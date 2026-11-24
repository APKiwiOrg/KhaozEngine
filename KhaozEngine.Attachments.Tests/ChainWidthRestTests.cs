using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainWidthRestTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(5f, 9f, 1.1f, 4)]
    [InlineData(7.5f, 5.5f, MathF.PI, 8)]
    [InlineData(7.5f, 3.5f, MathF.PI, 8)]
    public void Soft_bounded_cape_at_a_frozen_capsule_stops_moving(
        float stiffness, float damping, float angleLimit, int settleSeconds)
    {
        // #1884: contact can keep a soft cape pressed against its width band after the pose stops.
        // #1883: the lower damping rows also require rest with the bend limit disabled.
        const int stepsPerSecond = 128;
        const float stepSeconds = 1f / stepsPerSecond;
        var fixture = ChainSwingFixture.CreateRig(0.14f);
        var chains = fixture.Chains.Take(ChainSwingFixture.CapeChains)
            .Select(chain => chain with { Settings = new ChainSettings(stiffness, damping, 1, 1, angleLimit, 0.015f) }).ToArray();
        var rig = new ChainRig(fixture.AnchorCount, chains, fixture.Links);
        // Place the sphere beyond the hanging tips to press the width band through tip contact.
        var center = new Vector3(0.09f, -1.12f, -0.2f);
        ColliderCapsule[] colliders = [new(center, center, 0.15f)];
        var simulation = new ChainSimulation(rig, colliders.Length, new() { MaxStepSeconds = stepSeconds });
        var previous = new Vector3[chains.Length];

        At(0);
        for (int step = 0; step < settleSeconds * stepsPerSecond; step++)
            At(stepSeconds);

        float contactError = float.PositiveInfinity;
        for (int chain = 0; chain < previous.Length; chain++)
        {
            previous[chain] = Tip(chain);
            int first = rig.FirstJoint(chain);
            float clearance = colliders[0].Radius + chains[chain].Settings.Radius;
            for (int joint = 0; joint < chains[chain].Joints.Count; joint++)
            {
                var start = simulation.JointsFromAnchor[first + joint].Translation;
                var end = joint + 1 < chains[chain].Joints.Count
                    ? simulation.JointsFromAnchor[first + joint + 1].Translation : previous[chain];
                float distance = ChainSegmentContactTests.DistanceToSegment(center, start, end);
                contactError = MathF.Min(contactError, MathF.Abs(distance - clearance));
            }
        }

        Assert.Contains(rig.Links, link =>
            MathF.Abs(Vector3.Distance(previous[link.First], previous[link.Second]) / 0.18f - 1) >= 0.13f);
        float fixedPeak = MeasureTravel(uneven: false);
        float unevenPeak = MeasureTravel(uneven: true);
        output.WriteLine($"Peak settled tip travel: fixed {fixedPeak:F9} m, uneven {unevenPeak:F9} m");
        Assert.InRange(contactError, 0, 0.00002f);
        Assert.InRange(fixedPeak, 0, 0.000003f);
        Assert.InRange(unevenPeak, 0, 0.000003f);

        float MeasureTravel(bool uneven)
        {
            float peak = 0;
            for (int frame = 0; frame < 2 * stepsPerSecond; frame++)
            {
                At(uneven ? (frame % 2 == 0 ? 0.25f : 1.75f) * stepSeconds : stepSeconds);
                for (int chain = 0; chain < previous.Length; chain++)
                {
                    var tip = Tip(chain);
                    peak = MathF.Max(peak, Vector3.Distance(tip, previous[chain]));
                    previous[chain] = tip;
                }
            }
            return peak;
        }

        void At(float elapsed) => simulation.Step([Matrix4x4.Identity], colliders, Matrix4x4.Identity, elapsed);

        Vector3 Tip(int chain) =>
            Vector3.Transform(chains[chain].Tip, simulation.JointsFromAnchor[rig.FirstJoint(chain) + chains[chain].Joints.Count - 1]);
    }
}
