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
        float minimumTailClearance = float.PositiveInfinity;
        Span<float> hullClearance = [float.PositiveInfinity, float.PositiveInfinity];
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
            (float capsules, float tails) = MeasureExtraCapsules(panel, last);
            minimumCapsuleClearance = MathF.Min(minimumCapsuleClearance, capsules);
            minimumTailClearance = MathF.Min(minimumTailClearance, tails);
            for (int i = 0; i < hulls.Length; i++)
                hullClearance[i] = MathF.Min(hullClearance[i], MeasureVisibleHull(panel, hulls[i].Closing, i));
        }

        output.WriteLine($"Broad {broad} at tick {peakTick}, self and shared refit {self}, narrow {narrow}, " +
            $"sweeps {sweeps}, normal work {impulse}, extra capsule clearance {minimumCapsuleClearance:R} m. " +
            $"Tail clearance {minimumTailClearance:R} m and hull clearances {hullClearance[0]:R}, {hullClearance[1]:R} m.");
        Assert.InRange(minimumTailClearance, -HangingPanelFixture.ClearanceTolerance, .00055f);
        for (int i = 0; i < hullClearance.Length; i++)
            Assert.InRange(hullClearance[i], 0, .00055f);
    }

    private static (float Minimum, float Tails) MeasureExtraCapsules(in PresentedPanel panel, ReadOnlySpan<ClothSupport> supports)
    {
        float minimum = float.PositiveInfinity, tails = float.PositiveInfinity;
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
                if (i >= 20) tails = MathF.Min(tails, contact.Clearance);
            }
            Assert.True(minimum >= -HangingPanelFixture.ClearanceTolerance,
                $"Support {i} presented clearance {minimum:R} m.");
        }
        return (minimum, tails);
    }

    private static float MeasureVisibleHull(in PresentedPanel panel, Matrix4x4 pose, int hull)
    {
        Assert.True(Matrix4x4.Invert(pose, out Matrix4x4 inverse));
        Vector3 size = VisibleHullExtents[hull];
        Vector3 interior = size - new Vector3(HangingPanelFixture.ClearanceTolerance);
        float minimum = .002f;
        foreach (PanelTriangle face in panel.Faces)
        {
            Vector3 a = Vector3.Transform(panel.Vertices[face.A], inverse);
            Vector3 b = Vector3.Transform(panel.Vertices[face.B], inverse);
            Vector3 c = Vector3.Transform(panel.Vertices[face.C], inverse);
            Vector3 low = Vector3.Min(a, Vector3.Min(b, c)), high = Vector3.Max(a, Vector3.Max(b, c));
            if (!BoxOverlaps(low, high, size + new Vector3(minimum))) continue;
            Assert.False(ClipsBox(a, b, c, interior), $"Panel enters visible hull {hull}.");
            foreach (Vector3 vertex in VisibleHullSamples[hull])
                minimum = MathF.Min(minimum, Vector3.Distance(vertex, PanelGeometry.ClosestPoint(vertex, a, b, c)));
        }
        return minimum;
    }

    private static bool BoxOverlaps(Vector3 low, Vector3 high, Vector3 size) =>
        low.X <= size.X && high.X >= -size.X && low.Y <= size.Y && high.Y >= -size.Y &&
        low.Z <= size.Z && high.Z >= -size.Z;

    // Clip the presented triangle against all six visible planes. This also
    // finds an intersection entirely between the sampled mesh vertices.
    private static bool ClipsBox(Vector3 a, Vector3 b, Vector3 c, Vector3 size)
    {
        Span<Vector3> input = stackalloc Vector3[10];
        Span<Vector3> output = stackalloc Vector3[10];
        input[0] = a;
        input[1] = b;
        input[2] = c;
        int count = 3;
        for (int axis = 0; axis < 3; axis++)
        for (int side = -1; side <= 1; side += 2)
        {
            int next = 0;
            Vector3 first = input[count - 1];
            float firstGap = side * first[axis] - size[axis];
            for (int i = 0; i < count; i++)
            {
                Vector3 last = input[i];
                float lastGap = side * last[axis] - size[axis];
                if (firstGap * lastGap < 0)
                    output[next++] = Vector3.Lerp(first, last, firstGap / (firstGap - lastGap));
                if (lastGap <= 0) output[next++] = last;
                (first, firstGap) = (last, lastGap);
            }
            if (next == 0) return false;
            output[..next].CopyTo(input);
            count = next;
        }
        return true;
    }

    private static readonly Vector3[] VisibleHullExtents =
    [
        new(.02198f, .02598f, .00348f),
        new(.01798f, .02298f, .00398f),
    ];

    private static readonly Vector3[][] VisibleHullSamples = [Samples(VisibleHullExtents[0]), Samples(VisibleHullExtents[1])];

    private static Vector3[] Samples(Vector3 extent)
    {
        const int divisions = 6;
        var samples = new Vector3[6 * (divisions + 1) * (divisions + 1)];
        int index = 0;
        for (int axis = 0; axis < 3; axis++)
        for (int side = -1; side <= 1; side += 2)
        for (int row = 0; row <= divisions; row++)
        for (int column = 0; column <= divisions; column++)
        {
            Vector3 point = default;
            point[axis] = side * extent[axis];
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            point[u] = float.Lerp(-extent[u], extent[u], (float)row / divisions);
            point[v] = float.Lerp(-extent[v], extent[v], (float)column / divisions);
            samples[index++] = point;
        }
        return samples;
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
        float phase = (float)(Math.Tau * pose.PhaseSeconds / HangingPanelFixture.CycleSeconds);
        Vector3 pin = Vector3.Transform(HangingPanelFixture.Seam, pose.Chest);
        var drape = new PanelDrape(pin, pose.Support.Translation, HangingPanelFixture.LipRadius + HangingPanelFixture.HalfThickness);
        (Vector3 tangent, Vector3 normal) = drape.AtLength(index == 0 ? .13f : .34f);
        float yaw = (index == 0 ? .035f : -.045f) * MathF.Sin(phase);
        float arm = index == 0 ? -.215f : .217f;
        float depth = index == 0 ? .0035f : .004f;
        float advance = -.006f + .010f * (1 - MathF.Cos(phase)) * .5f;
        Vector3 center = tangent + arm * Vector3.UnitX + (advance - depth) * normal;
        float pitch = -MathF.Atan2(normal.Y, normal.Z);
        return Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateTranslation(center);
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
            float tailBack = tail is 0 or 3 ? pose.MarkerInModel.Z - .024f +
                .020f * (1 - MathF.Cos(phase)) * .5f : .034f;
            Vector3 from = new(x, .805f, tailBack);
            for (int segment = 0; segment < 3; segment++)
            {
                float depth = segment + 1;
                Vector3 to = new(x + .002f * depth * MathF.Sin(phase + tail), .805f - .095f * depth,
                    tailBack - .0015f * depth + .001f * depth * MathF.Sin(phase - .7f * depth + tail));
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
