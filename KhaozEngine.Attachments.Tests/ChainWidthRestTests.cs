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
    [InlineData(5f, 9f, 1.1f, 4, false)]
    [InlineData(5f, 9f, 1.1f, 4, true)]
    [InlineData(7.5f, 5.5f, MathF.PI, 8, false)]
    [InlineData(7.5f, 5.5f, MathF.PI, 8, true)]
    [InlineData(7.5f, 3.5f, MathF.PI, 8, false)]
    [InlineData(7.5f, 3.5f, MathF.PI, 8, true)]
    public void Soft_bounded_cape_at_a_frozen_capsule_stops_moving(
        float stiffness, float damping, float angleLimit, int settleSeconds, bool betweenChains)
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
        // Offset the low sphere outside the sheet so it still presses the width bound at a tip.
        var center = betweenChains ? new Vector3(0.025f, -0.52f, -0.2f) : new Vector3(-0.435f, -1.12f, -0.2f);
        ColliderCapsule[] colliders = [new(center, center, 0.15f)];
        var simulation = new ChainSimulation(rig, colliders.Length, new() { MaxStepSeconds = stepSeconds });
        var previous = new Vector3[chains.Length];

        At(0);
        for (int step = 0; step < settleSeconds * stepsPerSecond; step++)
            At(stepSeconds);

        for (int chain = 0; chain < previous.Length; chain++)
            previous[chain] = Tip(chain);

        float contactError = float.PositiveInfinity;
        foreach (var link in rig.Links)
        {
            int a = rig.FirstJoint(link.First);
            int b = rig.FirstJoint(link.Second);
            int count = chains[link.First].Joints.Count;
            float clearance = colliders[0].Radius + chains[link.First].Settings.Radius;
            // The low sphere lies at the tip row. The middle sphere can touch any band.
            for (int row = betweenChains ? 0 : count - 1; row < count; row++)
            {
                var a0 = simulation.JointsFromAnchor[a + row].Translation;
                var b0 = simulation.JointsFromAnchor[b + row].Translation;
                var a1 = row + 1 == count ? previous[link.First] : simulation.JointsFromAnchor[a + row + 1].Translation;
                var b1 = row + 1 == count ? previous[link.Second] : simulation.JointsFromAnchor[b + row + 1].Translation;
                contactError = MathF.Min(contactError,
                    MathF.Abs(ChainSheetContactTests.AxisDistance(colliders[0], a0, a1, b1) - clearance));
                contactError = MathF.Min(contactError,
                    MathF.Abs(ChainSheetContactTests.AxisDistance(colliders[0], a0, b1, b0) - clearance));
            }
        }

        if (!betweenChains)
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

        void At(float elapsed) => simulation.Step([Matrix4x4.Identity], colliders, Matrix4x4.Identity, elapsed);

        Vector3 Tip(int chain) =>
            Vector3.Transform(chains[chain].Tip, simulation.JointsFromAnchor[rig.FirstJoint(chain) + chains[chain].Joints.Count - 1]);
    }
}
