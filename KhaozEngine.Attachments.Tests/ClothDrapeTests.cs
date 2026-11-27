using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Attachments.ClothDrapeEvidence;

namespace KhaozEngine.Tests.Attachments;

public sealed class ClothDrapeTests(ITestOutputHelper output, ClothDrapeEvidence evidence) : IClassFixture<ClothDrapeEvidence>
{
    internal static readonly ClothMaterial Woven = ClothMaterial.HeavyWoven with { HalfThickness = HangingPanelFixture.HalfThickness };

    [Fact]
    public void Heavy_woven_matches_the_qualified_recipe_and_allows_authored_thickness()
    {
        var qualified = new ClothMaterial(.48f, 1800, 1200, 180, .0005f, .015f, .00075f, .38f, .25f, NormalAirDrag: .8162874f);
        Assert.Equal(qualified, ClothMaterial.HeavyWoven);
        Assert.Equal(qualified with { HalfThickness = .001f }, ClothMaterial.HeavyWoven with { HalfThickness = .001f });
    }

    [Fact]
    public void Loaded_lip_supports_the_free_panel_through_breath()
    {
        CheckGeometry(evidence.Samples, withdrawn: false);
        var expectedTicks = Enumerable.Range(FirstMeasured * 2, (LastMeasured - FirstMeasured) * 2 + 1);
        foreach (bool flat in new[] { false, true })
        {
            var measuredTicks = evidence.Samples.Where(sample => !sample.Withdrawn && sample.Flat == flat)
                .Select(sample => sample.Tick);
            Assert.Equal(expectedTicks, measuredTicks);
        }
    }

    [Fact]
    public void A_withdrawn_lip_releases_the_hem_to_its_bare_plumb_line() => CheckGeometry(evidence.Samples, withdrawn: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_near_profile_keeps_the_loaded_and_withdrawn_drape_with_self_contact_work(bool reuseMaterialFactor)
    {
        IReadOnlyList<DrapeSample> samples = reuseMaterialFactor ? evidence.LayeredReuseSamples : evidence.LayeredSamples;
        CheckGeometry(samples, withdrawn: false);
        CheckGeometry(samples, withdrawn: true);
        foreach (bool flat in new[] { false, true })
        {
            DrapeSample last = samples.Last(sample => sample.Flat == flat);
            Assert.InRange(last.PeakSelfContactChecks, 1, last.PeakContactChecks);
            Assert.InRange(last.PeakContactChecks, 1, ClothWorkLimits.LayeredContact.MaxContactChecks);
            Assert.InRange(last.PeakContactCandidates, 1, ClothWorkLimits.LayeredContact.MaxContactCandidates);
        }
    }

    private void CheckGeometry(IReadOnlyList<DrapeSample> samples, bool withdrawn)
    {
        var failures = new List<string>();
        foreach (DrapeSample sample in samples.Where(sample => sample.Withdrawn == withdrawn))
        {
            ClothObservation geometry = sample.Geometry;
            MaterialObservation material = geometry.Material!.Value;
            Require(geometry.IsExterior, "exterior", geometry.MinimumSignedClearance);
            Require(geometry.RecoveryCount == 0, "recoveries", geometry.RecoveryCount!.Value);
            Require(material.SeamError <= HangingPanelFixture.SeamLimit, "seam", material.SeamError);
            if (withdrawn)
            {
                if (sample.Frame == LastReleased)
                {
                    output.WriteLine($"{(sample.Flat ? "flat" : "raised")} final withdrawal gap {geometry.MarkerGap:R} m, " +
                        $"bare plumb error {material.BarePlumbError:R} m.");
                    Require(geometry.HasReleasedMarker, "withdrawal gap", geometry.MarkerGap);
                    Require(material.BarePlumbError <= HangingPanelFixture.PlumbLimit, "bare plumb", material.BarePlumbError);
                }
                continue;
            }

            Require(sample.Flat || geometry.IsLipLoaded, "loaded lip", geometry.MarkerGap);
            if (sample.Bearing is { } bearing)
            {
                Require(bearing.Acceptable, $"flat bearing {bearing.Verdict}", geometry.MarkerGap);
                if (sample.Tick == LastMeasured * 2)
                    Require(bearing.Verdict != FlatLipBearingVerdict.Closing, "unfinished flat bearing window", geometry.MarkerGap);
            }
            else if (geometry.IsLipLoaded)
            {
                Require(geometry.HasBearing, "bearing", geometry.MarkerGap);
            }
            Require(material.HemAngleDegrees <= HangingPanelFixture.HemAngleLimit, "tail angle", material.HemAngleDegrees);
            Require(material.TensileStrain <= HangingPanelFixture.StrainLimit, "strain", material.TensileStrain);
            Require(material.BridgeClearance >= HangingPanelFixture.BridgeDaylight, "bridge", material.BridgeClearance);
            if (!sample.Flat || sample.PhaseSeconds == 0)
                Require(material.TangentError is <= HangingPanelFixture.TangentLimit, "tangent", material.TangentError ?? float.NaN);
            if (sample.Flat && sample.Tick == LastMeasured * 2)
                Require(material.LipPlumbError <= HangingPanelFixture.PlumbLimit, "flat plumb", material.LipPlumbError);

            void Require(bool pass, string measurement, float value)
            {
                if (!pass)
                    failures.Add($"{(sample.Flat ? "flat" : "raised")} tick {sample.Tick} {measurement} {value:R} ({sample.Status})");
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} failed sample checks. {string.Join(", ", failures.Take(20))}");
    }

    [Fact]
    public void Marked_coupon_responds_to_friction()
    {
        Vector2[] pattern = [new(-.06f, -.06f), new(.06f, -.06f), new(-.06f, .06f), new(.06f, .06f)];
        Vector3[] rest = Array.ConvertAll(pattern, point => new Vector3(point.X, Woven.HalfThickness, point.Y));
        ClothTriangle[] faces = [new(0, 2, 3), new(0, 3, 1)];
        Vector3 free = Travel(Woven with { StaticFriction = 0, KineticFriction = 0 });
        Vector3 held = Travel(Woven with { StaticFriction = .5f, KineticFriction = .3f });
        Vector3 sled = .025f * Vector3.UnitX;
        output.WriteLine($"Sled travel 0.025 m. Frictionless patch X {free.X:R} m. Friction patch X {held.X:R} m.");
        Assert.InRange(free.Length(), 0, .002f);
        Assert.InRange(Vector3.Distance(held, sled), 0, .002f);

        Vector3 Travel(ClothMaterial material)
        {
            ClothRig rig = ClothRig.Create(rest, pattern, faces, [], [], material);
            var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 4, 32, 256));
            simulation.Reset(rest);
            Span<ClothSupport> first = stackalloc ClothSupport[1];
            Span<ClothSupport> last = stackalloc ClothSupport[1];
            first[0] = ClothSupport.Plane(Vector3.UnitY, Quaternion.Identity, Vector3.Zero);
            for (int tick = 1; tick <= 60; tick++)
            {
                float t = tick * StepSeconds;
                last[0] = ClothSupport.Plane(Vector3.UnitY, Quaternion.Identity, .1f * t * t * Vector3.UnitX);
                ClothStepResult step = simulation.Advance(new ClothSubstep([], first, last, StepSeconds, Gravity));
                Assert.Equal(ClothStepStatus.Accepted, step.Status);
                first[0] = last[0];
            }

            return (simulation.Positions[0] + simulation.Positions[3] - rest[0] - rest[3]) / 2;
        }
    }

    [Fact]
    public void Still_air_dissipates_normal_velocity_and_preserves_tangential_motion()
    {
        Vector3 testVelocity = 3 * Vector3.UnitX + 4 * Vector3.UnitZ;
        Vector3 forward = AdvanceFreeTriangle(new ClothTriangle(0, 1, 2), Woven, testVelocity);
        Vector3 reversed = AdvanceFreeTriangle(new ClothTriangle(0, 2, 1), Woven, testVelocity);
        Vector3 tangential = AdvanceFreeTriangle(new ClothTriangle(0, 1, 2), Woven, 3 * Vector3.UnitX);
        Vector3 noDrag = AdvanceFreeTriangle(new ClothTriangle(0, 1, 2), Woven with { NormalAirDrag = 0 }, testVelocity);
        float normalDamping = MathF.Exp(-StepSeconds * Woven.NormalAirDrag / Woven.Density);

        Assert.InRange(Vector3.Distance(forward, new Vector3(3, 0, 4 * normalDamping)), 0, .00003f);
        Assert.InRange(Vector3.Distance(forward, reversed), 0, .00003f);
        Assert.InRange(Vector3.Distance(tangential, 3 * Vector3.UnitX), 0, .00003f);
        Assert.InRange(Vector3.Distance(noDrag, testVelocity), 0, .00003f);
        Assert.True(forward.LengthSquared() < noDrag.LengthSquared());

        static Vector3 AdvanceFreeTriangle(ClothTriangle face, ClothMaterial material, Vector3 velocity)
        {
            Vector3[] rest = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
            Vector2[] pattern = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY];
            ClothRig rig = ClothRig.Create(rest, pattern, [face], [], [], material);
            var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 0, 0, 0, MaxNodeTravel: .05f));
            simulation.Reset(rest);

            // An impulse under the same mass density starts the triangle in rigid translation.
            Vector3 gravity = velocity / StepSeconds;
            gravity.Z *= MathF.Exp(StepSeconds * material.NormalAirDrag / material.Density);
            Assert.Equal(ClothStepStatus.Accepted,
                simulation.Advance(new ClothSubstep([], [], [], StepSeconds, gravity)).Status);
            Vector3 start = simulation.Positions[0];
            Assert.Equal(ClothStepStatus.Accepted,
                simulation.Advance(new ClothSubstep([], [], [], StepSeconds, Vector3.Zero)).Status);
            return (simulation.Positions[0] - start) / StepSeconds;
        }
    }

    [Fact]
    public void Opposed_faces_at_a_fold_both_resist_broadside_motion()
    {
        // Faces meet at a 180 degree fold. Their normals oppose one another,
        // but the drag from either face points against a broadside velocity.
        Vector3[] rest = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitY];
        Vector2[] pattern = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY];
        ClothRig rig = ClothRig.Create(rest, pattern, [new(0, 1, 2), new(1, 0, 3)], [], [], Woven);
        var drag = new ClothAirDrag(rig);
        drag.Begin(rest, StepSeconds);
        float normalDamping = MathF.Exp(-StepSeconds * Woven.NormalAirDrag / Woven.Density);
        Vector3 expected = Vector3.UnitZ * normalDamping;

        Vector3 hingeVelocity = drag.Damp(0, Vector3.UnitZ);

        Assert.InRange(Vector3.Distance(expected, hingeVelocity), 0, .000001f);
    }

    [Fact]
    public void A_vertical_strip_transmits_its_weight_to_the_pinned_band()
    {
        // A vertical uncontacted meter of wool stretches roughly rho*g*L²/(2*k), or 1.31 mm.
        // This isolates the membrane projection from both bearing contact and surface embedding.
        const int rows = 13, columns = 3;
        var rest = new Vector3[rows * columns];
        var pattern = new Vector2[rest.Length];
        var faces = new List<ClothTriangle>();
        for (int row = 0; row < rows; row++)
        for (int col = 0; col < columns; col++)
        {
            int node = row * columns + col;
            rest[node] = new Vector3(.06f * col, -row / 12f, 0);
            pattern[node] = new Vector2(rest[node].X, -rest[node].Y);
            if (row + 1 == rows || col + 1 == columns) continue;
            faces.Add(new ClothTriangle(node, node + 1, node + columns + 1));
            faces.Add(new ClothTriangle(node, node + columns + 1, node + columns));
        }

        ClothRig rig = ClothRig.Create(rest, pattern, faces.ToArray(), [0, 1, 2], [], Woven);
        var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 0, 0, 0));
        simulation.Reset(rest);
        ReadOnlySpan<Vector3> pins = rest.AsSpan(0, columns);
        for (int tick = 1; tick <= 480; tick++)
            Assert.Equal(ClothStepStatus.Accepted,
                simulation.Advance(new ClothSubstep(pins, [], [], StepSeconds, Gravity)).Status);

        float length = -simulation.Positions[^2].Y;
        output.WriteLine($"Vertical strip length with gravity {length:R} m.");
        Assert.InRange(length, 1.0005f, 1.004f);
    }

    [Fact]
    public void A_free_row_can_hinge_about_the_sewn_band()
    {
        Vector3[] rest = [Vector3.Zero, Vector3.UnitX, -Vector3.UnitY, Vector3.UnitX - Vector3.UnitY];
        Vector2[] pattern = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.One];
        ClothRig rig = ClothRig.Create(rest, pattern, [new(0, 1, 3), new(0, 3, 2)], [0, 1], [], Woven);
        var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 0, 0, 0));
        Quaternion opening = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 5);
        Vector3[] draped = Array.ConvertAll(rest, point => Vector3.Transform(point, opening));
        simulation.Reset(draped);

        ClothStepResult step = simulation.Advance(new ClothSubstep(rest.AsSpan(0, 2), [], [], StepSeconds, Vector3.Zero));

        Assert.Equal(ClothStepStatus.Accepted, step.Status);
        for (int node = 0; node < draped.Length; node++)
            Assert.InRange(Vector3.Distance(draped[node], simulation.Positions[node]), 0, .00001f);
    }

    [Fact]
    public void Material_factor_budget_applies_to_the_whole_step()
    {
        Vector3[] rest = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        Vector2[] pattern = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY];
        ClothRig rig = ClothRig.Create(rest, pattern, [new(0, 1, 2)], [], [], Woven);
        // A full 9 by 9 triangular factor uses 120 inner-product terms per projection.
        const long productsForFourIterations = 480;
        var limits = new ClothWorkLimits(4, 0, 0, 0, MaxMaterialFactorProducts: productsForFourIterations);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClothSimulation(rig, limits with { MaxMaterialFactorProducts = productsForFourIterations - 1 }));
        var simulation = new ClothSimulation(rig, limits);
        simulation.Reset(rest);

        ClothStepResult step = simulation.Advance(new ClothSubstep([], [], [], StepSeconds, Vector3.Zero));

        Assert.Equal(ClothStepStatus.Accepted, step.Status);
        Assert.Equal(productsForFourIterations, step.MaterialFactorProducts);
    }

    [Fact]
    public void A_contact_budget_refusal_preserves_the_last_accepted_positions()
    {
        Vector3[] rest = [new(0, .001f, 0), new(1, .001f, 0), new(0, .001f, 1)];
        Vector2[] pattern = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY];
        ClothRig rig = ClothRig.Create(rest, pattern, [new(0, 1, 2)], [], [], Woven);
        var simulation = new ClothSimulation(rig, new ClothWorkLimits(4, 1, 1, 16));
        simulation.Reset(rest);
        ClothSupport floor = ClothSupport.Plane(Vector3.UnitY, Quaternion.Identity, Vector3.Zero);
        ClothStepResult step = simulation.Advance(new ClothSubstep([], [floor], [floor], StepSeconds, Gravity));

        Assert.Equal(ClothStepStatus.ContactCapacity, step.Status);
        Assert.Equal(rest, simulation.Positions.ToArray());
    }

}
