using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using static KhaozEngine.Tests.Attachments.ChainSimulationTests;

namespace KhaozEngine.Tests.Attachments;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AttachmentAllocationCollection
{
    internal const string Name = "AllocSensitive";
}

[Collection(AttachmentAllocationCollection.Name)]
public sealed class ChainAllocationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Warm_step_and_compose_do_not_allocate(bool bounded, bool layered)
    {
        var upper = Chain(Vector3.UnitX, Settings, Joint(Vector3.Zero), Joint(Vector3.UnitX));
        ColliderCapsule distant = new(new(8, 0, 0), new(8, -1, 0), 0.3f);
        ColliderCapsule contact = new(new(2, -0.1f, 0), new(2, -1, 0), 0.3f);
        var lower = Chain(contact.End - contact.Start, Settings with { Gravity = 0, Radius = contact.Radius }, Joint(contact.Start))
            with { Layer = AttachmentLayer.Armour };
        var rig = bounded ? ChainSwingFixture.CreateRig(0.12f, wideLimits: true) :
            layered ? new ChainRig(1, [lower, upper with { Layer = AttachmentLayer.Outerwear }], []) :
            new ChainRig(1, [upper], []);
        ColliderCapsule[] capsules = layered ? [distant] : [distant, contact];
        var simulation = new ChainSimulation(rig, capsules.Length);
        Matrix4x4[] anchors = [Matrix4x4.Identity];
        var output = new Matrix4x4[simulation.Rig.JointCount];
        Advance(2);
        if (!bounded)
            Assert.InRange(DistanceToVerticalCapsule(output[^1].Translation,
                Vector3.Transform(Vector3.UnitX, output[^1]), contact), 0.299f, 0.301f);

        Replay();
        AllocationMeasurement.NoPerCallAllocation("warm swing step and composition", Replay);

        void Replay() => Advance(100);

        void Advance(int count)
        {
            simulation.Reset();
            for (int frame = 0; frame < count; frame++)
            {
                var wearer = Matrix4x4.Identity;
                if (bounded)
                    (anchors[0], wearer) = ChainSwingFixture.Pose(frame / 60f);
                simulation.Step(anchors, capsules, wearer, 1f / 60);
                simulation.ComposeJointWorlds(anchors, output);
            }
        }
    }
}
