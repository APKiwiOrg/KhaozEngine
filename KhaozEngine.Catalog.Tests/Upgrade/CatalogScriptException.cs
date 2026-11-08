using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>A test-only script refusal that remains catchable as InvalidOperationException.</summary>
internal sealed class CatalogScriptException(CatalogScriptRefusal reason, string message)
    : InvalidOperationException(message)
{
    IReadOnlyList<string>? _reportLines;

    /// <summary>The script rule that refused the operation.</summary>
    public CatalogScriptRefusal Reason { get; } = reason;
    /// <summary>The zero-based freeze slot requested, including the first missing slot.</summary>
    public int? ActualIndex { get; init; }
    /// <summary>The base version expected at the mismatching freeze slot.</summary>
    public int? ExpectedBaseVersion { get; init; }
    /// <summary>The base version passed to the mismatching freeze slot.</summary>
    public int? ActualBaseVersion { get; init; }
    /// <summary>The number of allowed freeze calls, when the script is exhausted.</summary>
    public int? MaximumCount { get; init; }
    /// <summary>The number of allowed draft observations.</summary>
    public int? MaximumReads { get; init; }
    /// <summary>The draft observation that exceeded the bound.</summary>
    public int? ActualReads { get; init; }
    /// <summary>The readiness gate the completed producer did not signal.</summary>
    public CatalogPhase? Phase { get; init; }
    /// <summary>An owned read-only snapshot of the ended run's diagnostic prose. Wording is not a contract.</summary>
    public IReadOnlyList<string>? ReportLines
    {
        get => _reportLines;
        init => _reportLines = value is null ? null : ImmutableArray.Create(value.ToArray());
    }
}
