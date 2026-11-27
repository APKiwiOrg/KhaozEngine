using System;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;

namespace KhaozEngine.Tests.Render3D;

internal sealed partial class HangingPanelDrawRig
{
    internal BoundClothSurface PreparationCostSurface => outfit.ClothSurface(garmentIndex, 1)!;
    internal int PreparationCostHostFaces => host.Indices32.Length / 3;
    internal int PreparationCostLipFaces => hardware!.SourceMesh.Indices32.Length / 3;

    internal void PoseForPreparationCost(in HangingPanelPose pose) => SetPose(pose);

    internal PreparationDraws PrepareForCost(scoped in Matrix4x4 model, float seconds, bool cloth,
        out WornClothPresentation shown, out ReadOnlyMemory<ModelVertex> detailedVertices)
    {
        WornMotion wearer = motion!;
        wearer.Step(rig.Skeleton, palette, model, seconds, MotionLevel.Simulated, cloth ? clothRequest : null);
        int lipCount = outfit.WriteDraws(0, wearer, rig.Skeleton, palette, model, rig.Key, Template, draws);
        AttachmentDraw lip = draws[0];
        int garmentCount = outfit.WriteDraws(garmentIndex, wearer, rig.Skeleton, palette, model, rig.Key, Template, draws);
        bool hasCloth = outfit.TryGetClothPresentation(garmentIndex, wearer, 1, out shown);
        detailedVertices = hasCloth ? shown.DetailedVertices(0) : default;
        return new(lipCount, lip, garmentCount, draws[0], hasCloth);
    }

    internal readonly record struct PreparationDraws(int LipCount, AttachmentDraw Lip, int GarmentCount,
        AttachmentDraw Garment, bool HasCloth);
}
