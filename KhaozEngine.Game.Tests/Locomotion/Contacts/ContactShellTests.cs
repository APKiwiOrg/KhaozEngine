using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>The public contact shell is the internal shell geometry, with no rule of its own.</summary>
public class ContactShellTests
{
    static MoveTuning Tuning(float halfHeight, float radius, float step) =>
        MoveTuning.Default with { CapsuleHalfHeight = halfHeight, CapsuleRadius = radius, StepHeight = step };

    [Fact]
    public void ShapeAndCentre_AreTheShellGeometry()
    {
        var feet = new Vector3(3f, -2f, 5f);
        foreach (MoveTuning tuning in new[] { MoveTuning.Default, Tuning(0.6f, 0.4f, 0.4f), Tuning(1.2f, 0.3f, 0.5f) })
        {
            ContactShell.Validate(tuning);
            // CapsuleShape is a class without value equality, so its two dimensions are compared.
            CapsuleShape expected = ShellGeometry.Shape(tuning), actual = ContactShell.Shape(tuning);
            Assert.Equal((expected.Radius, expected.Length), (actual.Radius, actual.Length));
            Assert.Equal(ShellGeometry.Centre(feet, tuning), ContactShell.Centre(feet, tuning));
        }
    }

    [Fact]
    public void Validate_RefusesWhatTheShellGeometryRefuses()
    {
        // The first shell span, 2 * 0.5 - 0.4, is shorter than its 0.8 diameter. In the second, (0.05 + 0.4) *
        // cos(45 degrees) is under 0.4 plus the 0.001 m skin, so the shell cannot clear its steepest walkable plane.
        foreach (MoveTuning tuning in new[] { Tuning(0.5f, 0.4f, 0.4f), Tuning(0.9f, 0.4f, 0.05f) })
        {
            Assert.Throws<ArgumentException>(() => ShellGeometry.Validate(tuning));
            Assert.Throws<ArgumentException>(() => ContactShell.Validate(tuning));
        }
    }
}
