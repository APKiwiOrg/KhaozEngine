namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>The closed set of producer operations and readiness gates in the catalog test harness.</summary>
internal enum CatalogPhase
{
    /// <summary>The runner start phase.</summary>
    RunnerStart,
    /// <summary>The read open draft phase.</summary>
    ReadOpenDraft,
    /// <summary>The freeze runner draft phase.</summary>
    FreezeRunnerDraft,
    /// <summary>The read active version phase.</summary>
    ReadActiveVersion,
    /// <summary>The read upgrade ledger phase.</summary>
    ReadUpgradeLedger,
    /// <summary>The read operator pin phase.</summary>
    ReadOperatorPin,
    /// <summary>The export baseline phase.</summary>
    ExportBaseline,
    /// <summary>The read baseline version phase.</summary>
    ReadBaselineVersion,
    /// <summary>The list sweep versions phase.</summary>
    ListSweepVersions,
    /// <summary>The write runner draft phase.</summary>
    WriteRunnerDraft,
    /// <summary>The publish runner draft phase.</summary>
    PublishRunnerDraft,
    /// <summary>The discard runner draft phase.</summary>
    DiscardRunnerDraft,
    /// <summary>The release runner freeze phase.</summary>
    ReleaseRunnerFreeze,
    /// <summary>The release scoped runner freeze phase.</summary>
    ReleaseScopedRunnerFreeze,
    /// <summary>The filesystem pack exists phase.</summary>
    FileSystemPackExists,
    /// <summary>The filesystem pack read phase.</summary>
    FileSystemPackRead,
    /// <summary>The filesystem pack write phase.</summary>
    FileSystemPackWrite,
    /// <summary>The filesystem version listing phase.</summary>
    FileSystemVersionListing,
    /// <summary>The filesystem pointer write phase.</summary>
    FileSystemPointerWrite,
    /// <summary>The filesystem pointer read phase.</summary>
    FileSystemPointerRead,
    /// <summary>The filesystem prune delete phase.</summary>
    FileSystemPruneDelete,
    /// <summary>The filesystem pruning enumeration entry phase.</summary>
    FileSystemPruningEnumerationEntry,
    /// <summary>The filesystem pruning enumeration phase.</summary>
    FileSystemPruningEnumeration,
    /// <summary>The release scoped publish freeze phase.</summary>
    ReleaseScopedPublishFreeze,
    /// <summary>The complete publish freeze phase.</summary>
    CompletePublishFreeze,
    /// <summary>The read baseline text phase.</summary>
    ReadBaselineText,
    /// <summary>The commit text publish phase.</summary>
    CommitTextPublish,
    /// <summary>The older sweep waiting for next complete freeze phase.</summary>
    OlderSweepWaitingForNextFreeze,
    /// <summary>The older sweep after next complete freeze phase.</summary>
    OlderSweepAfterNextFreeze,
    /// <summary>The next complete freeze waiting for old commit cleanup phase.</summary>
    NextFreezeWaitingForOldCleanup,
    /// <summary>The next publish after old commit cleanup phase.</summary>
    NextPublishAfterOldCleanup,
    /// <summary>The old commit cleanup snapshot phase.</summary>
    OldCommitCleanupSnapshot,
    /// <summary>The older runner waiting for next report phase.</summary>
    OlderRunnerWaitingForNextReport,
    /// <summary>The older runner teardown hold phase.</summary>
    OlderRunnerTeardownHold,
    /// <summary>The first commit phase.</summary>
    FirstCommit,
    /// <summary>The next complete freeze phase.</summary>
    NextCompleteFreeze,
    /// <summary>The older sweep wait phase.</summary>
    OlderSweepWait,
    /// <summary>The withheld older readiness phase.</summary>
    WithheldOlderReadiness,
    /// <summary>A publish interruption point, identified further by ContentPublishStep.</summary>
    Publish,
}
