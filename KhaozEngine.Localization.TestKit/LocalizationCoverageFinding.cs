using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.Localization.TestKit;

/// <summary>One gap a <see cref="LocalizationCoverage"/> check found. Identifiers the refused rule does not know
/// remain null. Every collection identifier is an owned, read-only snapshot taken when it is set.</summary>
/// <param name="Reason">The rule the gap broke.</param>
public sealed record LocalizationCoverageFinding(LocalizationCoverageRefusal Reason)
{
    private readonly IReadOnlyList<string>? missingKeys;
    private readonly IReadOnlyList<int>? expectedIndices;
    private readonly IReadOnlyList<int>? actualIndices;

    /// <summary>The satellite culture name as supplied, for <see cref="LocalizationCoverageRefusal.CultureNameInvalid"/>,
    /// <see cref="LocalizationCoverageRefusal.SatelliteResourcesUnavailable"/>,
    /// <see cref="LocalizationCoverageRefusal.TranslationsMissing"/> and
    /// <see cref="LocalizationCoverageRefusal.PlaceholderSetMismatch"/>.</summary>
    public string? CultureName { get; init; }

    /// <summary>The keys the resource set does not hold, in key universe order, for
    /// <see cref="LocalizationCoverageRefusal.NeutralKeysMissing"/> and
    /// <see cref="LocalizationCoverageRefusal.TranslationsMissing"/>.</summary>
    public IReadOnlyList<string>? MissingKeys
    {
        get => missingKeys;
        init => missingKeys = value is null ? null : ImmutableArray.CreateRange(value);
    }

    /// <summary>The key whose translation was refused, for
    /// <see cref="LocalizationCoverageRefusal.PlaceholderSetMismatch"/>.</summary>
    public string? Key { get; init; }

    /// <summary>The neutral template's placeholder indices in ascending order, for
    /// <see cref="LocalizationCoverageRefusal.PlaceholderSetMismatch"/>.</summary>
    public IReadOnlyList<int>? ExpectedIndices
    {
        get => expectedIndices;
        init => expectedIndices = value is null ? null : ImmutableArray.CreateRange(value);
    }

    /// <summary>The translation's placeholder indices in ascending order, for
    /// <see cref="LocalizationCoverageRefusal.PlaceholderSetMismatch"/>.</summary>
    public IReadOnlyList<int>? ActualIndices
    {
        get => actualIndices;
        init => actualIndices = value is null ? null : ImmutableArray.CreateRange(value);
    }
}
