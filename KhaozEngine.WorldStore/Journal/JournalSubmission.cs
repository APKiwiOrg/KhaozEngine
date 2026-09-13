using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

public enum JournalSubmissionStatus
{
    Accepted,
    StreamBusy,
    Backpressure,
    Stopping,

    /// <summary>
    /// A stream's expected version does not match the executor's admitted head version for it. The plan was built
    /// against a view the queue has already moved past.
    /// </summary>
    VersionConflict,
}

public sealed class JournalSubmission
{
    private static readonly ReadOnlyCollection<JournalAdmittedStreamHead> NoHeads = Array.AsReadOnly(Array.Empty<JournalAdmittedStreamHead>());
    private static readonly ReadOnlyCollection<JournalProjectionSectionKey> NoSections = Array.AsReadOnly(Array.Empty<JournalProjectionSectionKey>());
    private readonly ReadOnlyCollection<JournalAdmittedStreamHead> admittedStreams;
    private readonly ReadOnlyCollection<JournalProjectionSectionKey> changedSections;

    internal JournalSubmission(
        JournalSubmissionStatus status,
        Guid operationId,
        int admittedByteCount,
        JournalAdmittedStreamHead[]? admittedStreams = null,
        JournalProjectionSectionKey[]? changedSections = null)
    {
        Status = status;
        OperationId = operationId;
        AdmittedByteCount = admittedByteCount;
        this.admittedStreams = admittedStreams is null ? NoHeads : Array.AsReadOnly(admittedStreams);
        this.changedSections = changedSections is null ? NoSections : Array.AsReadOnly(changedSections);
    }

    public JournalSubmissionStatus Status { get; }
    public Guid OperationId { get; }
    public int AdmittedByteCount { get; }
    public bool IsAccepted => Status == JournalSubmissionStatus.Accepted;

    /// <summary>
    /// The version each touched stream now stands at in the admitted view, ordinal ascending by stream key. Empty
    /// unless the operation was accepted.
    /// </summary>
    public IReadOnlyList<JournalAdmittedStreamHead> AdmittedStreams => admittedStreams;

    /// <summary>
    /// Every projection section this operation replaced in the admitted view, ordinal ascending by stream then
    /// section. Empty unless the operation was accepted.
    /// </summary>
    public IReadOnlyList<JournalProjectionSectionKey> ChangedSections => changedSections;
}
