using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The contact controller's shell for callers outside locomotion: the upright capsule above knee height that
/// blocks walls and ceilings. It spans from <c>feet + StepHeight</c> to <c>feet + 2 * CapsuleHalfHeight</c>. Every
/// member delegates to the controller's own shell geometry, so the body model has one implementation.</summary>
public static class ContactShell
{
    /// <summary>Throws <see cref="System.ArgumentException"/> when the shell span cannot hold its radius or the shell
    /// cannot clear its steepest walkable plane with the contact skin.</summary>
    public static void Validate(in MoveTuning tuning) => ShellGeometry.Validate(tuning);

    /// <summary>The shell capsule, upright under an identity orientation.</summary>
    public static CapsuleShape Shape(in MoveTuning tuning) => ShellGeometry.Shape(tuning);

    /// <summary>The shell centre for a body whose feet are at <paramref name="feet"/>.</summary>
    public static Vector3 Centre(Vector3 feet, in MoveTuning tuning) => ShellGeometry.Centre(feet, tuning);
}
