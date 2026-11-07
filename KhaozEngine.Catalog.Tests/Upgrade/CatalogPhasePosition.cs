using System;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>A phase's typed identity and its existing diagnostic rendering.</summary>
internal readonly record struct CatalogPhasePosition(CatalogPhase Phase,
    ContentPublishStep? PublishStep = null, bool IsRunnerProgress = false)
{
    public override string ToString()
    {
        string name = Phase switch
        {
            CatalogPhase.RunnerStart => "runner start",
            CatalogPhase.ReadOpenDraft => "read open draft",
            CatalogPhase.FreezeRunnerDraft => "freeze runner draft",
            CatalogPhase.ReadActiveVersion => "read active version",
            CatalogPhase.ReadUpgradeLedger => "read upgrade ledger",
            CatalogPhase.ReadOperatorPin => "read operator pin",
            CatalogPhase.ExportBaseline => "export baseline",
            CatalogPhase.ReadBaselineVersion => "read baseline version",
            CatalogPhase.ListSweepVersions => "list sweep versions",
            CatalogPhase.WriteRunnerDraft => "write runner draft",
            CatalogPhase.PublishRunnerDraft => "publish runner draft",
            CatalogPhase.DiscardRunnerDraft => "discard runner draft",
            CatalogPhase.ReleaseRunnerFreeze => "release runner freeze",
            CatalogPhase.ReleaseScopedRunnerFreeze => "release scoped runner freeze",
            CatalogPhase.FileSystemPackExists => "filesystem pack exists",
            CatalogPhase.FileSystemPackRead => "filesystem pack read",
            CatalogPhase.FileSystemPackWrite => "filesystem pack write",
            CatalogPhase.FileSystemVersionListing => "filesystem version listing",
            CatalogPhase.FileSystemPointerWrite => "filesystem pointer write",
            CatalogPhase.FileSystemPointerRead => "filesystem pointer read",
            CatalogPhase.FileSystemPruneDelete => "filesystem prune delete",
            CatalogPhase.FileSystemPruningEnumerationEntry => "filesystem pruning enumeration entry",
            CatalogPhase.FileSystemPruningEnumeration => "filesystem pruning enumeration",
            CatalogPhase.ReleaseScopedPublishFreeze => "release scoped publish freeze",
            CatalogPhase.CompletePublishFreeze => "complete publish freeze",
            CatalogPhase.ReadBaselineText => "read baseline text",
            CatalogPhase.CommitTextPublish => "commit text publish",
            CatalogPhase.OlderSweepWaitingForNextFreeze => "older sweep waiting for next complete freeze",
            CatalogPhase.OlderSweepAfterNextFreeze => "older sweep after next complete freeze",
            CatalogPhase.NextFreezeWaitingForOldCleanup => "next complete freeze waiting for old commit cleanup",
            CatalogPhase.NextPublishAfterOldCleanup => "next publish after old commit cleanup",
            CatalogPhase.OldCommitCleanupSnapshot => "old commit cleanup snapshot",
            CatalogPhase.OlderRunnerWaitingForNextReport => "older runner waiting for next report",
            CatalogPhase.OlderRunnerTeardownHold => "older runner teardown hold",
            CatalogPhase.FirstCommit => "first commit",
            CatalogPhase.NextCompleteFreeze => "next complete freeze",
            CatalogPhase.OlderSweepWait => "older sweep wait",
            CatalogPhase.WithheldOlderReadiness => "withheld older readiness",
            CatalogPhase.Publish => "publish " + PublishStep,
            // Only named phases enter the harness. An undefined enum value is a plain parameter error.
            _ => throw new ArgumentOutOfRangeException(nameof(Phase), Phase, "The catalog harness phase is not defined."),
        };
        return IsRunnerProgress ? "runner progress after " + name : name;
    }
}
