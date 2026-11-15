using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class SupportProposalAdmissionFloatingPointTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("zero")]
    [InlineData("flush-output")]
    [InlineData("flush-input")]
    public void ChangedNativeControlsMissAndRecomputeTheOriginalBoolean(string mode)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null)
        {
            output.WriteLine("Native FP environment is unavailable. Admission uses the original predicate.");
            SupportProposalAdmissionTests.Check(Pose.Identity);
            return;
        }
        var reader = SupportProposalAdmission.ControlReaderForTests;
        try
        {
            SupportProposalAdmission.ControlReaderForTests = null;
            SupportProposalAdmission.ResetForTests();
            Assert.True(environment.Set("nearest"));
            using var capture = new SupportProposalAdmissionCapture();
            SupportProposalAdmissionTests.Check(Pose.Identity);
            SupportProposalAdmissionTests.Check(Pose.At(new(4, -8, 16)));
            if (capture.Bypasses != 0)
            {
                output.WriteLine("Controls reader or runtime is unsupported. Original predicate was evaluated.");
                Assert.Equal(2, capture.Bypasses);
                return;
            }
            Assert.Equal(1, capture.Misses);
            Assert.Equal(1, capture.Hits);
            if (!environment.Set(mode))
            {
                output.WriteLine($"No independent {mode} control on this architecture.");
                return;
            }
            SupportProposalAdmissionTests.Check(Pose.At(new(-16, 8, -4)));
            SupportProposalAdmissionTests.Check(Pose.Identity);
            Assert.Equal(2, capture.Misses);
            Assert.Equal(2, capture.Hits);
            Assert.True(environment.Set("nearest"));
            SupportProposalAdmissionTests.Check(Pose.Identity);
            Assert.Equal(3, capture.Misses);
        }
        finally
        {
            SupportProposalAdmission.ControlReaderForTests = reader;
            SupportProposalAdmission.ResetForTests();
        }
    }
}
