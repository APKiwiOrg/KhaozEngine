using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

public sealed record CoverageEquipmentDepth(uint CommandId, ulong EquipmentInstanceId,
    CoverageDepthResult ChangedRecord, CoverageDepthResult ChangedRecordRaw, CoverageDepthResult Inherited);

public sealed record CoverageReplayPose(string DomainId, [property: JsonIgnore] CoverageDenseRow Input, int EntryIndex,
    bool ExactValid, CoverageFailure Failure, int ChangedTriangleCount, int InheritedTriangleCount,
    int PositionChangedTriangleCount, int AttributesOnlyTriangleCount, IReadOnlyList<CoverageEquipmentDepth> Equipment,
    CoverageDepthResult ChangedRecord, CoverageDepthResult ChangedRecordRaw, CoverageDepthResult Inherited)
{
    public int EquipmentCount => Equipment.Count;
    public int DenseIndex => Input.DenseIndex;
    public int PrecedingPhaseTick => Input.PrecedingPhaseTick;
    public ulong ResolvedStateKey => Input.ResolvedStateKey;
    public string SampleId => Input.Sample.Id;
    public bool Looped => Input.Looped;
    public bool Clamped => Input.Clamped;
}

/// <summary>Complete sampled replay. Inherited depth never excuses penetration on a changed record.
/// Raw depth on the same changed triangles is diagnostic, not a subtraction or causation proof.</summary>
public sealed class CoverageReplayResult
{
    internal CoverageReplayResult(IEnumerable<CoverageReplayPose> poses, int queryBudget)
    {
        Poses = Array.AsReadOnly(poses.ToArray());
        QueryBudgetPerClassEquipmentPose = queryBudget;
    }
    public int QueryBudgetPerClassEquipmentPose { get; }
    public double DepthResolutionMm => CoverageWholeSkinDepth.ResolutionMm;
    public string ArithmeticLimitations => "Double transforms, winding and distance estimates over sampled poses. No certified continuous enclosure.";
    public IReadOnlyList<CoverageReplayPose> Poses { get; }
    public int PoseCount => Poses.Count;
    public int HoleCount => Poses.Count(p => p.EntryIndex < 0);
    public int SelectedFailures => Poses.Count(p => p.EntryIndex >= 0 && !p.ExactValid);
    public double ChangedRecordObservedMm => Poses.Max(p => p.ChangedRecord.ObservedMm);
    public double ChangedRecordUpperEstimateMm => Poses.Max(p => p.ChangedRecord.UpperEstimateMm);
    public double ChangedRecordRawDepthMm => Poses.Max(p => p.ChangedRecordRaw.ObservedMm);
    public double InheritedObservedMm => Poses.Max(p => p.Inherited.ObservedMm);
    public double InheritedUpperEstimateMm => Poses.Max(p => p.Inherited.UpperEstimateMm);
    public int UnresolvedChangedRecordPoses => Poses.Count(p => !p.ChangedRecord.Resolved);
    public int UnresolvedInheritedPoses => Poses.Count(p => !p.Inherited.Resolved);
    public long Queries => Poses.Sum(p => (long)p.ChangedRecord.Queries + p.ChangedRecordRaw.Queries + p.Inherited.Queries);
    public bool Accepted => PoseCount > 0 && HoleCount == 0 && SelectedFailures == 0 && UnresolvedChangedRecordPoses == 0
        && ChangedRecordObservedMm == 0 && ChangedRecordUpperEstimateMm == 0;

    public void EnsureAccepted()
    {
        foreach (var pose in Poses)
        {
            CoverageVerificationRefusal? reason = pose.EntryIndex < 0 ? CoverageVerificationRefusal.ReplayHole
                : !pose.ExactValid ? CoverageVerificationRefusal.SelectedReplayFailure
                : !pose.ChangedRecord.Resolved ? CoverageVerificationRefusal.UnresolvedChangedRecords
                : pose.ChangedRecord.ObservedMm > 0 || pose.ChangedRecord.UpperEstimateMm > 0 ? CoverageVerificationRefusal.ChangedRecordPenetration : null;
            if (reason.HasValue)
            {
                var equipment = reason is CoverageVerificationRefusal.UnresolvedChangedRecords or CoverageVerificationRefusal.ChangedRecordPenetration
                    ? pose.Equipment.FirstOrDefault(e => reason == CoverageVerificationRefusal.UnresolvedChangedRecords
                        ? !e.ChangedRecord.Resolved : e.ChangedRecord.ObservedMm > 0 || e.ChangedRecord.UpperEstimateMm > 0) : null;
                throw new CoverageVerificationException(reason.Value, "Dense coverage verification was refused.") { Failure = pose.Failure }
                    .At(pose.DomainId, pose.Input, pose.EntryIndex, equipment?.CommandId, equipment?.EquipmentInstanceId);
            }
        }
    }
}

/// <summary>Replays consumer-supplied current poses and opaque keys. Never samples animation, infers bins,
/// clamps a path, loops a clip or substitutes an earlier pose.</summary>
public static class CoverageReplay
{
    public static CoverageReplayResult Verify(CoverageBuildInput input, CoverageRecessionTable table,
        int queryBudget = CoverageWholeSkinDepth.DefaultQueryBudget)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queryBudget);
        if (input.Identity != table.Identity)
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.ProvenanceMismatch, "Table provenance differs from replay inputs.");
        ValidateRows(input);
        var measurement = new CoverageReplayMeasurement(input, table);
        var results = new List<CoverageReplayPose>();
        foreach (var domain in input.Domains)
            foreach (var row in domain.DenseRows)
                results.Add(measurement.Measure(domain, row, queryBudget));
        return new(results, queryBudget);
    }

    static void ValidateRows(CoverageBuildInput input)
    {
        foreach (var domain in input.Domains)
        {
            if (domain.DenseRows.Count == 0)
                throw new CoverageVerificationException(CoverageVerificationRefusal.MissingDenseRows, "Each domain requires nonempty 240 Hz work.") { DomainId = domain.Id };
            for (int index = 0; index < domain.DenseRows.Count; index++)
            {
                var row = domain.DenseRows[index];
                var sample = row.Sample;
                CoverageVerificationRefusal? reason = row.DenseIndex != index ? CoverageVerificationRefusal.DenseSequence
                    : row.Looped && row.Clamped ? CoverageVerificationRefusal.ReplayPolicy
                    : row.PrecedingPhaseTick < 0 || !row.Looped && !row.Clamped && row.PrecedingPhaseTick != row.DenseIndex / 4 ? CoverageVerificationRefusal.PrecedingPhaseTick
                    : sample.DomainId != domain.Id || sample.AuthoredFrameId == null && sample.BlendPathId == null
                        || sample.AuthoredFrameId != null && !domain.AuthoredFrameIds.Contains(sample.AuthoredFrameId, StringComparer.Ordinal)
                        || sample.BlendPathId != null && !domain.BlendPathIds.Contains(sample.BlendPathId, StringComparer.Ordinal) ? CoverageVerificationRefusal.DenseDomainMembership
                    : sample.Commands.Length == 0 ? CoverageVerificationRefusal.MissingEquipment : null;
                if (reason.HasValue) throw new CoverageVerificationException(reason.Value, "Dense replay declaration was refused.").At(domain.Id, row);
            }
        }
    }
}
