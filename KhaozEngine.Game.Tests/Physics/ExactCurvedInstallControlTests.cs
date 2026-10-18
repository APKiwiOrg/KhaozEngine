using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class ExactCurvedInstallControlTests
{
    // A witness, error, raw owner, role order, proposal verdict or zero-sign change on an exact install
    // must differ from the frozen public answers of unchanged production at 9e63657a7.
    [Fact]
    public void ExactCurvedInstallControlsMatchBaseline()
    {
        var lines = new List<string>();
        int axisRoles = 0, footprintRoles = 0, contacts = 0, misses = 0;
        Quaternion[] rotations = [Quaternion.Identity, new(0, 1, 0, 0), new(0.5f, 0.5f, 0.5f, 0.5f)];
        foreach (Quaternion rotation in rotations)
            foreach (float zero in new[] { 0f, BitConverter.Int32BitsToSingle(int.MinValue) })
                foreach ((string name, PhysicsShape shape, Vector3 localCentre) in Shapes())
                {
                    Vector3 position = -Vector3.Transform(localCentre, rotation);
                    position = new(position.X == 0 ? zero : position.X, position.Y,
                        position.Z == 0 ? zero : position.Z);
                    using var world = new BepuPhysicsWorld(Vector3.Zero);
                    StaticHandle owner = world.AddStatic(shape, new(position, rotation));
                    using var lease = world.AcquireQueryReadLease();
                    float top = name == "sphere" || rotation == rotations[2] ? 0.25f :
                        name == "cylinder" ? 0.5f : 0.75f;
                    foreach (Vector2 axis in new[] { new Vector2(zero, zero), new Vector2(0.125f, 0.125f),
                                 new Vector2(0.375f, zero) })
                    {
                        SupportFootprintRequest request = SupportFootprintTests.Request(x: axis.X, z: axis.Y,
                            lower: -1, upper: 1);
                        var members = new SupportFootprintMember[256];
                        var joins = new ulong[1024];
                        SupportFootprintResult footprint = ((IPhysicsSupportFootprint)world)
                            .QuerySupportFootprint(lease, request, members, joins);
                        Assert.Equal(CapsuleFeatureStatus.Complete, footprint.Status);
                        Assert.True(footprint.Members > 0, name);
                        foreach (SupportFootprintMember member in members.Take(footprint.Members))
                        {
                            Assert.Equal(owner, member.Static);
                            if (member.WitnessCount == 2)
                            {
                                Assert.Equal(SupportWitnessRole.Axis, member.First.Role);
                                Assert.Equal(SupportWitnessRole.Footprint, member.Second.Role);
                            }
                            foreach (SupportFootprintWitness witness in new[] { member.First, member.Second }
                                         .Take(member.WitnessCount))
                                if (witness.Role == SupportWitnessRole.Axis) axisRoles++;
                                else footprintRoles++;
                        }
                        foreach (SupportProposalVerdict verdict in new[] { footprint.AxisVerdict, footprint.FootVerdict })
                            if (verdict.Status == SupportProposalStatus.Contact) contacts++;
                            else if (verdict.Status == SupportProposalStatus.Miss) misses++;
                        string footprintBits = FootprintBits(footprint, members, joins, world, lease);
                        using (SupportReferenceEvaluation.Begin())
                        {
                            SupportFootprintResult reference = ((IPhysicsSupportFootprint)world)
                                .QuerySupportFootprint(lease, request, members, joins);
                            Assert.Equal(footprintBits, FootprintBits(reference, members, joins, world, lease));
                        }

                        var elements = new SupportElement[256];
                        SupportNeighborhoodResult neighborhood = ((IPhysicsSupportNeighborhood)world)
                            .QuerySupportNeighborhood(lease, new CapsuleShape(0.2f, 0.01f),
                                Pose.At(new(axis.X, top + 0.205f, axis.Y)), 0.0001f, elements, joins);
                        Assert.Equal(CapsuleFeatureStatus.Complete, neighborhood.Status);
                        if (axis == Vector2.Zero) Assert.True(neighborhood.Elements > 0, name);
                        lines.Add(($"{name} {BitConverter.SingleToInt32Bits(zero):X8} " +
                            InstalledSupportPoseTests.Raw(new Pose(position, rotation), world, lease) + " " +
                            InstalledSupportPoseTests.Raw(request, world, lease) + " " + footprintBits + " " +
                            InstalledSupportPoseTests.Raw(neighborhood, world, lease) + " " +
                            string.Join(" ", elements.Take(neighborhood.Elements)
                                .Select(e => InstalledSupportPoseTests.Raw(e, world, lease))) + " " +
                            string.Join(" ", joins.Take(neighborhood.Elements * neighborhood.JoinWordsPerRow)
                                .Select(j => j.ToString("X16")))).TrimEnd());
                    }
                }
        Assert.Equal(72, lines.Count);
        Assert.True(axisRoles > 0 && footprintRoles > 0 && contacts > 0 && misses > 0);
        string? capture = Environment.GetEnvironmentVariable("KHAOZENGINE_SUPPORT_CURVED_CONTROL_CAPTURE");
        if (!string.IsNullOrEmpty(capture))
            File.WriteAllLines(KhaozEngine.Tests.Locomotion.Contacts.SupportEquivalenceQueryAnchors.CapturePath(capture), lines);
        else
            Assert.Equal(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Locomotion", "Contacts", "Fixtures",
                "support-exact-curved-install-controls.txt")), lines.ToArray());
    }

    static string FootprintBits(SupportFootprintResult result, SupportFootprintMember[] members, ulong[] joins,
        IPhysicsWorld world, IPhysicsQueryLease lease) => InstalledSupportPoseTests.Raw(result, world, lease) + " " +
        string.Join(" ", members.Take(result.Members).Select(m => InstalledSupportPoseTests.Raw(m, world, lease))) + " " +
        string.Join(" ", joins.Take(result.Members * result.JoinWordsPerRow).Select(j => j.ToString("X16")));

    static IEnumerable<(string Name, PhysicsShape Shape, Vector3 LocalCentre)> Shapes()
    {
        yield return ("sphere", new SphereShape(0.25f), Vector3.Zero);
        yield return ("capsule", new CapsuleShape(0.25f, 1), Vector3.Zero);
        yield return ("cylinder", new CylinderShape(0.25f, 1), new(0, 0.5f, 0));
        // The curved child is second, with a nonzero local translation. Its raw element id must survive composition.
        Vector3 childCentre = new(0.125f, 0.25f, -0.125f);
        yield return ("compound-capsule", new CompoundShape([
            new(new BoxShape(Vector3.One), Pose.At(new(4, 0, 0))),
            new(new CapsuleShape(0.25f, 1), Pose.At(childCentre))]), childCentre);
    }
}
