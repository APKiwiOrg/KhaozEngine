using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class PhysicsNavBake
{
    internal PhysicsNavDescentEvidence? DescentEvidence { get; private set; }
    internal static PhysicsNavBake CaptureDescent(GroundMoveContext context, PhysicsNavBakeOptions options, NavAreaClassifier classify)
    {
        return Capture(context, options with { CaptureTerrainDescentEvidence = true }, classify);
    }
}
