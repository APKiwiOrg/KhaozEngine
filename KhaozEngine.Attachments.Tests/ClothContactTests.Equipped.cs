using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Attachments;

public sealed partial class ClothContactTests(ITestOutputHelper output)
{
    [Fact]
    public void Equipped_breathing_checks_all_contact_work_within_the_near_profile()
    {
        var fixture = new ClothDrapeFixture();
        var simulation = new ClothSimulation(fixture.Rig, ClothWorkLimits.LayeredContact);
        simulation.Reset(fixture.Fitted);
        Span<ClothSupport> first = stackalloc ClothSupport[32];
        Span<ClothSupport> last = stackalloc ClothSupport[32];
        Span<Vector3> pins = stackalloc Vector3[ClothDrapeFixture.Columns];
        var hulls = new ClothHullSupport[2];
        HangingPanelPose opening = HangingPanelFixture.PoseAt(0);
        PoseInventory(opening, first);
        int broad = 0, narrow = 0, sweeps = 0, impulse = 0, peakTick = 0, self = 0;
        float minimumCapsuleClearance = float.PositiveInfinity;
        const int cycle = 4 * 120;
        for (int tick = 1; tick <= 3 * cycle; tick++)
        {
            HangingPanelPose pose = HangingPanelFixture.PoseAt(Math.Max(0, tick - cycle) / 120d);
            fixture.PosePins(pose, pins);
            PoseInventory(pose, last);
            for (int i = 0; i < hulls.Length; i++)
                hulls[i] = new ClothHullSupport(HullIdentities[i], EquippedHulls[i], HullPose(opening, i), HullPose(pose, i));

            ClothStepResult step = simulation.Advance(new ClothSubstep(pins, first, last, Seconds, ClothDrapeEvidence.Gravity, hulls));

            Assert.True(step.Status == ClothStepStatus.Accepted,
                $"Tick {tick}, phase {pose.PhaseSeconds:R}, {step.Status}, part {step.PartId}, " +
                $"iteration {step.Iterations}, broad {step.ContactChecks}, self {step.SelfContactChecks}, " +
                $"narrow {step.ContactCandidates}, sweeps {step.SweepRefinements}, velocity {step.NormalVelocityChecks}.");
            if (step.ContactChecks > broad)
            {
                broad = step.ContactChecks;
                peakTick = tick;
            }
            narrow = Math.Max(narrow, step.ContactCandidates);
            sweeps = Math.Max(sweeps, step.SweepRefinements);
            impulse = Math.Max(impulse, step.NormalVelocityChecks);
            self = Math.Max(self, step.SelfContactChecks);
            last.CopyTo(first);
            opening = pose;
            if (tick < 2 * cycle || tick % 30 != 0) continue;

            PresentedPanel panel = fixture.Present(simulation.Positions, pose, recoveries: 0);
            ClothObservation geometry = HangingPanelFixture.Observe(pose, panel);
            Assert.True(geometry.IsExterior);
            Assert.True(geometry.HasBearing);
            Assert.True(geometry.Material!.Value.BridgeClearance >= HangingPanelFixture.BridgeDaylight);
            Assert.InRange(geometry.Material.Value.SeamError, 0, HangingPanelFixture.SeamLimit);
            minimumCapsuleClearance = MathF.Min(minimumCapsuleClearance, MeasureExtraCapsules(panel, last));
        }

        output.WriteLine($"Broad {broad} at tick {peakTick}, self and shared refit {self}, narrow {narrow}, " +
            $"sweeps {sweeps}, normal work {impulse}, extra capsule clearance {minimumCapsuleClearance:R} m.");
    }

    private static float MeasureExtraCapsules(in PresentedPanel panel, ReadOnlySpan<ClothSupport> supports)
    {
        float minimum = float.PositiveInfinity;
        for (int i = 2; i < supports.Length; i++)
        {
            if (i is 13 or 14) continue;
            ColliderCapsule posed = supports[i].WorldCapsule;
            var visible = new PanelCapsule(posed.Start, posed.End, posed.Radius);
            foreach (PanelTriangle face in panel.Faces)
            {
                PanelTriangleContact contact = PanelGeometry.Contact(visible, panel.Vertices[face.A],
                    panel.Vertices[face.B], panel.Vertices[face.C]);
                minimum = MathF.Min(minimum, contact.Clearance);
            }
            Assert.True(minimum >= -HangingPanelFixture.ClearanceTolerance,
                $"Support {i} presented clearance {minimum:R} m.");
        }
        return minimum;
    }

    private static readonly ClothSupportIdentity[] HullIdentities =
    [
        new(3, 7, 1, AttachmentLayer.Armour),
        new(3, 8, 1, AttachmentLayer.Armour),
    ];

    private static readonly ClothSupportHull[] EquippedHulls =
    [
        Box(Vector3.Zero, new Vector3(.022f, .026f, .0035f)),
        Box(Vector3.Zero, new Vector3(.018f, .023f, .004f)),
    ];

    private static Matrix4x4 HullPose(in HangingPanelPose pose, int index)
    {
        float swing = (float)Math.Sin(Math.Tau * pose.PhaseSeconds / HangingPanelFixture.CycleSeconds);
        return index == 0
            ? Matrix4x4.CreateRotationY(.13f * swing) * Matrix4x4.CreateTranslation(-.215f, 1.18f, -.027f) * pose.Chest
            : Matrix4x4.CreateRotationY(-.17f * swing) * Matrix4x4.CreateTranslation(.217f, .925f, .028f);
    }

    private static void PoseInventory(in HangingPanelPose pose, Span<ClothSupport> result)
    {
        ClothDrapeFixture.PoseSupports(pose, result);
        Quaternion chestRotation = Quaternion.CreateFromRotationMatrix(pose.Chest);
        Vector3 chestOffset = pose.Chest.Translation;
        for (int side = 0; side < 2; side++)
        {
            float direction = side == 0 ? -1 : 1;
            int body = 2 + 5 * side;
            result[body] = Body(direction * .19f, 1.255f, -.058f, .042f, .038f, body);
            result[body + 1] = Body(direction * .109f, 1.243f, -.052f, .061f, .029f, body + 1);
            result[body + 2] = Body(direction * .232f, 1.17f, -.073f, .105f, .035f, body + 2);
            result[body + 3] = Body(direction * .163f, 1.08f, -.086f, .095f, .039f, body + 3);
            result[body + 4] = Body(direction * .145f, .95f, -.089f, .115f, .044f, body + 4);

            Matrix4x4 plate = HullPose(pose, side);
            result[13 + side] = ClothSupport.Capsule(new ColliderCapsule(-.013f * Vector3.UnitY,
                .013f * Vector3.UnitY, .021f), Quaternion.CreateFromRotationMatrix(plate), plate.Translation,
                HullIdentities[side]);
            result[15 + 2 * side] = Worn(direction * .20f, 1.15f, -.036f, .070f, .010f, 15 + 2 * side);
            result[16 + 2 * side] = Worn(direction * .17f, .96f, -.013f, .075f, .018f, 16 + 2 * side);
        }
        result[12] = Body(0, 1.26f, -.076f, .064f, .049f, 12);
        result[19] = Worn(0, 1.03f, -.022f, .05f, .016f, 19);

        float phase = (float)(Math.Tau * pose.PhaseSeconds / HangingPanelFixture.CycleSeconds);
        for (int tail = 0; tail < 4; tail++)
        {
            float x = (tail - 1.5f) * .128f;
            Vector3 from = new(x, .805f, .034f);
            for (int segment = 0; segment < 3; segment++)
            {
                float depth = segment + 1;
                Vector3 to = new(x + .002f * depth * MathF.Sin(phase + tail), .805f - .095f * depth,
                    .034f - .004f * depth + .003f * depth * MathF.Sin(phase - .7f * depth + tail));
                float radius = .011f - .001f * segment + .001f * MathF.Cos(phase + tail);
                result[20 + 3 * tail + segment] = ClothSupport.LowerSegment(new ColliderCapsule(from, to, radius),
                    new ClothSupportIdentity(4, (uint)tail + 1, segment + 1, AttachmentLayer.Garment));
                from = to;
            }
        }

        ClothSupport Body(float x, float y, float z, float length, float radius, int part) =>
            PosedCapsule(x, y, z, length, radius, new ClothSupportIdentity(1, (uint)part, 0, AttachmentLayer.Body));

        ClothSupport Worn(float x, float y, float z, float length, float radius, int part) =>
            PosedCapsule(x, y, z, length, radius, new ClothSupportIdentity(2, (uint)part, 0, AttachmentLayer.Garment));

        ClothSupport PosedCapsule(float x, float y, float z, float length, float radius, ClothSupportIdentity owner) =>
            ClothSupport.Capsule(new ColliderCapsule(new Vector3(x, y, z), new Vector3(x, y - length, z), radius),
                chestRotation, chestOffset, owner);
    }
}
