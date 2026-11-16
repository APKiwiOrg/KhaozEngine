using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

internal sealed record CoverageReplayMetrics(int EntryIndex, bool ExactValid, CoverageFailure Failure,
    int ChangedTriangleCount, int InheritedTriangleCount, int PositionChangedTriangleCount,
    int AttributesOnlyTriangleCount, IReadOnlyList<CoverageEquipmentDepth> Equipment,
    CoverageDepthResult ChangedRecord, CoverageDepthResult ChangedRecordRaw, CoverageDepthResult Inherited)
{
    /// <summary>Rejoins captured metrics with a row its caller holds.</summary>
    internal CoverageReplayPose ToPose(string domainId, CoverageDenseRow row) => new(domainId, row, EntryIndex, ExactValid,
        Failure, ChangedTriangleCount, InheritedTriangleCount, PositionChangedTriangleCount, AttributesOnlyTriangleCount,
        Equipment, ChangedRecord, ChangedRecordRaw, Inherited);

    internal static CoverageReplayMetrics Capture(CoverageReplayPose pose) => new(pose.EntryIndex, pose.ExactValid,
        pose.Failure, pose.ChangedTriangleCount, pose.InheritedTriangleCount, pose.PositionChangedTriangleCount,
        pose.AttributesOnlyTriangleCount, ImmutableArray.CreateRange(pose.Equipment.ToArray()),
        pose.ChangedRecord, pose.ChangedRecordRaw, pose.Inherited);
}

/// <summary>Addresses immutable metadata, never a decoded input pose or a closure over one.</summary>
internal sealed record CoverageReplayObservation(int DomainIndex, int RowIndex, CoverageReplayMetrics Metrics);

internal sealed class CoverageReplayProjection(CoverageCampaign input, IReadOnlyList<CoverageReplayObservation> observations)
    : IReadOnlyList<CoverageReplayPose>
{
    public int Count => observations.Count;
    public CoverageReplayPose this[int index]
    {
        get
        {
            // The public pose is a copy the caller owns, so the decoded row is released before it is returned.
            var observation = observations[index];
            using var row = input.ReadDenseRow(observation.DomainIndex, observation.RowIndex);
            return observation.Metrics.ToPose(input.Domains[observation.DomainIndex].Id, row.Value);
        }
    }
    public IEnumerator<CoverageReplayPose> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // The logical replay schema needs metadata and metrics only. Writing it never decodes Input.
    internal static IEnumerable<object> Evidence(CoverageCampaign input, IReadOnlyList<CoverageReplayObservation> observations)
    {
        foreach (var observation in observations)
        {
            var domain = input.Domains[observation.DomainIndex];
            var row = domain.DenseRows[observation.RowIndex];
            var m = observation.Metrics;
            yield return new
            {
                DomainId = domain.Id, m.EntryIndex, m.ExactValid, m.Failure, m.ChangedTriangleCount, m.InheritedTriangleCount,
                m.PositionChangedTriangleCount, m.AttributesOnlyTriangleCount, m.Equipment, m.ChangedRecord, m.ChangedRecordRaw,
                m.Inherited, EquipmentCount = m.Equipment.Count, row.DenseIndex, row.PrecedingPhaseTick, row.ResolvedStateKey,
                SampleId = row.Sample.Id, row.Looped, row.Clamped,
            };
        }
    }
}
