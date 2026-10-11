using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.Movement;

// Compatibility fixture for the frozen F3 certificate and its contact replacement. Use an
// independent writer so missing recipe inputs cannot also disappear from the expected hash.
internal static class ExplicitProfileRecipe
{
    internal static string Fingerprint(ExplicitMovementProfile profile, NavBakeSources sources, bool contact,
        float footprintFraction = 0.5f, float placementOffset = 0.001f)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)1);
        LongString(contact ? "contact-shell-footprint-swim-v1" : "f3-full-capsule-v1");
        if (contact)
        {
            writer.Write(footprintFraction);
            writer.Write(placementOffset);
            writer.Write(1u); // Within-tick ownership handoff recipe.
            writer.Write(1u); // Shell, footprint and complete medium-route proof recipe.
        }
        LongString(NavBakeIdentity.CurrentEngineVersion);
        ShortString(profile.Name);
        writer.Write(profile.Areas.Required);
        writer.Write(profile.Areas.Excluded);
        var t = profile.Tuning;
        foreach (float value in new[] { t.CapsuleHalfHeight, t.MaxSlopeRadians, t.CapsuleRadius,
            t.Gravity, t.JumpSpeed, t.MaxFallSpeed, t.CoyoteTime, t.JumpBuffer, t.AirControl,
            t.GroundedEpsilon, t.StepHeight, t.WadeStartDepthFraction, t.WadeEndDepthFraction,
            t.WadeMinSpeedScale, t.SwimEnterDepthFraction, t.SwimExitDepthFraction, t.SwimSpeed,
            t.SwimSurfaceSubmersionFraction, t.SwimBuoyancyStiffness, t.MaxStepClimbSpeed,
            t.AirBrakeAccel, t.FacingTurnSpeed, t.TractionHysteresisRadians,
            t.SlideFrictionRampRadians, t.StrafeSpeedScale, t.BackpedalSpeedScale }) writer.Write(value);
        writer.Write(t.BackpedalAllowsRun);
        writer.Write(t.WalkSpeed);
        writer.Write(t.RunSpeed);
        writer.Write(t.AirMomentum);
        var water = profile.Water;
        writer.Write((byte)water.Mode);
        writer.Write(water.SurfaceJumpSpeed);
        writer.Write(water.SurfaceContactToleranceMetres);
        writer.Write(water.ContactSkinMetres);
        writer.Write(water.ResolverPolicyVersion);
        writer.Write(water.ProjectionPolicyVersion);
        writer.Write(water.MaxCoverageSpans);
        writer.Write(water.MaxDomainContacts);
        writer.Write(water.MaxWaterDomainsPerLease);
        writer.Write(water.MaxSolidContacts);
        writer.Write(water.MaxDistinctNormals);
        writer.Write(water.MaxCorrections);
        writer.Write(profile.StepSeconds);
        writer.Write(profile.MaxSteps);
        LongString(profile.BoundaryIdentity);
        var snapshot = sources.Snapshot();
        writer.Write((ushort)snapshot.Count);
        foreach (var source in snapshot)
        {
            ShortString(source.Label);
            writer.Write(source.Digest);
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));

        void LongString(string value)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(value);
            writer.Write((ushort)encoded.Length);
            writer.Write(encoded);
        }
        void ShortString(string value)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(value);
            writer.Write((byte)encoded.Length);
            writer.Write(encoded);
        }
    }
}
