using System.Numerics;
using KhaozEngine.Attachments;

namespace KhaozEngine.Tests.Render3D;

internal static class SevenGuideSwingCostFixture
{
    // Keep the same distance between the first and last guides as the five-guide layered fixture.
    internal static SwingCostPiece LoadCape(OutfitRig rig, Vector3 firstRoot) =>
        SwingCostFixture.LoadPiece(rig, "cape", AttachmentLayer.Outerwear, "hip", 7, 5, firstRoot,
            linkStretch: .08f, chainSpacing: .08f, settingsForChain: static _ => new(34f, 32f, 1f, .105f, .075f, .0135f));

    internal static ChainSettings TailSettings(int chain) => chain < 2
        ? new(24f, 9f, 1f, .23f, .45f, .005f)
        : new(24f, 9f, 1f, .23f, .215f, .0085f);
}
