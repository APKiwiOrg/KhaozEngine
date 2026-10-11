using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactControllerTests
{
    [Fact]
    public void LastSubstepAirLandingWithACarriedRunEmitsNoGroundSignals()
    {
        // One vertical substep lands on support 0.3 above the start. Its paced seat owes
        // 0.3 - MaxStepClimbSpeed * Dt, but landing debt cannot continue a walking run.
        using var ground = new Ground(null, (_, _) => 0.3f);
        MoveState input = WithSignals(Falling(Vector3.Zero)) with { VerticalVelocity = -1 };

        MoveState result = ground.Step(input, Vector2.Zero, 0, false, Tuning);

        Assert.True(result.Grounded, Show(result));
        AssertNear(Tuning.MaxStepClimbSpeed * Dt, Feet(result).Y, HalfSkin, Show(result));
        AssertNoSignals(result);
    }

    [Fact]
    public void SlideLandingAfterAWalkingRiseEmitsNoGroundSignals()
    {
        // The first of three walking substeps climbs the 0.18 tread. The second reaches
        // the 60 degree down face. Its last third slides onto the flat toe at X 0.175.
        float grade = MathF.Tan(Radians(60));
        float toe = 0.18f - grade * (0.175f - 0.12f);
        Vector3 face = Vector3.Normalize(new Vector3(grade, 1, 0));
        using var ground = new Ground(null,
            (x, _) => x < 0 ? 0 : x <= 0.12f ? 0.18f : x < 0.175f ? 0.18f - grade * (x - 0.12f) : toe,
            (x, _) => x > 0.12f && x < 0.175f ? face : Vector3.UnitY);
        MoveState input = WithSignals(Standing(new(-0.1f, 0, 0)));

        MoveState result = ground.Step(input, Vector2.UnitX, 1, false, Tuning, run: true);

        Assert.True(result.Grounded, Show(result));
        Assert.True(Feet(result).X >= 0.175f, Show(result));
        AssertNear(toe, Feet(result).Y, HalfSkin, Show(result));
        AssertNoSignals(result);
    }

    [Fact]
    public void DryWedgeWithACarriedRunEmitsNoGroundSignals()
    {
        float grade = MathF.Tan(Radians(60));
        Vector3 left = Vector3.Normalize(new Vector3(grade, 1, 0));
        Vector3 right = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using var ground = new Ground(null, (x, _) => MathF.Abs(x) * grade,
            (x, _) => x <= 0 ? left : right);

        MoveState result = ground.Step(WithSignals(Falling(Vector3.Zero)), Vector2.Zero, 0, false, Tuning);

        Assert.True(result.Grounded && result.SupportGranted, Show(result));
        AssertNoSignals(result);
    }
}
