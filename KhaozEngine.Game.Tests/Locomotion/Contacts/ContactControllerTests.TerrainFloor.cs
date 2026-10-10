// Rows for the terrain floor: no path leaves the feet below analytic terrain, where nothing would ever catch them
// again. Expectations come from the installed geometry and the closed forms.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactControllerTests
{
    // Terrain steps from 0 to a 0.6 bank top past x 0.8, and a static roof whose underside is 2.1 covers x 1 to 4. A
    // body falls from feet 0.3 at x 0.61, steered +X for one tick at 6 m/s and then released. Its last substep ends
    // at x 0.81 with the bank top 0.33 above the feet, inside the landing band. The paced seat's shell meets the
    // roof's edge, whose push moves the axis back past 0.8 off the bank, so the seat fails clearance. The body lands
    // unraised on that substep, with the bank top within StepHeight above its feet, and every later idle tick stays
    // grounded with the feet within StepHeight of the bank top, never lower than where it landed.
    [Fact]
    public void FailedSeatOverATerrainBankLands()
    {
        const float Edge = 0.8f, Top = 0.6f;
        using var g = new Ground(new FootSupportScene(SceneVariant.Box)
            .Ceiling("roof", new Vector3(2.5f, 2.1f, 0), 1.5f, 2), (x, _) => x > Edge ? Top : 0);
        MoveState s = g.Step(Falling(new Vector3(0.61f, 0.3f, 0)), Vector2.UnitX, 1, false, Tuning);
        Assert.True(Feet(s).X > Edge, $"the tick never reached the bank: {Show(s)}");
        Assert.True(s.Grounded, $"the failed seat did not land: {Show(s)}");
        float landed = Feet(s).Y;
        Assert.True(Top - landed <= Tuning.StepHeight, $"landed out of the band: {Show(s)}");

        for (int tick = 1; tick <= 30; tick++)
        {
            s = g.Step(s, Vector2.Zero, 0, false, Tuning);
            Assert.True(s.Grounded, $"tick {tick}: {Show(s)}");
            Assert.True(Feet(s).Y >= landed - HalfSkin && Top - Feet(s).Y <= Tuning.StepHeight,
                $"tick {tick} left the band: {Show(s)}");
        }
    }

    // Terrain steps from 0 to a 0.6 bank top past x 0.8, with no roof. A body stands 0.3 below the bank top at x 0.81,
    // the lag a failed seat leaves, and its jump has just been stamped. The air pass owns airborne feet within StepHeight
    // below walkable terrain, so the jump rises from the lag in free flight, exactly the closed form's first tick.
    // Lifting the feet onto the bank first would launch the jump from the bank top, and under a roof that lift is what
    // put the shell into the roof and let recovery throw the body off the bank.
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void JumpFromALagBelowATerrainBankRisesFromTheLag(int hz)
    {
        const float Edge = 0.8f, Lag = 0.3f;
        float dt = 1f / hz;
        using var g = new Ground(null, (x, _) => x > Edge ? 0.6f : 0);
        MoveState s = Falling(new Vector3(0.81f, Lag, 0)) with { VerticalVelocity = Tuning.JumpSpeed };

        s = g.Step(s, Vector2.Zero, 0, false, Tuning, dt);

        Assert.False(s.Grounded, Show(s));
        AssertNear(0.81f, Feet(s).X, 0, Show(s));
        AssertNear(Lag + (Tuning.JumpSpeed - (double)Gravity * dt) * dt, Feet(s).Y, HalfSkin,
            $"the jump did not rise from the lag: {Show(s)}");
    }

    // The same bank and lag with the body falling from rest. The air pass lands it on the bank through the ground core's
    // placement, which pays the rise from the tick's climb budget. Lifting the feet onto the bank first would climb the
    // whole 0.3 in one tick.
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void FallFromALagBelowATerrainBankLandsPaced(int hz)
    {
        const float Edge = 0.8f, Lag = 0.3f;
        float dt = 1f / hz;
        using var g = new Ground(null, (x, _) => x > Edge ? 0.6f : 0);

        MoveState s = g.Step(Falling(new Vector3(0.81f, Lag, 0)), Vector2.Zero, 0, false, Tuning, dt);

        Assert.True(s.Grounded, Show(s));
        Assert.True(Feet(s).Y - Lag <= Tuning.MaxStepClimbSpeed * (double)dt + HalfSkin,
            $"the landing climbed past one budget: {Show(s)}");
        Assert.True(Feet(s).Y > Lag, $"the landing did not climb: {Show(s)}");
    }

    // A body whose feet start a metre below analytic terrain, deeper than any band reaches, is invalid state that only
    // a defect leaves. The tick seats it on the terrain at its axis before it moves: on level terrain it lands there,
    // and on a 60 degree face rising to +X it slides from the surface and stays on it.
    [Theory]
    [InlineData(0f)]
    [InlineData(60f)]
    public void FeetBelowTerrainAreSeatedOnIt(float degrees)
    {
        float grade = MathF.Tan(Radians(degrees));
        Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using var g = new Ground(null, (x, _) => grade * x, (_, _) => normal);
        MoveState s = Falling(new Vector3(1, grade - 1, 0));

        s = g.Step(s, Vector2.Zero, 0, false, Tuning);

        AssertNear(grade * Feet(s).X, Feet(s).Y, HalfSkin, Show(s));
        Assert.Equal(degrees == 0, s.Grounded);
        if (degrees != 0) Assert.True(s.VerticalVelocity < 0 && Feet(s).X < 1, $"not sliding: {Show(s)}");
    }
}
