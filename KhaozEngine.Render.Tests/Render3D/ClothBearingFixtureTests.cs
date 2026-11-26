using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Attachments;
using KhaozEngine.Tests.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class ClothBearingFixtureTests(ITestOutputHelper output)
{
    [Fact]
    public void Clearance_alone_accepts_a_floating_panel()
    {
        HangingPanelPose pose = HangingPanelFixture.PoseAt(0);
        using var draw = new HangingPanelDrawRig(Triangle(pose.MarkerInModel, .015f, .015f), PanelFace.Lining, welded: false);

        ClothObservation sample = HangingPanelFixture.Observe(pose, draw.Present(pose));

        Assert.InRange(sample.MarkerGap, .01499f, .01501f);
        Assert.True(sample.IsExterior);
        Assert.False(sample.HasBearing);
    }

    [Fact]
    public void Withdrawal_rejects_a_welded_corner()
    {
        HangingPanelPose closed = HangingPanelFixture.PoseAt(0);
        HangingPanelPose withdrawn = closed.WithWithdrawal();
        Vector3 marker = closed.MarkerInModel;
        using var welded = new HangingPanelDrawRig(Triangle(marker, .015f, .001f), PanelFace.Lining, welded: true);
        using var released = new HangingPanelDrawRig(Triangle(new Vector3(0, marker.Y, 0), .015f, 0), PanelFace.Lining, welded: false);

        Assert.True(HangingPanelFixture.Observe(closed, welded.Present(closed)).HasBearing);
        ClothObservation stuck = HangingPanelFixture.Observe(withdrawn, welded.Present(withdrawn));
        ClothObservation free = HangingPanelFixture.Observe(withdrawn, released.Present(withdrawn));

        Assert.InRange(stuck.MarkerGap, .00099f, .00101f);
        Assert.False(stuck.HasReleasedMarker);
        Assert.InRange(free.MarkerGap, .06899f, .06901f);
        Assert.True(free.HasReleasedMarker);
    }

    [Theory]
    [InlineData("Lining")]
    [InlineData("Outer")]
    [InlineData("Rim")]
    public void The_observer_detects_an_edge_crossing_with_exterior_vertices(string surface)
    {
        HangingPanelPose pose = HangingPanelFixture.PoseAt(0);
        Vector3 marker = pose.MarkerInModel;
        PanelFace face = Enum.Parse<PanelFace>(surface);
        using var draw = new HangingPanelDrawRig(
            [marker + new Vector3(-.03f, -.06f, -.02f), marker + new Vector3(-.03f, .06f, -.02f), marker + new Vector3(.05f, .06f, -.02f)],
            face, welded: false);

        ClothObservation sample = HangingPanelFixture.Observe(pose, draw.Present(pose));

        Assert.Equal(1, sample.AnalyticSupportCrossings);
        Assert.InRange(sample.MinimumSignedClearance, -.02001f, -.01999f);
    }

    [Fact]
    public void The_observer_detects_a_support_piercing_only_the_triangle_interior()
    {
        HangingPanelPose pose = HangingPanelFixture.PoseAt(0);
        var axis = new PanelCapsule(new(0, 0, -.1f), new(0, 0, .1f), .05f);
        using var draw = new HangingPanelDrawRig([new(-.2f, -.2f, 0), new(.2f, -.2f, 0), new(0, .2f, 0)],
            PanelFace.Lining, welded: false);
        ReadOnlySpan<Vector3> submitted = draw.Present(pose).Vertices;

        PanelTriangleContact contact = PanelGeometry.Contact(axis, submitted[0], submitted[1], submitted[2]);

        Assert.True(contact.Crosses);
        Assert.InRange(contact.Clearance, -.05001f, -.04999f);
    }

    [Theory]
    [InlineData(.047f)]
    [InlineData(.0001f)]
    public void The_observer_marks_a_triangle_wholly_inside_the_solid(float depth)
    {
        HangingPanelPose pose = HangingPanelFixture.PoseAt(0);
        using var draw = new HangingPanelDrawRig(Triangle(pose.MarkerInModel, .001f, -depth), PanelFace.Outer, welded: false);

        ClothObservation sample = HangingPanelFixture.Observe(pose, draw.Present(pose));

        Assert.Equal(1, sample.ContainedTriangles);
        Assert.Equal(0, sample.AnalyticSupportCrossings);
        Assert.InRange(sample.MinimumSignedClearance, -depth - .00001f, -depth + .00001f);
        Assert.False(sample.IsExterior);
    }

    [Fact]
    public void The_observer_measures_material_strain_between_paired_skins()
    {
        HangingPanelPose pose = HangingPanelFixture.PoseAt(0);
        Vector3[] layers = [new(0, 0, -.1f), new(1.2f, 0, -.1f), new(0, 1, -.1f),
            new(0, 0, .1f), new(.8f, 0, .1f), new(0, 1, .1f)];
        using var draw = new HangingPanelDrawRig(layers,
            [new PanelTriangle(0, 1, 2, PanelFace.Lining), new PanelTriangle(3, 5, 4, PanelFace.Outer)], welded: false);
        PresentedPanel submitted = draw.Present(pose);
        PanelMaterialTriangle triangle = ReferenceTriangle(1, 1);
        var observed = new PresentedPanel(submitted.Vertices, submitted.Faces, new[] { triangle });

        Assert.InRange(Vector3.Distance(submitted.Vertices[0], submitted.Vertices[1]), 1.19999f, 1.20001f);
        Assert.InRange(HangingPanelFixture.MeasureStrain(observed, pose.Wearer), 0, .00001f);
    }

    [Fact]
    public void The_observer_measures_principal_stretch_from_in_plane_shear()
    {
        const float width = .120f, height = .01328125f, shear = .06f;
        HangingPanelPose pose = HangingPanelFixture.PoseAt(0);
        Vector3[] layers = [
            new(0, 0, 0), new(width, 0, 0), new(shear * height, height, 0),
            new(0, 0, .002f), new(width, 0, .002f), new(shear * height, height, .002f),
        ];
        using var draw = new HangingPanelDrawRig(layers, PairedTriangle, welded: false);
        PresentedPanel submitted = draw.Present(pose);
        PanelMaterialTriangle triangle = ReferenceTriangle(width, height);
        var observed = new PresentedPanel(submitted.Vertices, submitted.Faces, new[] { triangle });

        float strain = HangingPanelFixture.MeasureStrain(observed, pose.Wearer);
        Assert.InRange(strain, .03044f, .03046f);
        output.WriteLine($"Sheared material triangle principal strain {strain:R}.");
    }

    [Fact]
    public void The_observer_detects_a_bend_near_the_end_of_the_tangent_span()
    {
        HangingPanelPose pose = HangingPanelFixture.PoseAt(0);
        var cut = new HangingPanelCut();
        var drape = new PanelDrape(HangingPanelFixture.Seam, pose.Support.Translation, HangingPanelCut.FittedRadius);
        Vector3[] fitted = cut.Fit(drape);
        int lining = HangingPanelCut.Index(33, HangingPanelCut.CenterColumn);
        fitted[lining] += .020f * Vector3.UnitZ;
        fitted[HangingPanelCut.Outer(lining)] += .020f * Vector3.UnitZ;
        using var draw = new HangingPanelDrawRig(fitted, cut.Faces, welded: false);

        ClothObservation sample = HangingPanelFixture.Observe(pose, draw.Present(pose, cut));

        float error = sample.Material!.Value.TangentError!.Value;
        Assert.InRange(error, .018f, .022f);
        Assert.True(error > HangingPanelFixture.TangentLimit);
        output.WriteLine($"Planted centerline bend tangent error {error:R} m.");
    }

    private static readonly PanelTriangle[] PairedTriangle =
        [new(0, 1, 2, PanelFace.Lining), new(3, 5, 4, PanelFace.Outer)];

    private static PanelMaterialTriangle ReferenceTriangle(float width, float height)
    {
        Vector3[] rest = [Vector3.Zero, new(width, 0, 0), new(0, height, 0),
            Vector3.Zero, new(width, 0, 0), new(0, height, 0)];
        return PanelMaterialTriangle.FromRest(new(0, 3), new(1, 4), new(2, 5), rest);
    }

    [Fact]
    public void The_outfit_draw_preserves_the_shoulder_seam_over_breath_and_release()
    {
        const int framesPerSecond = 60;
        const int cycle = (int)HangingPanelFixture.CycleSeconds * framesPerSecond;
        const int firstMeasured = 2 * cycle;
        const int lastMeasured = 4 * cycle;
        const int lastReleased = 5 * cycle;
        float deltaSeconds = 1f / framesPerSecond;
        var cut = new HangingPanelCut();
        CalibrateSupportedAndReleasedCut(cut);
        string? tracePath = Environment.GetEnvironmentVariable("KE_CLOTH_FIXTURE_TRACE");
        using StreamWriter? trace = string.IsNullOrWhiteSpace(tracePath) ? null : File.CreateText(tracePath);

        string? mesh = null, definition = null;
        foreach (bool flat in new[] { false, true })
        foreach (MotionLevel level in new[] { MotionLevel.Held, MotionLevel.Simulated })
        {
            HangingPanelPose closing = At(0, flat);
            using var draw = new HangingPanelDrawRig(cut, closing);
            mesh ??= draw.GarmentMeshDigest;
            definition ??= draw.GarmentDefinitionDigest;
            Assert.Equal(mesh, draw.GarmentMeshDigest);
            Assert.Equal(definition, draw.GarmentDefinitionDigest);
            for (int frame = 0; frame <= lastReleased; frame++)
            {
                bool withdrawn = frame > lastMeasured;
                double driveTime = withdrawn ? 0 : Math.Max(0, frame - cycle) / (double)framesPerSecond;
                HangingPanelPose pose = At(driveTime, flat);
                if (withdrawn) pose = pose.WithWithdrawal();
                PresentedPanel panel = draw.Present(pose, cut, frame == 0 ? 0 : deltaSeconds, level);
                if (frame < firstMeasured) continue;

                ClothObservation sample = HangingPanelFixture.Observe(pose, panel);
                if (trace is not null)
                    trace.WriteLine(JsonSerializer.Serialize(new
                    {
                        Level = level.ToString(),
                        GarmentMesh = mesh,
                        GarmentDefinition = definition,
                        Outfit = flat ? "flat" : "raised",
                        Stage = withdrawn ? "withdrawal" : "breath",
                        Frame = frame,
                        Seconds = frame / (double)framesPerSecond,
                        pose.PhaseSeconds,
                        sample.MarkerGap,
                        sample.IsLipLoaded,
                        BearingWithinLimit = sample.IsLipLoaded ? (bool?)sample.HasBearing : null,
                        sample.IsExterior,
                        sample.MinimumSignedClearance,
                        sample.AnalyticSupportCrossings,
                        sample.ContainedTriangles,
                        sample.Lining,
                        sample.Outer,
                        sample.Rim,
                        sample.Material,
                        sample.RecoveryCount,
                    }));
                Assert.True(float.IsFinite(sample.MarkerGap) && float.IsFinite(sample.MinimumSignedClearance));
                Assert.Equal(sample.IsLipLoaded, sample.Material!.Value.TangentError.HasValue);
                if (!flat && !withdrawn)
                    Assert.True(sample.IsLipLoaded);
                Assert.InRange(sample.Material!.Value.SeamError, 0, HangingPanelFixture.SeamLimit);
            }
        }

        static HangingPanelPose At(double seconds, bool flat)
        {
            HangingPanelPose pose = HangingPanelFixture.PoseAt(seconds);
            return flat ? pose.WithFlatLip() : pose;
        }
    }

    private void CalibrateSupportedAndReleasedCut(HangingPanelCut cut)
    {
        HangingPanelPose raised = HangingPanelFixture.PoseAt(0);
        var reference = new PanelDrape(HangingPanelFixture.Seam, raised.Support.Translation, HangingPanelCut.FittedRadius);
        using var fitted = new HangingPanelDrawRig(cut.Fit(reference), cut.Faces, welded: false);
        ClothObservation supported = HangingPanelFixture.Observe(raised, fitted.Present(raised, cut));
        MaterialObservation fittedMaterial = supported.Material!.Value;
        Assert.True(supported.IsExterior);
        Assert.True(supported.IsLipLoaded);
        Assert.True(supported.HasBearing);
        Assert.InRange(fittedMaterial.HemAngleDegrees, 0, HangingPanelFixture.HemAngleLimit);
        Assert.InRange(fittedMaterial.LipPlumbError, 0, HangingPanelFixture.PlumbLimit);
        HangingPanelPose flatLoaded = raised.WithFlatLip();
        HangingPanelPose flatUnloaded = HangingPanelFixture.PoseAt(1).WithFlatLip();
        Assert.True(HangingPanelFixture.Observe(flatLoaded, fitted.Present(flatLoaded, cut)).IsLipLoaded);
        Assert.False(HangingPanelFixture.Observe(flatUnloaded, fitted.Present(flatUnloaded, cut)).IsLipLoaded);
        output.WriteLine($"Analytic supported cut: marker gap {supported.MarkerGap:R} m, "
            + $"signed clearance {supported.MinimumSignedClearance:R} m, analytic support crossings {supported.AnalyticSupportCrossings}, "
            + $"hem {fittedMaterial.HemAngleDegrees:R} degrees, lip plumb error {fittedMaterial.LipPlumbError:R} m.");

        HangingPanelPose withdrawn = raised.WithWithdrawal();
        using var free = new HangingPanelDrawRig(cut.Rest, cut.Faces, welded: false);
        ClothObservation released = HangingPanelFixture.Observe(withdrawn, free.Present(withdrawn, cut));
        MaterialObservation releasedMaterial = released.Material!.Value;
        Assert.True(released.IsExterior);
        Assert.False(released.IsLipLoaded);
        Assert.True(released.HasReleasedMarker);
        Assert.InRange(releasedMaterial.HemAngleDegrees, 0, HangingPanelFixture.HemAngleLimit);
        Assert.InRange(releasedMaterial.BarePlumbError, 0, HangingPanelFixture.PlumbLimit);
        output.WriteLine($"Analytic released cut: marker gap {released.MarkerGap:R} m, "
            + $"signed clearance {released.MinimumSignedClearance:R} m, analytic support crossings {released.AnalyticSupportCrossings}, "
            + $"hem {releasedMaterial.HemAngleDegrees:R} degrees, bare plumb error {releasedMaterial.BarePlumbError:R} m.");
    }

    private static Vector3[] Triangle(Vector3 origin, float halfHeight, float offset) =>
        [origin + new Vector3(-.03f, -halfHeight, offset), origin + new Vector3(.03f, -halfHeight, offset),
            origin + new Vector3(0, halfHeight, offset)];
}
