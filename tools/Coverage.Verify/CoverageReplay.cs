using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
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
    readonly CoverageCampaign input;
    readonly IReadOnlyList<CoverageReplayObservation> observations;
    internal CoverageReplayResult(CoverageCampaign input, IEnumerable<CoverageReplayObservation> observations, int queryBudget)
    {
        this.input = input;
        this.observations = ImmutableArray.CreateRange(observations.ToArray());
        Poses = new CoverageReplayProjection(input, this.observations);
        QueryBudgetPerClassEquipmentPose = queryBudget;
    }
    public int QueryBudgetPerClassEquipmentPose { get; }
    public double DepthResolutionMm => CoverageWholeSkinDepth.ResolutionMm;
    public string ArithmeticLimitations => "Double transforms, winding and distance estimates over sampled poses. No certified continuous enclosure.";
    public IReadOnlyList<CoverageReplayPose> Poses { get; }
    public int PoseCount => Poses.Count;
    public int HoleCount => observations.Count(p => p.Metrics.EntryIndex < 0);
    public int SelectedFailures => observations.Count(p => p.Metrics.EntryIndex >= 0 && !p.Metrics.ExactValid);
    public double ChangedRecordObservedMm => observations.Max(p => p.Metrics.ChangedRecord.ObservedMm);
    public double ChangedRecordUpperEstimateMm => observations.Max(p => p.Metrics.ChangedRecord.UpperEstimateMm);
    public double ChangedRecordRawDepthMm => observations.Max(p => p.Metrics.ChangedRecordRaw.ObservedMm);
    public double InheritedObservedMm => observations.Max(p => p.Metrics.Inherited.ObservedMm);
    public double InheritedUpperEstimateMm => observations.Max(p => p.Metrics.Inherited.UpperEstimateMm);
    public int UnresolvedChangedRecordPoses => observations.Count(p => !p.Metrics.ChangedRecord.Resolved);
    public int UnresolvedInheritedPoses => observations.Count(p => !p.Metrics.Inherited.Resolved);
    public long Queries => observations.Sum(p => (long)p.Metrics.ChangedRecord.Queries + p.Metrics.ChangedRecordRaw.Queries + p.Metrics.Inherited.Queries);
    public bool Accepted => PoseCount > 0 && HoleCount == 0 && SelectedFailures == 0 && UnresolvedChangedRecordPoses == 0
        && ChangedRecordObservedMm == 0 && ChangedRecordUpperEstimateMm == 0;

    public void EnsureAccepted()
    {
        foreach (var observation in observations)
        {
            var pose = observation.Metrics;
            CoverageVerificationRefusal? reason = pose.EntryIndex < 0 ? CoverageVerificationRefusal.ReplayHole
                : !pose.ExactValid ? CoverageVerificationRefusal.SelectedReplayFailure
                : !pose.ChangedRecord.Resolved ? CoverageVerificationRefusal.UnresolvedChangedRecords
                : pose.ChangedRecord.ObservedMm > 0 || pose.ChangedRecord.UpperEstimateMm > 0 ? CoverageVerificationRefusal.ChangedRecordPenetration : null;
            if (reason.HasValue)
            {
                var equipment = reason is CoverageVerificationRefusal.UnresolvedChangedRecords or CoverageVerificationRefusal.ChangedRecordPenetration
                    ? pose.Equipment.FirstOrDefault(e => reason == CoverageVerificationRefusal.UnresolvedChangedRecords
                        ? !e.ChangedRecord.Resolved : e.ChangedRecord.ObservedMm > 0 || e.ChangedRecord.UpperEstimateMm > 0) : null;
                var domain = input.Domains[observation.DomainIndex];
                var row = domain.DenseRows[observation.RowIndex];
                throw new CoverageVerificationException(reason.Value, "Dense coverage verification was refused.")
                {
                    Failure = pose.Failure, DomainId = domain.Id, DenseIndex = row.DenseIndex, SampleId = row.Sample.Id,
                    StateKey = row.ResolvedStateKey, EntryIndex = pose.EntryIndex,
                    CommandId = equipment?.CommandId, EquipmentInstanceId = equipment?.EquipmentInstanceId,
                };
            }
        }
    }

    internal void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("queryBudgetPerClassEquipmentPose", QueryBudgetPerClassEquipmentPose);
        writer.WriteNumber("depthResolutionMm", DepthResolutionMm); writer.WriteString("arithmeticLimitations", ArithmeticLimitations);
        writer.WriteStartArray("poses");
        foreach (var pose in CoverageReplayProjection.Evidence(input, observations))
        {
            JsonSerializer.Serialize(writer, pose, options); writer.Flush();
        }
        writer.WriteEndArray(); writer.WriteNumber("poseCount", PoseCount); writer.WriteNumber("holeCount", HoleCount);
        writer.WriteNumber("selectedFailures", SelectedFailures); writer.WriteNumber("changedRecordObservedMm", ChangedRecordObservedMm);
        writer.WriteNumber("changedRecordUpperEstimateMm", ChangedRecordUpperEstimateMm); writer.WriteNumber("changedRecordRawDepthMm", ChangedRecordRawDepthMm);
        writer.WriteNumber("inheritedObservedMm", InheritedObservedMm); writer.WriteNumber("inheritedUpperEstimateMm", InheritedUpperEstimateMm);
        writer.WriteNumber("unresolvedChangedRecordPoses", UnresolvedChangedRecordPoses); writer.WriteNumber("unresolvedInheritedPoses", UnresolvedInheritedPoses);
        writer.WriteNumber("queries", Queries); writer.WriteBoolean("accepted", Accepted); writer.WriteEndObject(); writer.Flush();
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
        return Verify(CoverageCampaign.FromSnapshot(input), table, queryBudget);
    }

    internal static CoverageReplayResult Verify(CoverageCampaign input, CoverageRecessionTable table,
        int queryBudget = CoverageWholeSkinDepth.DefaultQueryBudget)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queryBudget);
        if (input.Identity != table.Identity)
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.ProvenanceMismatch, "Table provenance differs from replay inputs.");
        ValidateRows(input);
        var measurement = new CoverageReplayMeasurement(input, table);
        var results = new List<CoverageReplayObservation>();
        for (int domain = 0; domain < input.Domains.Count; domain++)
            for (int row = 0; row < input.Domains[domain].DenseRows.Count; row++)
                results.Add(Measure(domain, row));
        return new(input, results, queryBudget);

        CoverageReplayObservation Measure(int domain, int row) => new(domain, row,
            CoverageReplayMetrics.Capture(measurement.Measure(input.Domains[domain], input.ReadDenseRow(domain, row), queryBudget)));
    }

    static void ValidateRows(CoverageCampaign input)
    {
        for (int domainIndex = 0; domainIndex < input.Domains.Count; domainIndex++)
        {
            var domain = input.Domains[domainIndex];
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
                    : input.ReadDenseRow(domainIndex, index).Sample.Commands.Length == 0 ? CoverageVerificationRefusal.MissingEquipment : null;
                if (reason.HasValue) throw new CoverageVerificationException(reason.Value, "Dense replay declaration was refused.")
                    .At(domain.Id, input.ReadDenseRow(domainIndex, index));
            }
        }
    }
}
