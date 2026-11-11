using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Completes independent row observations after acceptance preflight or measurement refuses.</summary>
internal static class CoverageReplayDiagnostics
{
    internal static void Collect(CoverageDiagnosticDocument document, CoverageBuildInput input,
        CoverageRecessionTable table, CoverageReplayResult? replay)
    {
        var failures = new List<CoverageDenseFailureDiagnostic>();
        var changed = new List<CoverageDepthDiagnostic>();
        var inherited = new List<CoverageDepthDiagnostic>();
        var measurement = replay == null ? new CoverageReplayMeasurement(input, table) : null;
        int poseIndex = 0;
        document.Add("missingDenseWork", input.Domains.Where(d => d.DenseRows.Count == 0)
            .Select(d => new { DomainId = d.Id, Reason = "MissingDenseRows" }));
        foreach (var domain in input.Domains)
            for (int index = 0; index < domain.DenseRows.Count; index++)
            {
                var row = domain.DenseRows[index];
                // This projection only labels diagnostic work. CoverageReplay.ValidateRows remains the acceptance gate.
                var sample = row.Sample;
                CoverageVerificationRefusal? declaration = row.DenseIndex != index ? CoverageVerificationRefusal.DenseSequence
                    : row.Looped && row.Clamped ? CoverageVerificationRefusal.ReplayPolicy
                    : row.PrecedingPhaseTick < 0 || !row.Looped && !row.Clamped && row.PrecedingPhaseTick != row.DenseIndex / 4 ? CoverageVerificationRefusal.PrecedingPhaseTick
                    : sample.DomainId != domain.Id || sample.AuthoredFrameId == null && sample.BlendPathId == null
                        || sample.AuthoredFrameId != null && !domain.AuthoredFrameIds.Contains(sample.AuthoredFrameId, StringComparer.Ordinal)
                        || sample.BlendPathId != null && !domain.BlendPathIds.Contains(sample.BlendPathId, StringComparer.Ordinal) ? CoverageVerificationRefusal.DenseDomainMembership
                    : sample.Commands.Length == 0 ? CoverageVerificationRefusal.MissingEquipment : null;
                if (declaration.HasValue) failures.Add(Failure(domain, row, declaration.Value.ToString()));
                CoverageReplayPose pose;
                try
                {
                    pose = replay == null ? measurement!.Measure(domain, row, CoverageWholeSkinDepth.DefaultQueryBudget,
                    error => failures.Add(new(domain.Id, row.DenseIndex, row.ResolvedStateKey, sample.Id, error.Reason.ToString(),
                        error.EntryIndex, error.CommandId, error.EquipmentInstanceId, error.Failure))) : replay.Poses[poseIndex++];
                }
                catch (CoverageVerificationException error)
                {
                    failures.Add(new(domain.Id, row.DenseIndex, row.ResolvedStateKey, sample.Id, error.Reason.ToString(),
                        error.EntryIndex, error.CommandId, error.EquipmentInstanceId, error.Failure));
                    continue;
                }
                catch (CoverageBuildValidationException error)
                {
                    failures.Add(Failure(domain, row, error.Reason.ToString(), error.Failure));
                    continue;
                }
                CoverageVerificationRefusal? reason = pose.EntryIndex < 0 ? CoverageVerificationRefusal.ReplayHole
                    : !pose.ExactValid ? CoverageVerificationRefusal.SelectedReplayFailure
                    : !pose.ChangedRecord.Resolved ? CoverageVerificationRefusal.UnresolvedChangedRecords
                    : pose.ChangedRecord.ObservedMm > 0 || pose.ChangedRecord.UpperEstimateMm > 0 ? CoverageVerificationRefusal.ChangedRecordPenetration : null;
                if (reason.HasValue) failures.Add(new(domain.Id, row.DenseIndex, row.ResolvedStateKey, sample.Id,
                    reason.Value.ToString(), pose.EntryIndex, null, null, pose.Failure));
                foreach (var equipment in pose.Equipment)
                {
                    if (Finding(equipment.ChangedRecord)) changed.Add(Depth(pose, equipment, equipment.ChangedRecord,
                        equipment.ChangedRecordRaw, pose.ChangedTriangleCount));
                    if (Finding(equipment.Inherited)) inherited.Add(Depth(pose, equipment, equipment.Inherited, null, pose.InheritedTriangleCount));
                }
            }
        document.Add("replayFailures", failures);
        document.Add("changedRecordDepth", changed);
        document.Add("inheritedDepth", inherited);
    }
    static bool Finding(CoverageDepthResult depth) => depth.ObservedMm > 0 || depth.UpperEstimateMm > 0 || !depth.Resolved;
    static CoverageDepthDiagnostic Depth(CoverageReplayPose pose, CoverageEquipmentDepth equipment,
        CoverageDepthResult depth, CoverageDepthResult? raw, int count) => new(pose.DomainId, pose.DenseIndex,
            pose.ResolvedStateKey, pose.SampleId, pose.EntryIndex, equipment.CommandId, equipment.EquipmentInstanceId, count, depth, raw);
    static CoverageDenseFailureDiagnostic Failure(CoverageDomain domain, CoverageDenseRow row, string reason, CoverageFailure? failure = null)
        => new(domain.Id, row.DenseIndex, row.ResolvedStateKey, row.Sample.Id, reason, null, null, null, failure);
}

internal sealed record CoverageDenseFailureDiagnostic(string DomainId, int DenseIndex, ulong ResolvedStateKey, string SampleId,
    string Reason, int? EntryIndex, uint? CommandId, ulong? EquipmentInstanceId, CoverageFailure? Failure);
internal sealed record CoverageDepthDiagnostic(string DomainId, int DenseIndex, ulong ResolvedStateKey, string SampleId, int EntryIndex,
    uint CommandId, ulong EquipmentInstanceId, int TriangleCount, CoverageDepthResult Depth, CoverageDepthResult? RawDepth);
