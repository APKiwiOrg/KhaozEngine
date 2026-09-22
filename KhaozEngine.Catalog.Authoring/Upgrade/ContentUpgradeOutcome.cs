using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// How a run ended. Four of these are a SUCCESS and the rest are a refusal the host stops on, which
/// <see cref="ContentUpgradeReport.Success"/> is the single answer to.
/// </summary>
public enum ContentUpgradeOutcome
{
    /// <summary>Every shipped definition is already in the ledger. NOTHING was written.</summary>
    UpToDate,

    /// <summary>At least one definition was applied, each as its own published version.</summary>
    Applied,

    /// <summary>A preview: the pending definitions were planned and reported, and nothing was written.</summary>
    PreviewOnly,

    /// <summary>
    /// The catalog has no active version. The runner NEVER seeds, so this is the caller's signal to import
    /// its bundle and then call <see cref="ContentUpgradeRunner.RecordBaselineAsync"/>. It is a success
    /// because a fresh install is not a failure, and it is a distinct outcome because a caller that does not
    /// seed has to be able to tell it from <see cref="UpToDate"/>.
    /// </summary>
    NoCatalog,

    /// <summary>The store implements no <see cref="IContentUpgradeLedger"/>, so no history can be kept.</summary>
    Unsupported,

    /// <summary>The ledger holds an upgrade id this build does not ship, so the catalog is ahead of it.</summary>
    CatalogAheadOfBuild,

    /// <summary>An open draft the runner cannot prove is its own is operator work, and it was left untouched.</summary>
    OperatorDraftOpen,

    /// <summary>The operator pin names a version other than the active one, so the baseline is not in force.</summary>
    PinnedElsewhere,

    /// <summary>The supplied expected version is not the active version, so the catalog moved.</summary>
    BaselineMoved,

    /// <summary>A planner refused this catalog. Earlier definitions stay applied and later ones stay pending.</summary>
    Refused,

    /// <summary>A publish failed and the ledger says the upgrade did not land. The prior version is in force.</summary>
    Failed,
}

/// <summary>
/// The outcomes as the tokens a report renders and a supervisor script matches on, so a log line reads the
/// same everywhere and never depends on an enum member's spelling.
/// </summary>
public static class ContentUpgradeOutcomes
{
    /// <summary>
    /// Whether an outcome is a SUCCESS. The four are up to date, applied, preview only and no catalog, and
    /// the last one is a success because a fresh install has nothing to upgrade and the caller seeds next.
    /// </summary>
    /// <param name="outcome">The outcome.</param>
    public static bool IsSuccess(ContentUpgradeOutcome outcome) => outcome is ContentUpgradeOutcome.UpToDate
        or ContentUpgradeOutcome.Applied
        or ContentUpgradeOutcome.PreviewOnly
        or ContentUpgradeOutcome.NoCatalog;

    /// <summary>The token one outcome renders as.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is not one of the eleven.</exception>
    public static string Token(ContentUpgradeOutcome outcome) => outcome switch
    {
        ContentUpgradeOutcome.UpToDate => "up-to-date",
        ContentUpgradeOutcome.Applied => "applied",
        ContentUpgradeOutcome.PreviewOnly => "preview-only",
        ContentUpgradeOutcome.NoCatalog => "no-catalog",
        ContentUpgradeOutcome.Unsupported => "unsupported",
        ContentUpgradeOutcome.CatalogAheadOfBuild => "catalog-ahead-of-build",
        ContentUpgradeOutcome.OperatorDraftOpen => "operator-draft-open",
        ContentUpgradeOutcome.PinnedElsewhere => "pinned-elsewhere",
        ContentUpgradeOutcome.BaselineMoved => "baseline-moved",
        ContentUpgradeOutcome.Refused => "refused",
        ContentUpgradeOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown upgrade outcome."),
    };
}
