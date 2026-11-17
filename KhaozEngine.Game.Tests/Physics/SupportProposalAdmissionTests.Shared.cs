using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Linked into KhaozEngine.FloatingPoint.Tests, where SupportProposalAdmissionFloatingPointTests checks admission under
// changed floating point controls, so the check stays single-sourced without its default tests.
public sealed partial class SupportProposalAdmissionTests
{
    internal static bool Check(Pose pose)
    {
        bool expected = InstalledPoseOperator.TryCreateSupport(new RigidPose(pose.Position, pose.Orientation), out _);
        bool actual = SupportProposalAdmission.Admits(pose);
        Assert.Equal(expected, actual);
        return actual;
    }
}
