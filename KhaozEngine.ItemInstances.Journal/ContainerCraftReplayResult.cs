using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>The evidence-checked resolution of a pre-upgrade client craft.</summary>
public enum ContainerCraftReplayStatus
{
    /// <summary>No receipt exists under the requested operation id.</summary>
    NotFound,

    /// <summary>The legacy fingerprint and retained authored plan evidence both match.</summary>
    Replayed,

    /// <summary>The legacy request fields or retained authored plan disagree.</summary>
    OperationConflict,

    /// <summary>The receipt exists, but its retained event cannot prove the authored plan.</summary>
    EvidenceUnavailable,
}

/// <summary>Only a proven legacy replay exposes its original receipt.</summary>
public sealed class ContainerCraftReplayResult
{
    internal ContainerCraftReplayResult(ContainerCraftReplayStatus status, JournalCommitReceipt? receipt = null)
    {
        Status = status;
        Receipt = receipt;
    }

    /// <summary>The resolution, including an explicit missing-evidence answer.</summary>
    public ContainerCraftReplayStatus Status { get; }

    /// <summary>The original replay receipt, present only when the authored plan was proven.</summary>
    public JournalCommitReceipt? Receipt { get; }
}
