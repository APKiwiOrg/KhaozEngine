using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;

namespace KhaozEngine.Tests.Render3D;

internal readonly record struct SwingCostPiece(LoadedAttachmentSet Set, int? PaletteBones);

internal static class LayeredSwingCostOutfit
{
    internal const int SimulatedWearers = 16;

    internal static SwingCostPiece[] Load(OutfitRig rig, bool sevenGuideCape = false)
    {
        var capeRoot = new Vector3(-.24f, 0, -.23f);
        SwingCostPiece cape = sevenGuideCape
            ? SevenGuideSwingCostFixture.LoadCape(rig, capeRoot)
            : SwingCostFixture.LoadPiece(rig, "cape", AttachmentLayer.Outerwear, "hip", 5, 6, capeRoot, linkStretch: .08f);
        // The knee's pitch sweeps the tails across the cape between panels during the walk.
        SwingCostPiece tails = SwingCostFixture.LoadPiece(rig, "tails", AttachmentLayer.Armour, "knee",
            4, 3, new Vector3(-.18f, .28f, -.26f),
            colliders: Hardware(rig, "hip", new Vector3(-.18f, -.52f, -.20f)),
            settingsForChain: sevenGuideCape ? SevenGuideSwingCostFixture.TailSettings : null);
        AttachmentMeshRef mesh = rig.Source.Mesh("plates.glb", AttachmentTestSource.Glb());
        var plate = new RigidAttachmentPartDefinition(1, mesh, "knee", Matrix4x4.Identity) { Region = "alpha" };
        var plates = rig.Load("plates.json", new AttachmentSetDefinition([plate], AttachmentLayer.Garment, [],
            Hardware(rig, "knee", new Vector3(-.18f, -.12f, -.19f))));

        return [cape, tails, new SwingCostPiece(plates, null)];
    }

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
