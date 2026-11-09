using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ArrivalCertificateBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SlopeBoundIncludesEqualityAndDeclinesTheNextLowerNormal(bool below)
    {
        // #1754 R1. Set the bound from the actual cosine, without relying on acos/cos cancellation.
        float cosine = MathF.Cos(FlatArrivalScene.DefaultTuning.MaxSlopeRadians);
        float horizontal = MathF.Sqrt(1 - cosine * cosine), grade = horizontal / cosine;
        using var scene = new FlatArrivalScene(slope: grade, normal: (_, _) => new(-horizontal, cosine, 0),
            minimumNormalY: below ? MathF.BitDecrement(cosine) : cosine);
        AssertPreview(scene, scene.Tuning, !below);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeightSpanDeclinesEqualityAndAcceptsTheNextNarrowerBound(bool equal)
    {
        // #1754 R1. Both bounds enclose this shallow field. Only the certificate's height span changes.
        float half = FlatArrivalScene.DefaultTuning.StepHeight / 2;
        using var scene = new FlatArrivalScene(slope: 0.125f,
            heightBounds: new(-half, equal ? half : MathF.BitDecrement(half)));
        AssertPreview(scene, scene.Tuning, !equal);
    }

    static void AssertPreview(FlatArrivalScene scene, MoveTuning tuning, bool certified)
    {
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        const float dt = 0.025f;
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, dt, tuning);
        Assert.True(predicted.SupportGranted);
        var target = ReachTarget.Point(predicted.Position);
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0, false, dt, scene.Context,
            Vector2.UnitX, predicted, ArrivalRangeCrossingTests.Admit, out int count);
        using var work = new ArrivalPreviewDiagnostics();
        Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, 0, false, dt, scene.Context,
            Vector2.UnitX, predicted, ArrivalRangeCrossingTests.Admit);
        ArrivalRangeEquivalenceTests.Same(expected, actual);
        Assert.True(count > 0);
        Assert.Equal(certified ? 0 : count, work.FullSteps);
        Assert.Equal(certified ? count : 0, work.CertifiedTrials);
        using var preview = scene.Context.OpenGroundRangePreview(body, predicted, tuning, false, dt);
        if (certified) Assert.NotNull(preview);
        else Assert.Null(preview);
    }

}
