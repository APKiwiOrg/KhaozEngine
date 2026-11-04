using System;
using System.Collections.Generic;

namespace KhaozEngine.Content;

/// <summary>Result of validating a JSON instance against a schema. The positional constructor and
/// <see cref="Errors"/> retain the existing developer-facing strings.</summary>
public sealed record ValidationReport(bool IsValid, IReadOnlyList<string> Errors)
{
    IReadOnlyList<JsonSchemaFinding> _findings = Array.Empty<JsonSchemaFinding>();

    /// <summary>Structured schema evaluation findings, empty when validation passes or the schema cannot be
    /// parsed. Setting this property copies the collection into an owned read-only snapshot.</summary>
    public IReadOnlyList<JsonSchemaFinding> Findings
    {
        get => _findings;
        init => _findings = Array.AsReadOnly<JsonSchemaFinding>([.. value]);
    }
}
