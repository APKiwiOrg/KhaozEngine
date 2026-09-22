using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>Whether a run writes anything, which is the only mode distinction there is.</summary>
public enum ContentUpgradeMode
{
    /// <summary>Plan and report, writing NOTHING: no draft, no version, no audit row and no ledger row.</summary>
    Preview,

    /// <summary>Apply every pending definition in order, one published version each.</summary>
    Apply,
}

/// <summary>
/// What a caller asks a run for: the mode, an optional expected version, who is running it, and the build
/// ordinals the published versions carry as their minimums.
/// <para>
/// <b><see cref="ExpectedVersion"/> is optional on purpose and the two deployment arms want opposite
/// answers.</b> A local arm upgrades whatever it finds, so it supplies none. A hosted deploy step names the
/// version it previewed, and a catalog that moved underneath it is <c>BaselineMoved</c> with nothing changed
/// rather than an upgrade applied to a baseline nobody reviewed.
/// </para>
/// <para>
/// <b>The builds RISE and never fall.</b> Each published version takes the larger of the baseline version's
/// minimum and the ordinal here, so a run by an older build than the catalog was published under cannot
/// lower the bar a client is admitted over.
/// </para>
/// </summary>
/// <param name="Mode">Preview or Apply.</param>
/// <param name="Actor">What the engine authenticated, 1 to 128 characters. It is also what proves an interrupted run's draft is the runner's own.</param>
/// <param name="Operator">The identity the console forwarded, empty when it forwarded none.</param>
/// <param name="ServerBuild">This build's server ordinal, which every published version's minimum rises to.</param>
/// <param name="ClientBuild">This build's client ordinal, which every published version's minimum rises to.</param>
public sealed record ContentUpgradeOptions(
    ContentUpgradeMode Mode,
    string Actor,
    string Operator,
    int ServerBuild,
    int ClientBuild)
{
    /// <summary>The longest actor the audit and ledger columns take.</summary>
    public const int MaxActorLength = 128;

    /// <summary>What the engine authenticated, checked here so a <c>with</c> is checked the same way.</summary>
    /// <exception cref="ArgumentException">The actor is empty or longer than 128 characters.</exception>
    public string Actor { get; init; } = RequireActor(Actor);

    /// <summary>The identity the console forwarded, empty when it forwarded none.</summary>
    /// <exception cref="ArgumentNullException">The operator is null.</exception>
    public string Operator { get; init; } = Operator ?? throw new ArgumentNullException(nameof(Operator));

    /// <summary>This build's server ordinal.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is negative.</exception>
    public int ServerBuild { get; init; } = RequireOrdinal(ServerBuild, nameof(ServerBuild));

    /// <summary>This build's client ordinal.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is negative.</exception>
    public int ClientBuild { get; init; } = RequireOrdinal(ClientBuild, nameof(ClientBuild));

    /// <summary>
    /// The version the caller believes the catalog stands at, or null to upgrade whatever is found. A
    /// mismatch is <see cref="ContentUpgradeOutcome.BaselineMoved"/> with nothing changed.
    /// </summary>
    public int? ExpectedVersion { get; init; }

    static string RequireActor(string actor)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor, nameof(Actor));
        return actor.Length <= MaxActorLength
            ? actor
            : throw new ArgumentException(
                FormattableString.Invariant(
                    $"An actor is at most {MaxActorLength} characters and this one is {actor.Length}."),
                nameof(Actor));
    }

    static int RequireOrdinal(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }
}
