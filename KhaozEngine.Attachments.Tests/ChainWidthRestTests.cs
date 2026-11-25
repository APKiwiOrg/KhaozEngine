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
    [InlineData(5f, 9f, 1.1f, 4, false, false)]
    [InlineData(5f, 9f, 1.1f, 4, true, false)]
    [InlineData(7.5f, 5.5f, MathF.PI, 8, false, false)]
    [InlineData(7.5f, 5.5f, MathF.PI, 8, true, false)]
    [InlineData(7.5f, 3.5f, MathF.PI, 8, false, false)]
    [InlineData(7.5f, 3.5f, MathF.PI, 8, true, false)]
    [InlineData(5f, 9f, 1.1f, 4, false, true)]
    [InlineData(7.5f, 3.5f, MathF.PI, 8, true, true)]
    public void Soft_bounded_cape_at_a_frozen_capsule_stops_moving(
        float stiffness, float damping, float angleLimit, int settleSeconds, bool betweenChains, bool sheet)
    {
        // #1884: contact can keep a soft cape pressed against its width band after the pose stops.
        // #1883: the lower damping rows also require rest with the bend limit disabled.
        const int stepsPerSecond = 128;
        const float stepSeconds = 1f / stepsPerSecond;
        var fixture = ChainSwingFixture.CreateRig(0.14f);
        var chains = fixture.Chains.Take(ChainSwingFixture.CapeChains)
            .Select(chain => chain with { Settings = new ChainSettings(stiffness, damping, 1, 1, angleLimit, 0.015f) }).ToArray();
        var rig = new ChainRig(fixture.AnchorCount, chains, fixture.Links);
        // The middle sphere used to send adjacent panels around opposite sides.
        // The low sphere presses the width bound through tip contact.
        var center = betweenChains ? new Vector3(0.025f, -0.52f, -0.2f) : new Vector3(0.09f, -1.12f, -0.2f);
        ColliderCapsule[] colliders = [new(center, center, 0.15f)];
        var simulation = new ChainSimulation(rig, colliders.Length, new() { MaxStepSeconds = stepSeconds });
        var previous = new Vector3[chains.Length];

        At(0);
        for (int step = 0; step < settleSeconds * stepsPerSecond; step++)
            At(stepSeconds);

        for (int chain = 0; chain < previous.Length; chain++)
            previous[chain] = Tip(chain);

        float contactError = float.PositiveInfinity;
        Assert.Equal(rig.JointCount, simulation.JointsFromAnchor.Length);
        for (int chain = 0; chain < chains.Length; chain++)
        {
            int first = rig.FirstJoint(chain);
            int count = chains[chain].Joints.Count;
            float clearance = colliders[0].Radius + chains[chain].Settings.Radius;
            for (int joint = 0; joint < count; joint++)
            {
                var start = simulation.JointsFromAnchor[first + joint].Translation;
                var end = joint + 1 == count ? previous[chain] : simulation.JointsFromAnchor[first + joint + 1].Translation;
                float distance = ChainSegmentContactTests.DistanceToSegment(center, start, end);
                Assert.True(distance >= clearance - 0.00002f, $"Chain {chain}, segment {joint}: clearance {distance} m.");
                contactError = MathF.Min(contactError, MathF.Abs(distance - clearance));
            }
        }

        if (sheet)
        {
            foreach (var link in rig.Links)
            {
                float distance = ChainSheetContactTests.MeasureSheet(simulation, chains[link.First],
                    chains[link.Second], colliders[0], rig.FirstJoint(link.First), rig.FirstJoint(link.Second));
                float clearance = colliders[0].Radius + chains[link.First].Settings.Radius;
                Assert.True(distance >= clearance - 0.00002f, $"Band {link.First}-{link.Second}: clearance {distance} m.");
                contactError = MathF.Min(contactError, MathF.Abs(distance - clearance));
            }
        }
        else if (!betweenChains)
        {
            Assert.Contains(rig.Links, link =>
                MathF.Abs(Vector3.Distance(previous[link.First], previous[link.Second]) / 0.18f - 1) >= 0.13f);
        }

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

        void At(float elapsed)
        {
            // Keep the segment-only rows on the width constraint they originally covered.
            bool previous = ChainSheetContact.DisabledForTests;
            ChainSheetContact.DisabledForTests = !sheet;
            try
            {
                simulation.Step([Matrix4x4.Identity], colliders, Matrix4x4.Identity, elapsed);
            }
            finally
            {
                ChainSheetContact.DisabledForTests = previous;
            }
        }

        Vector3 Tip(int chain) =>
            Vector3.Transform(chains[chain].Tip, simulation.JointsFromAnchor[rig.FirstJoint(chain) + chains[chain].Joints.Count - 1]);
    }
}
