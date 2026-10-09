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
}
