using System;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Portable identity for the F3 explicit motion model. Semantic inputs must include the
/// query/clearance recipe, shore rules and actual route-cost inputs. They exclude local lease IDs,
/// handles and frame epochs. Areas remain navigation admission, separate from the motion proof.</summary>
public sealed class ExplicitMovementProfile
{
    public const string ControllerModel = "f3-full-capsule-v1";
    public const int MaximumProbeSteps = 65536;
    public string Name { get; }
    public MoveTuning Tuning { get; }
    public WaterTraversalPolicy Water { get; }
    public NavAreaFilter Areas { get; }
    public float StepSeconds { get; }
    public int MaxSteps { get; }
    public string BoundaryIdentity { get; }
    public string Fingerprint { get; }

    public ExplicitMovementProfile(string name, MoveTuning tuning, WaterTraversalPolicy water,
        NavBakeSources semanticInputs, NavAreaFilter areas = default, float stepSeconds = 1f / 30,
        int maxSteps = 64, string boundaryIdentity = "")
    {
        NavBakeIdentity.CheckName(name, nameof(name), "Profile name");
        ArgumentNullException.ThrowIfNull(semanticInputs);
        ArgumentNullException.ThrowIfNull(boundaryIdentity);
        GroundMoveContext.CheckTuning(tuning);
        if (tuning.Gravity <= 0 || tuning.MaxFallSpeed <= 0 || tuning.SwimEnterDepthFraction <= 0 ||
            !water.IsValid || water.Mode == WaterTraversalMode.Legacy)
            throw new ArgumentException("The profile requires valid explicit movement tuning.", nameof(tuning));
        if (!float.IsFinite(stepSeconds) || stepSeconds <= 0 || maxSteps < 1 || maxSteps > MaximumProbeSteps ||
            !float.IsFinite(stepSeconds * maxSteps)) throw new ArgumentOutOfRangeException(nameof(stepSeconds));
        var sources = semanticInputs.Snapshot();
        if (sources.Count == 0 || sources.Count > ushort.MaxValue)
            throw new ArgumentException("A profile needs a bounded semantic dependency set.", nameof(semanticInputs));
        Name = name;
        Tuning = tuning;
        Water = water;
        Areas = areas;
        StepSeconds = stepSeconds;
        MaxSteps = maxSteps;
        BoundaryIdentity = boundaryIdentity;
        var utf8 = new UTF8Encoding(false, true);
        var writer = new NavBakeWriter();
        writer.WriteUInt16(1);
        writer.WriteLongString(utf8.GetBytes(ControllerModel));
        writer.WriteLongString(utf8.GetBytes(NavBakeIdentity.CurrentEngineVersion));
        writer.WriteShortString(utf8.GetBytes(name));
        writer.WriteUInt32(areas.Required);
        writer.WriteUInt32(areas.Excluded);
        // Reuse the existing canonical tuning encoding, then add the three fields its legacy unit
        // probe omits. The explicit runtime and probe keep the actual configured movement values.
        NavBakeIdentity.WriteTuning(writer, tuning);
        writer.WriteSingle(tuning.WalkSpeed);
        writer.WriteSingle(tuning.RunSpeed);
        writer.WriteUInt8(tuning.AirMomentum ? (byte)1 : (byte)0);
        writer.WriteUInt8((byte)water.Mode);
        writer.WriteSingle(water.SurfaceJumpSpeed);
        writer.WriteSingle(water.SurfaceContactToleranceMetres);
        writer.WriteSingle(water.ContactSkinMetres);
        writer.WriteUInt32(water.ResolverPolicyVersion);
        writer.WriteUInt32(water.ProjectionPolicyVersion);
        writer.WriteInt32(water.MaxCoverageSpans);
        writer.WriteInt32(water.MaxDomainContacts);
        writer.WriteInt32(water.MaxWaterDomainsPerLease);
        writer.WriteInt32(water.MaxSolidContacts);
        writer.WriteInt32(water.MaxDistinctNormals);
        writer.WriteInt32(water.MaxCorrections);
        writer.WriteSingle(stepSeconds);
        writer.WriteInt32(maxSteps);
        writer.WriteLongString(utf8.GetBytes(boundaryIdentity));
        writer.WriteUInt16((ushort)sources.Count);
        foreach (var source in sources)
        {
            writer.WriteShortString(utf8.GetBytes(source.Label));
            writer.WriteBytes(source.Digest);
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(writer.WrittenSpan));
    }
}
