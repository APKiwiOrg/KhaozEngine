using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Attachments;

public sealed class ChainWidthRestTests(ITestOutputHelper output)
{
    [Fact]
    public void Soft_bounded_cape_at_a_frozen_capsule_stops_moving()
    {
        // #1884: contact can keep a soft cape pressed against its width band after the pose stops.
        const int stepsPerSecond = 128;
        const float stepSeconds = 1f / stepsPerSecond;
        var fixture = ChainSwingFixture.CreateRig(0.14f);
        var chains = fixture.Chains.Take(ChainSwingFixture.CapeChains)
            .Select(chain => chain with { Settings = new ChainSettings(5, 9, 1, 1, 1.1f, 0.015f) }).ToArray();
        var rig = new ChainRig(fixture.AnchorCount, chains, fixture.Links);
        var center = new Vector3(0.025f, -0.52f, -0.2f);
        ColliderCapsule[] colliders = [new(center, center, 0.15f)];
        var simulation = new ChainSimulation(rig, colliders.Length, new() { MaxStepSeconds = stepSeconds });
        var previous = new Vector3[chains.Length];

        At(0);
        for (int step = 0; step < 4 * stepsPerSecond; step++)
            At(stepSeconds);

        float contactError = float.PositiveInfinity;
        for (int chain = 0; chain < previous.Length; chain++)
        {
            previous[chain] = Tip(chain);
            int first = rig.FirstJoint(chain);
            float clearance = colliders[0].Radius + chains[chain].Settings.Radius;
            for (int joint = 1; joint < chains[chain].Joints.Count; joint++)
            {
                float distance = Vector3.Distance(center, simulation.JointsFromAnchor[first + joint].Translation);
                contactError = MathF.Min(contactError, MathF.Abs(distance - clearance));
            }
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
