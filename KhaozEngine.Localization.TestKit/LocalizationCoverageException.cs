using System;
using System.Collections.Generic;

namespace KhaozEngine.Localization.TestKit;

/// <summary>
/// Thrown by <see cref="LocalizationCoverage"/> when a localization gap is found: a key absent from the neutral
/// resx or a shipped satellite, or a placeholder-index mismatch between the neutral template and a translation.
/// <see cref="Reason"/> names the rule and <see cref="Findings"/> lists every gap found, so one failing test reports
/// them all. The message is developer prose that lists the same gaps, never a contract. Framework-agnostic - an
/// uncaught throw inside an xUnit <c>[Fact]</c> (or any test framework) fails that test with this message.
/// </summary>
public sealed class LocalizationCoverageException : Exception
{
    private readonly IReadOnlyList<LocalizationCoverageFinding>? findings;

    /// <summary>Creates the exception with an aggregated, human-readable description of the gaps and no
    /// <see cref="Reason"/>.</summary>
    public LocalizationCoverageException(string message) : base(message) { }

    /// <summary>Creates the exception with its reason.</summary>
    /// <param name="reason">The rule that refused the coverage check. For a coverage failure, the first finding's
    /// reason.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public LocalizationCoverageException(LocalizationCoverageRefusal reason, string message) : base(message)
        => Reason = reason;

    /// <summary>The rule that refused the check. For a coverage failure, the first finding's reason. Null only for
    /// an instance built through the message-only constructor by code outside the engine.</summary>
    public LocalizationCoverageRefusal? Reason { get; }

    /// <summary>The keys class whose key universe was checked or found empty. Null for the
    /// <see cref="System.Resources.ResourceManager"/> and key-sequence universes.</summary>
    public Type? KeysType { get; init; }

    /// <summary>The <see cref="System.Resources.ResourceManager.BaseName"/> of the resources checked, for a coverage
    /// failure, for <see cref="LocalizationCoverageRefusal.NeutralResourcesUnavailable"/> and for a neutral resx
    /// with no string entries.</summary>
    public string? BaseName { get; init; }

    /// <summary>The number of distinct keys checked, for a coverage failure. A
    /// <see cref="LocalizationCoverageRefusal.SatelliteResourcesUnavailable"/> finding leaves all of them
    /// unresolved.</summary>
    public int? KeyCount { get; init; }

    /// <summary>Every gap in the order the message lists them, for a coverage failure, or null for a refusal
    /// raised before any key is checked (an empty key universe, or an unloadable neutral set when
    /// <see cref="LocalizationCoverage.NeutralKeys"/> reads the universe). Setting this property takes an owned,
    /// read-only snapshot, so changing the input collection or casting the exposed list cannot mutate it.</summary>
    public IReadOnlyList<LocalizationCoverageFinding>? Findings
    {
        get => findings;
        init => findings = value is null ? null : Array.AsReadOnly<LocalizationCoverageFinding>([.. value]);
    }
}
