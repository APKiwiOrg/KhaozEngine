using System;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;

namespace KhaozEngine.Tests.Render3D;

internal readonly record struct SwingCostPiece(LoadedAttachmentSet Set, int? PaletteBones);

internal static class LayeredSwingCostOutfit
{
    internal const int SimulatedWearers = 16;

    internal static SwingCostPiece[] Load(OutfitRig rig) => LoadWithCape(rig,
        SwingCostFixture.LoadPiece(rig, "cape", AttachmentLayer.Outerwear, "hip",
            5, 6, new Vector3(-.24f, 0, -.23f), linkStretch: .08f));

    internal static SwingCostPiece[] LoadSheetCase(OutfitRig rig, bool sevenGuides)
    {
        ReadOnlySpan<float> guides = sevenGuides ? [0f, .08f, .16f, .5f, .84f, .92f, 1f] : [0f, .16f, .5f, .84f, 1f];
        var cape = SwingCostFixture.LoadPiece(rig, "cape", AttachmentLayer.Outerwear, "hip",
            guides.Length, 5, new Vector3(-.24f, 0, -.23f), linkStretch: .08f, guideFractions: guides,
            tuning: new ChainSettings(34f, 32f, 1f, .105f, .075f, .0135f), segmentLength: .07f);
        return LoadWithCape(rig, cape, shortTails: true);
    }

    static SwingCostPiece[] LoadWithCape(OutfitRig rig, SwingCostPiece cape, bool shortTails = false)
    {
        // The knee's pitch sweeps the tails across the cape between panels during the walk.
        var tails = shortTails
            ? SwingCostFixture.LoadPiece(rig, "tails", AttachmentLayer.Armour, "knee",
                4, 3, new Vector3(-.14f, HipAboveKnee(rig) - .17f, -.23f),
                colliders: Hardware(rig, "hip", new Vector3(-.18f, -.52f, -.20f)),
                tuning: new ChainSettings(8f, 5f, 1f, .2f, 1.2f, .01f), segmentLength: .08f)
            : SwingCostFixture.LoadPiece(rig, "tails", AttachmentLayer.Armour, "knee",
                4, 3, new Vector3(-.18f, .28f, -.26f),
                colliders: Hardware(rig, "hip", new Vector3(-.18f, -.52f, -.20f)));
        AttachmentMeshRef mesh = rig.Source.Mesh("plates.glb", AttachmentTestSource.Glb());
        var plate = new RigidAttachmentPartDefinition(1, mesh, "knee", Matrix4x4.Identity) { Region = "alpha" };
        var plates = rig.Load("plates.json", new AttachmentSetDefinition([plate], AttachmentLayer.Garment, [],
            Hardware(rig, "knee", new Vector3(-.18f, -.12f, -.19f))));

        return [cape, tails, new SwingCostPiece(plates, null)];
    }

    static float HipAboveKnee(OutfitRig rig) =>
        rig.Palette[rig.Map.Bone("hip")].Translation.Y - rig.Palette[rig.Map.Bone("knee")].Translation.Y;

    static ColliderDefinition[] Hardware(OutfitRig rig, string joint, Vector3 firstCenter)
    {
        Vector3 center = SwingCostFixture.ModelBindPosition(rig, joint) + firstCenter;
        Vector3 halfAxis = Vector3.UnitY * .08f;
        var capsules = new ColliderDefinition[4];
        for (int index = 0; index < capsules.Length; index++)
        {
            Vector3 position = center + Vector3.UnitX * (.12f * index);
            capsules[index] = new ColliderDefinition(joint, position - halfAxis, position + halfAxis, .03f);
        }
        return capsules;
    }
}
