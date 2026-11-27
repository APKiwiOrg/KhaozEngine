using System;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;

namespace KhaozEngine.Tests.Render3D;

internal sealed partial class HangingPanelDrawRig
{
    internal sealed record DetailedFrame(Vector3[] Positions, ClothObservation Geometry, WornClothStatus Status,
        bool HasCloth, bool IsCurrent, ulong Revision);

    // Read the actual fallback draw or the actual accepted detailed surface in the same visible vertex order.
    internal DetailedFrame StepHandoff(HangingPanelPose pose, float elapsed, MotionLevel level, bool request)
    {
        SetPose(pose);
        motion!.Step(rig.Skeleton, palette, pose.Wearer, elapsed, level, request ? clothRequest : null);
        VerifySupportPose(pose);
        bool hasCloth = outfit.TryGetClothPresentation(garmentIndex, motion, 1, out var shown);
        Vector3[] positions;
        if (hasCloth)
        {
            ReadOnlySpan<ModelVertex> vertices = shown.DetailedVertices(0).Span;
            positions = new Vector3[clothCut!.Rest.Length];
            for (int i = 0; i < positions.Length; i++) positions[i] = vertices[i].Position;
        }
        else
        {
            garment.Read(Write(garmentIndex, pose.Wearer));
            positions = garment.Positions.AsSpan(0, clothCut!.Rest.Length).ToArray();
        }

        var panel = new PresentedPanel(positions, shownFaces!, clothCut.MaterialTriangles, clothCut.Probes);
        return new(positions, HangingPanelFixture.Observe(pose, panel), motion.ClothStatus, hasCloth,
            !hasCloth || shown.IsCurrentPose, hasCloth ? shown.DisplayRevision : 0);
    }

    internal Vector3[] AuthoredVisiblePositions(in Matrix4x4 model)
    {
        outfit.WriteDraws(garmentIndex, rig.Skeleton, palette, model, rig.Key, Template, draws);
        garment.Read(draws[0]);
        return garment.Positions.AsSpan(0, clothCut!.Rest.Length).ToArray();
    }
}
