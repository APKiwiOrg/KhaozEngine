using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// ONE thing a run has to say, under a stable code an operator or a supervisor script keys on rather than
/// parsing the message.
/// <para>
/// The codes follow the catalog validator's own convention, which is a fixed alphabetic prefix and a
/// four-digit number. The validator's findings are <c>KEC</c> and these are <c>KECU</c>, so the two never
/// collide and a grep for either one is exact. There is no game prefix anywhere: the engine owns the
/// orchestration and a game owns only its definitions.
/// </para>
/// </summary>
/// <param name="Code">The stable code, one of <see cref="ContentUpgradeCodes"/>.</param>
/// <param name="Message">The line an operator reads, naming the catalog state and the next action.</param>
public sealed record ContentUpgradeDiagnostic(string Code, string Message)
{
    /// <summary>The stable code, checked here because a diagnostic with no code is one nothing can key on.</summary>
    /// <exception cref="ArgumentException">The code is empty.</exception>
    public string Code { get; init; } = RequireCode(Code);

    /// <summary>The line an operator reads.</summary>
    /// <exception cref="ArgumentNullException">The message is null.</exception>
    public string Message { get; init; } = Message ?? throw new ArgumentNullException(nameof(Message));

    /// <summary>The code and the message as one line, which is what the report renders after the prefix.</summary>
    public override string ToString() => Code + " " + Message;

    static string RequireCode(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code, nameof(Code));
        return code;
    }
}

/// <summary>
/// Every diagnostic code the runner emits. They are constants rather than an enum because a host logs the
/// STRING and a log an operator greps has to keep reading the same after a value is inserted in the middle.
/// </summary>
public static class ContentUpgradeCodes
{
    /// <summary>The store keeps no upgrade ledger, so no run can know what a catalog already holds.</summary>
    public const string LedgerUnsupported = "KECU0001";

    /// <summary>The catalog has no active version. Seed it and record the baseline.</summary>
    public const string NoCatalog = "KECU0002";

    /// <summary>The ledger holds an upgrade id this build does not ship.</summary>
    public const string CatalogAheadOfBuild = "KECU0003";

    /// <summary>An open draft the runner cannot prove is its own, left untouched.</summary>
    public const string OperatorDraftOpen = "KECU0004";

    /// <summary>The pin names a version other than the active one.</summary>
    public const string PinnedElsewhere = "KECU0005";

    /// <summary>The pin is on the active version. It was not moved, and the line names the version to repin to.</summary>
    public const string PinHeld = "KECU0006";

    /// <summary>The supplied expected version is not the active version.</summary>
    public const string BaselineMoved = "KECU0007";

    /// <summary>A planner refused this catalog.</summary>
    public const string PlanRefused = "KECU0008";

    /// <summary>A publish failed and the ledger confirms the upgrade did not land.</summary>
    public const string PublishFailed = "KECU0009";

    /// <summary>An interrupted run's own draft was found and discarded before planning.</summary>
    public const string DraftRecovered = "KECU0010";

    /// <summary>A publish failed and the ledger shows another runner applied the same upgrade. The run continued.</summary>
    public const string AppliedConcurrently = "KECU0011";

    /// <summary>Every shipped definition is already in the ledger and nothing was written.</summary>
    public const string UpToDate = "KECU0012";

    /// <summary>A preview wrote nothing. The line names the command that applies it.</summary>
    public const string PreviewOnly = "KECU0013";

    /// <summary>
    /// INFORMATIONAL. A shipped definition's order differs from the order the ledger recorded it under. The
    /// id is the identity, so the catalog holds the upgrade and it will not run again.
    /// </summary>
    public const string UpgradeOrderMoved = "KECU0014";

    /// <summary>
    /// INFORMATIONAL. A pending definition is ordered below one the catalog already holds, which is what two
    /// feature branches merging produces, or below one this run publishes first out of a recovered draft. It
    /// runs now, against the catalog as it stands.
    /// </summary>
    public const string PendingBelowApplied = "KECU0015";

    /// <summary>
    /// INFORMATIONAL. A draft stood between this run and its publish, every edit in it came from a plan this
    /// run computed, so it was cleared and the upgrade was tried again. It is what a write that merged with
    /// a second runner's leaves behind, and no operator work is ever cleared under it.
    /// </summary>
    public const string DraftCleared = "KECU0016";
}
