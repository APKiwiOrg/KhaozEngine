using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

public class ChainRigTests
{
    [Theory]
    [InlineData("anchor count")]
    [InlineData("missing anchor")]
    [InlineData("empty chain")]
    [InlineData("long chain")]
    [InlineData("nonfinite rest")]
    [InlineData("singular rest")]
    [InlineData("zero rotation")]
    [InlineData("zero tip")]
    [InlineData("nonfinite tip")]
    [InlineData("stiffness")]
    [InlineData("damping")]
    [InlineData("gravity")]
    [InlineData("trail")]
    [InlineData("angleLimit")]
    [InlineData("radius")]
    [InlineData("missing chain")]
    [InlineData("self link")]
    [InlineData("duplicate link")]
    [InlineData("reversed link")]
    public void Rig_refuses_one_invalid_argument_at_a_time(string invalid)
    {
        var joint = Joint(Vector3.Zero);
        var chain = Chain(Vector3.UnitY, Settings, joint);
        ChainRig Build(bool change)
        {
            var chosen = change ? invalid : "";
            var edited = chosen switch
            {
                "missing anchor" => chain with { Anchor = 1 },
                "empty chain" => chain with { Joints = [] },
                "long chain" => chain with { Joints = Enumerable.Repeat(joint, 17).ToArray() },
                "nonfinite rest" => chain with { Joints = [joint with { Translation = new(float.NaN, 0, 0) }] },
                "singular rest" => chain with { Joints = [joint with { Scale = new(1, 0, 1) }] },
                "zero rotation" => chain with { Joints = [joint with { Rotation = default }] },
                "zero tip" => chain with { Tip = Vector3.Zero },
                "nonfinite tip" => chain with { Tip = new(0, float.PositiveInfinity, 0) },
                "stiffness" => chain with { Settings = Settings with { Stiffness = -1 } },
                "damping" => chain with { Settings = Settings with { Damping = float.NaN } },
                "gravity" => chain with { Settings = Settings with { Gravity = 11 } },
                "trail" => chain with { Settings = Settings with { Trail = 1.01f } },
                "angleLimit" => chain with { Settings = Settings with { AngleLimit = 0 } },
                "radius" => chain with { Settings = Settings with { Radius = -1 } },
                _ => chain,
            };
            ChainLink[] links = chosen switch
            {
                "missing chain" => [new(0, 1), new(1, 3)],
                "self link" => [new(0, 1), new(1, 1)],
                "duplicate link" => [new(0, 1), new(0, 1)],
                "reversed link" => [new(0, 1), new(1, 0)],
                _ => [new(0, 1), new(1, 2)],
            };
            return new(chosen == "anchor count" ? -1 : 1, [edited, chain, chain], links);
        }

        Assert.Equal(3, Build(false).JointCount);
        Assert.ThrowsAny<ArgumentException>(() => Build(true));
    }

    [Fact]
    public void Rig_owns_chain_joint_and_link_lists_and_flattens_joint_indices()
    {
        ChainJointRest[] joints = [Joint(Vector3.Zero), Joint(Vector3.UnitX)];
        ChainRigChain[] chains = [new(0, joints, Vector3.UnitX, Settings), Chain(Vector3.UnitY, Settings, Joint(Vector3.Zero))];
        ChainLink[] links = [new(0, 1)];
        var rig = new ChainRig(1, chains, links);
        joints[1] = Joint(Vector3.UnitZ);
        chains[0] = chains[1];
        links[0] = new(1, 0);

        Assert.Equal(3, rig.JointCount);
        Assert.Equal(2, rig.FirstJoint(1));
        Assert.Equal(Vector3.UnitX, rig.Chains[0].Joints[1].Translation);
        Assert.Equal(new ChainLink(0, 1), Assert.Single(rig.Links));
        Assert.Equal(0, new ChainRig(0, [], []).JointCount);
    }

    [Theory]
    [InlineData("anchor count")]
    [InlineData("collider count")]
    [InlineData("negative time")]
    [InlineData("nonfinite time")]
    [InlineData("anchor matrix")]
    [InlineData("singular anchor")]
    [InlineData("wearer matrix")]
    [InlineData("capsule start")]
    [InlineData("capsule end")]
    [InlineData("capsule radius")]
    [InlineData("negative radius")]
    [InlineData("motion level")]
    public void Invalid_step_leaves_output_and_future_motion_untouched(string invalid)
    {
        var chain = Chain(Vector3.UnitX, Settings, Joint(Vector3.Zero));
        var edited = Create(chain, 1);
        var control = Create(chain, 1);
        var normal = Matrix4x4.Identity;
        var nonfinite = normal with { M11 = float.NaN };
        ColliderCapsule empty = new(new(5, 5, 5), new(5, 5, 5), 0);
        foreach (var simulation in new[] { edited, control })
        {
            simulation.Step([normal], [empty], normal, 0);
            simulation.Step([normal], [empty], normal, 1f / 60);
        }

        Matrix4x4[] anchors = invalid switch
        {
            "anchor count" => [],
            "anchor matrix" => [nonfinite],
            "singular anchor" => [Matrix4x4.CreateScale(0)],
            _ => [normal],
        };
        ColliderCapsule[] capsules = invalid switch
        {
            "collider count" => [],
            "capsule start" => [empty with { Start = new(float.NaN, 5, 5) }],
            "capsule end" => [empty with { End = new(5, float.NaN, 5) }],
            "capsule radius" => [empty with { Radius = float.PositiveInfinity }],
            "negative radius" => [empty with { Radius = -1 }],
            _ => [empty],
        };
        var wearer = invalid == "wearer matrix" ? nonfinite : normal;
        var seconds = invalid switch { "negative time" => -1, "nonfinite time" => float.NaN, _ => 1f / 60 };
        var level = invalid == "motion level" ? (MotionLevel)42 : MotionLevel.Simulated;

        Assert.ThrowsAny<ArgumentException>(() => edited.Step(anchors, capsules, wearer, seconds, level));
        Assert.True(control.JointsFromAnchor.SequenceEqual(edited.JointsFromAnchor));
        control.Step([normal], [empty], normal, 1f / 60);
        edited.Step([normal], [empty], normal, 1f / 60);
        Assert.True(control.JointsFromAnchor.SequenceEqual(edited.JointsFromAnchor));
    }

    [Theory]
    [InlineData("step seconds")]
    [InlineData("step count")]
    [InlineData("teleport")]
    [InlineData("gap")]
    [InlineData("blend")]
    [InlineData("gravity")]
    public void Simulation_refuses_invalid_options(string invalid)
    {
        var rig = new ChainRig(0, [], []);
        var options = new ChainSimulationOptions();
        var edited = invalid switch
        {
            "step seconds" => options with { MaxStepSeconds = 0 },
            "step count" => options with { MaxStepsPerFrame = 0 },
            "teleport" => options with { TeleportDistance = -1 },
            "gap" => options with { LongestGapSeconds = float.NaN },
            "blend" => options with { HoldBlendSeconds = -1 },
            _ => options with { Gravity = new(float.PositiveInfinity, 0, 0) },
        };

        Assert.Empty(new ChainSimulation(rig, 0, options).JointsFromAnchor.ToArray());
        Assert.ThrowsAny<ArgumentException>(() => new ChainSimulation(rig, 0, edited));
    }
}
