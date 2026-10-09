using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The shell is the upright capsule above knee height that blocks walls and ceilings. It spans from
/// <c>feet + StepHeight</c> to <c>feet + 2 * CapsuleHalfHeight</c>, so anything at or below the step height is
/// left to foot support.</summary>
internal static class ShellGeometry
{
    /// <summary>Throws when the span above the step height cannot hold a sphere of the capsule radius.</summary>
    internal static void Validate(in MoveTuning tuning)
    {
        float span = Span(tuning);
        float diameter = 2f * tuning.CapsuleRadius;
        if (!(span >= diameter))
            throw new ArgumentException(
                $"The shell span 2 * CapsuleHalfHeight - StepHeight ({span}) must be at least 2 * CapsuleRadius ({diameter}).",
                nameof(tuning));
    }

    /// <summary>The shell capsule. Its cylindrical length takes the same 0.01 floor as the body capsule, so a
    /// span of exactly one diameter is a sphere.</summary>
    internal static CapsuleShape Shape(in MoveTuning tuning)
        => new(tuning.CapsuleRadius, MathF.Max(0.01f, Span(tuning) - 2f * tuning.CapsuleRadius));

    /// <summary>The shell centre for a body whose feet are at <paramref name="feet"/>.</summary>
    internal static Vector3 Centre(Vector3 feet, in MoveTuning tuning)
        => feet + new Vector3(0f, 0.5f * (tuning.StepHeight + 2f * tuning.CapsuleHalfHeight), 0f);

    static float Span(in MoveTuning tuning) => 2f * tuning.CapsuleHalfHeight - tuning.StepHeight;
}
