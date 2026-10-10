using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class ShellGeometryTests
{
    static MoveTuning Tuning(float halfHeight, float radius, float step) =>
        MoveTuning.Default with { CapsuleHalfHeight = halfHeight, CapsuleRadius = radius, StepHeight = step };

    [Fact]
    public void ShellOfTheDefaultTuning()
    {
        MoveTuning tuning = MoveTuning.Default;
        ShellGeometry.Validate(tuning);

        CapsuleShape shape = ShellGeometry.Shape(tuning);
        Assert.Equal(2 * 0.9 - 0.4 - 2 * 0.4, shape.Length, 1e-6);
        Assert.Equal(0.4f, shape.Radius);

        Vector3 feet = new(3f, -2f, 5f);
        Vector3 centre = ShellGeometry.Centre(feet, tuning);
        Assert.Equal(3f, centre.X);
        Assert.Equal(-2f + 1.1f, centre.Y, 1e-6f);
        Assert.Equal(5f, centre.Z);
    }

    [Fact]
    public void ShellAtTheMinimumIsASphere()
    {
        MoveTuning tuning = Tuning(0.6f, 0.4f, 0.4f);
        ShellGeometry.Validate(tuning);

        CapsuleShape shape = ShellGeometry.Shape(tuning);
        Assert.Equal(0.01f, shape.Length);
        Assert.Equal(0.4f, shape.Radius);
    }

    [Fact]
    public void ShellTooShortThrows()
    {
        MoveTuning tuning = Tuning(0.5f, 0.4f, 0.4f);
        Assert.Throws<ArgumentException>(() => ShellGeometry.Validate(tuning));
    }

    // Radius 0.4 and StepHeight 0.18 put the lower cap centre 0.58 above the feet. At the bare 45 degree gate it lies
    // 0.58 * cos(45) = 0.410 from the plane, clear of radius plus skin 0.401. A footed body is kept to the banded
    // 48 degrees, where it lies 0.58 * cos(48) = 0.388 from the plane, inside the shell.
    [Fact]
    public void ShellMustClearTheBandedPlane()
    {
        MoveTuning tuning = Tuning(0.9f, 0.4f, 0.18f);
        ShellGeometry.Validate(tuning with { TractionHysteresisRadians = 0 });

        ArgumentException error = Assert.Throws<ArgumentException>(() => ShellGeometry.Validate(tuning));

        Assert.Contains("TractionHysteresisRadians", error.Message);
    }

    // The consumer bodies clear their banded plane with room to spare. The engine default lies 0.8 * cos(48) = 0.535
    // from it against 0.401. A Grimhollow player (radius 0.3, StepHeight 0.4, 45 degrees, default band) lies
    // 0.7 * cos(48) = 0.468 against 0.301. Ruinborne (radius 0.4, StepHeight 0.4, 49 degrees, 1 degree band) lies
    // 0.8 * cos(50) = 0.514 against 0.401.
    [Theory]
    [InlineData(0.9f, 0.4f, 0.4f, 45f, 3f)]
    [InlineData(0.75f, 0.3f, 0.4f, 45f, 3f)]
    [InlineData(0.9f, 0.3f, 0.4f, 45f, 3f)]
    [InlineData(0.9f, 0.4f, 0.4f, 49f, 1f)]
    public void ConsumerShellsClearTheBandedPlane(float halfHeight, float radius, float step, float slope, float band)
    {
        MoveTuning tuning = Tuning(halfHeight, radius, step) with
        {
            MaxSlopeRadians = slope * MathF.PI / 180f,
            TractionHysteresisRadians = band * MathF.PI / 180f,
        };

        ShellGeometry.Validate(tuning);
    }
}
