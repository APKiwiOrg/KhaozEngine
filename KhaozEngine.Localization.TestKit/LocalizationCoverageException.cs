using System;

namespace KhaozEngine.Localization.TestKit;

/// <summary>
/// Thrown by <see cref="LocalizationCoverage"/> when a localization gap is found: a key absent from the neutral
/// resx or a shipped satellite, or a placeholder-index mismatch between the neutral template and a translation.
/// The message aggregates every gap found so one failing test lists them all. Framework-agnostic - an uncaught
/// throw inside an xUnit <c>[Fact]</c> (or any test framework) fails that test with this message.
/// </summary>
public sealed class LocalizationCoverageException : Exception
{
    /// <summary>Creates the exception with an aggregated, human-readable description of the gaps.</summary>
    public LocalizationCoverageException(string message) : base(message) { }
}
