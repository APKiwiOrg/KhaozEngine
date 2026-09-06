using System;

namespace KhaozEngine.WorldStore.Journal;

public enum JournalSubmissionStatus
{
    Accepted,
    StreamBusy,
    Backpressure,
    Stopping,
}

public sealed class JournalSubmission
{
    internal JournalSubmission(JournalSubmissionStatus status, Guid operationId, int admittedByteCount)
    {
        Status = status;
        OperationId = operationId;
        AdmittedByteCount = admittedByteCount;
    }

    public JournalSubmissionStatus Status { get; }
    public Guid OperationId { get; }
    public int AdmittedByteCount { get; }
    public bool IsAccepted => Status == JournalSubmissionStatus.Accepted;
}
