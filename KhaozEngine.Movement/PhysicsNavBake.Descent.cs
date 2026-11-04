using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class PhysicsNavBake
{
    internal PhysicsNavDescentEvidence? DescentEvidence { get; private set; }
    internal static PhysicsNavBake CaptureDescent(GroundMoveContext context, PhysicsNavBakeOptions options, NavAreaClassifier classify)
    {
        var capture = Capture(context, options, classify);
        try { capture.DescentEvidence = PhysicsNavDescentCapture.Capture(capture); return capture; }
        catch { capture.Dispose(); throw; }
    }
}
