using System;
using System.Numerics;

namespace KhaozEngine.Tests.Attachments;

internal static class HangingPanelFixture
{
    internal const double CycleSeconds = 4;
    internal const float Width = .48f;
    internal const float Length = .85f;
    internal const float HalfThickness = .00075f;
    internal const float LipRadius = .05f;
    internal const float LipHalfSpan = .31f;
    internal const float RecessRadius = .08f;
    internal const float BearingLimit = .003f;
    internal const float ClearanceTolerance = .00025f;
    internal const float SeamLimit = .0005f;
    internal const float HemAngleLimit = 5f;
    internal const float StrainLimit = .02f;
    internal const float BridgeDaylight = .020f;
    internal const float TangentLimit = .008f;
    internal const float PlumbLimit = .005f;
    internal const float ReleaseGap = .040f;
    internal static readonly Vector3 ChestPivot = new(0, 1.18f, 0);
    internal static readonly Vector3 Seam = new(0, 1.30f, 0);
    internal static readonly Vector3 RecessCenter = new(0, 1.065f, -.15f);
    internal static readonly Vector3 MarkerLocal = new(0, 0, LipRadius);

    internal static HangingPanelPose PoseAt(double seconds)
    {
        double phase = seconds % CycleSeconds;
        double angle = Math.Tau * phase / CycleSeconds;
        float sin = (float)Math.Sin(angle), cos = (float)Math.Cos(angle);
        Matrix4x4 chest = Matrix4x4.CreateTranslation(-ChestPivot) *
            Matrix4x4.CreateRotationX(MathF.PI / 36 * sin) *
            Matrix4x4.CreateTranslation(ChestPivot + new Vector3(0, 0, .008f * sin));
        var support = Matrix4x4.CreateTranslation(0, .83f + .005f * cos, .025f + .006f * cos);
        return new HangingPanelPose(phase, chest, support, Matrix4x4.Identity);
    }

    internal static ClothObservation Observe(in HangingPanelPose pose, in PresentedPanel panel)
    {
        Matrix4x4 model = pose.Wearer;
        PanelCapsule lip = pose.LipInWorld;
        PanelCapsule recess = pose.RecessInWorld;
        Vector3 marker = Vector3.Transform(pose.MarkerInModel, model);
        float markerGap = float.PositiveInfinity;
        Span<PanelClearance> clearance = [PanelClearance.Empty, PanelClearance.Empty, PanelClearance.Empty];
        foreach (PanelTriangle face in panel.Faces)
        {
            Vector3 a = Vector3.Transform(panel.Vertices[face.A], model);
            Vector3 b = Vector3.Transform(panel.Vertices[face.B], model);
            Vector3 c = Vector3.Transform(panel.Vertices[face.C], model);
            if (face.Face == PanelFace.Lining)
                markerGap = MathF.Min(markerGap, Vector3.Distance(marker, PanelGeometry.ClosestPoint(marker, a, b, c)));
            ref PanelClearance surface = ref clearance[(int)face.Face];
            surface = surface.Add(PanelGeometry.Contact(lip, a, b, c), PanelGeometry.Contact(recess, a, b, c));
        }

        MaterialObservation? material = panel.Probes is { } probes ? MeasureMaterial(pose, panel, probes) : null;
        return new ClothObservation(markerGap, clearance[0], clearance[1], clearance[2], material, panel.RecoveryCount);
    }

    private static MaterialObservation MeasureMaterial(in HangingPanelPose pose, in PresentedPanel panel,
        PanelProbes probes)
    {
        Matrix4x4 model = pose.Wearer;
        Matrix4x4 chest = pose.Chest * model;
        Vector3 pin = Vector3.Transform(Seam, chest);
        float seamLeft = PinError(probes.LeftSeam, -Width / 2, panel.Vertices);
        float clasp = PinError(probes.Clasp, 0, panel.Vertices);
        float seamRight = PinError(probes.RightSeam, Width / 2, panel.Vertices);
        Vector3 bridge = probes.Bridge.Read(panel.Vertices, model);
        Vector3 hem = probes.Hem.Read(panel.Vertices, model);
        Vector3 quarter = probes.HemQuarter.Read(panel.Vertices, model);
        Vector3 marker = Vector3.Transform(pose.MarkerInModel, model);
        Vector3 patch = Vector3.Transform(panel.Vertices[probes.MarkedPatch], model);
        Vector3 tail = hem - quarter;
        float hemAngle = MathF.Atan2(HorizontalLength(tail), -tail.Y) * (180 / MathF.PI);
        PanelCapsule recess = pose.RecessInWorld;
        Vector3 referenceCenter = Vector3.Transform(pose.Support.Translation, model);
        bool hasTangent = PanelDrape.TryTangent(pin, referenceCenter, LipRadius + HalfThickness, out Vector3 tangent);
        float? tangentError = null;
        if (hasTangent)
        {
            tangentError = 0;
            foreach (PanelMaterialPoint point in probes.UpperSpan)
            {
                Vector3 displayed = point.Read(panel.Vertices, model);
                tangentError = MathF.Max(tangentError.Value,
                    Vector3.Distance(displayed, PanelGeometry.ClosestPointOnSegment(displayed, pin, tangent)));
            }
        }

        return new MaterialObservation(seamLeft, clasp, seamRight, hemAngle, MeasureStrain(panel, model),
            Vector3.Distance(bridge, PanelGeometry.ClosestPointOnSegment(bridge, recess.Start, recess.End)) - recess.Radius,
            tangentError, HorizontalLength(hem - pin), HorizontalLength(hem - marker), patch.Y - marker.Y,
            patch.Y, patch.Z, marker.Y, marker.Z, hem.Y, hem.Z);

        float PinError(PanelMaterialPoint point, float x, ReadOnlySpan<Vector3> vertices) =>
            Vector3.Distance(point.Read(vertices, model), Vector3.Transform(Seam + x * Vector3.UnitX, chest));
    }

    internal static float MeasureStrain(in PresentedPanel panel, Matrix4x4 model)
    {
        float strain = 0;
        foreach (PanelMaterialEdge edge in panel.MaterialEdges)
        {
            Vector3 first = edge.First.Read(panel.Vertices, model);
            Vector3 second = edge.Second.Read(panel.Vertices, model);
            strain = MathF.Max(strain, Vector3.Distance(first, second) / edge.RestLength - 1);
        }
        return strain;
    }

    private static float HorizontalLength(Vector3 point) => new Vector2(point.X, point.Z).Length();
}

internal readonly record struct HangingPanelPose(double PhaseSeconds, Matrix4x4 Chest, Matrix4x4 Support, Matrix4x4 Wearer)
{
    internal Vector3 MarkerInModel => Vector3.Transform(HangingPanelFixture.MarkerLocal, Support);
    internal HangingPanelPose WithWithdrawal() => this with { Support = Support * Matrix4x4.CreateTranslation(0, 0, -.15f) };
    internal HangingPanelPose WithFlatLip() => this with { Support = Support * Matrix4x4.CreateTranslation(0, 0, -.061f) };

    internal PanelCapsule LipInWorld => Transverse(Vector3.Zero, HangingPanelFixture.LipRadius, Support * Wearer);
    internal PanelCapsule RecessInWorld =>
        Transverse(HangingPanelFixture.RecessCenter, HangingPanelFixture.RecessRadius, Chest * Wearer);

    private static PanelCapsule Transverse(Vector3 center, float radius, Matrix4x4 transform)
    {
        Vector3 halfAxis = Vector3.UnitX * HangingPanelFixture.LipHalfSpan;
        return new PanelCapsule(Vector3.Transform(center - halfAxis, transform),
            Vector3.Transform(center + halfAxis, transform), radius);
    }
}

internal enum PanelFace { Lining, Outer, Rim }
internal readonly record struct PanelTriangle(int A, int B, int C, PanelFace Face);

internal readonly ref struct PresentedPanel(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<PanelTriangle> faces,
    ReadOnlySpan<PanelMaterialEdge> materialEdges = default, PanelProbes? probes = null, int? recoveryCount = null)
{
    internal ReadOnlySpan<Vector3> Vertices { get; } = vertices;
    internal ReadOnlySpan<PanelTriangle> Faces { get; } = faces;
    internal ReadOnlySpan<PanelMaterialEdge> MaterialEdges { get; } = materialEdges;
    internal PanelProbes? Probes { get; } = probes;
    internal int? RecoveryCount { get; } = recoveryCount;
}

internal readonly record struct PanelMaterialPoint(int Lining, int Outer)
{
    internal Vector3 Read(ReadOnlySpan<Vector3> vertices, Matrix4x4 model) =>
        Vector3.Transform((vertices[Lining] + vertices[Outer]) * .5f, model);
}

internal readonly record struct PanelMaterialEdge(PanelMaterialPoint First, PanelMaterialPoint Second, float RestLength);

internal sealed record PanelProbes(PanelMaterialPoint LeftSeam, PanelMaterialPoint Clasp, PanelMaterialPoint RightSeam,
    PanelMaterialPoint Bridge, PanelMaterialPoint HemQuarter, PanelMaterialPoint Hem, int MarkedPatch,
    PanelMaterialPoint[] UpperSpan);

internal readonly record struct PanelClearance(float Lip, float Recess, int Crossings, int Contained)
{
    internal static readonly PanelClearance Empty = new(float.PositiveInfinity, float.PositiveInfinity, 0, 0);
    internal float Minimum => MathF.Min(Lip, Recess);

    internal PanelClearance Add(PanelTriangleContact lip, PanelTriangleContact recess) =>
        new(MathF.Min(Lip, lip.Clearance), MathF.Min(Recess, recess.Clearance),
            Crossings + (lip.Crosses ? 1 : 0) + (recess.Crosses ? 1 : 0),
            Contained + (lip.Contained ? 1 : 0) + (recess.Contained ? 1 : 0));
}

internal readonly record struct ClothObservation(float MarkerGap, PanelClearance Lining, PanelClearance Outer, PanelClearance Rim,
    MaterialObservation? Material, int? RecoveryCount)
{
    public float MinimumSignedClearance => MathF.Min(Lining.Minimum, MathF.Min(Outer.Minimum, Rim.Minimum));
    public int SurfaceCrossings => Lining.Crossings + Outer.Crossings + Rim.Crossings;
    public int ContainedTriangles => Lining.Contained + Outer.Contained + Rim.Contained;
    public bool IsExterior => MinimumSignedClearance >= -HangingPanelFixture.ClearanceTolerance && SurfaceCrossings == 0;
    public bool HasBearing => MarkerGap <= HangingPanelFixture.BearingLimit;
    public bool HasReleasedMarker => MarkerGap >= HangingPanelFixture.ReleaseGap;
}

internal readonly record struct MaterialObservation(float LeftSeamError, float ClaspError, float RightSeamError,
    float HemAngleDegrees, float TensileStrain, float BridgeClearance, float? TangentError, float BarePlumbError,
    float LipPlumbError, float RelativeTangentialTravel, float PatchY, float PatchZ, float MarkerY, float MarkerZ, float HemY,
    float HemZ)
{
    public float SeamError => MathF.Max(ClaspError, MathF.Max(LeftSeamError, RightSeamError));
}
