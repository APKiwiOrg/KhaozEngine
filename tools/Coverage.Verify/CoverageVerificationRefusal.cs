using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

public enum CoverageVerificationRefusal
{
    MissingDenseRows, DenseSequence, PrecedingPhaseTick, ReplayPolicy, DenseDomainMembership, MissingEquipment,
    SelectedSampleFailure, SampleHole, ReplayHole, SelectedReplayFailure, ChangedRecordPenetration, UnresolvedChangedRecords,
    OpenEquipmentSolid, DepthArithmetic,
}

/// <summary>Blocking verification refusal. Replay context names the domain, dense index, opaque state key and
/// selected entry when available. Equipment context names the current command and instance.</summary>
public sealed class CoverageVerificationException : InvalidOperationException
{
    public CoverageVerificationException(CoverageVerificationRefusal reason, string message) : base(message) => Reason = reason;
    public CoverageVerificationRefusal Reason { get; }
    public string? DomainId { get; init; }
    public int? DenseIndex { get; init; }
    public string? SampleId { get; init; }
    public ulong? StateKey { get; init; }
    public int? EntryIndex { get; init; }
    public uint? CommandId { get; init; }
    public ulong? EquipmentInstanceId { get; init; }
    public CoverageFailure? Failure { get; init; }

    internal CoverageVerificationException At(string domain, CoverageDenseRow row, int? entry = null,
        uint? command = null, ulong? equipment = null) => new(Reason, Message)
        {
            DomainId = domain,
            DenseIndex = row.DenseIndex,
            SampleId = row.Sample.Id,
            StateKey = row.ResolvedStateKey,
            EntryIndex = entry ?? EntryIndex,
            CommandId = command ?? CommandId,
            EquipmentInstanceId = equipment ?? EquipmentInstanceId,
            Failure = Failure,
        };
}
