using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed partial class CapturedStairExplicitSweepTests
{
    static void AssertShellClear(CapturedStairSweepFixture fixture, TickCapture capture)
    {
        Assert.True(capture.Sweeps.Count >= 3);
        var sweeps = capture.Sweeps;
        Assert.Equal(new Vector3(0, fixture.Tuning.StepHeight, 0), sweeps[0].Delta);
        Assert.Equal(0, sweeps[1].Delta.Y);
        Assert.Equal(0, sweeps[2].Delta.Y);
        Assert.True(sweeps[1].Delta.LengthSquared() > 0);
        Assert.True(sweeps[2].Delta.LengthSquared() > 0);
        foreach (var sweep in sweeps)
        {
            Assert.Equal(CapsuleSweepStatus.Clear, sweep.Result.Status);
            Assert.Null(sweep.Result.ImpactDistance);
            Assert.InRange(sweep.Result.CertifiedErrorMetres, 0, 0.001f);
            Assert.False(fixture.World.SweepCapsule(new CapsuleShape(0.3f, 0.5f), sweep.Pose,
                Vector3.Normalize(sweep.Delta), sweep.Delta.Length(), out _, QueryFilter.StaticsOnly));
        }
    }
}
