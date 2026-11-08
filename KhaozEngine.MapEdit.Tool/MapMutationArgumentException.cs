using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

/// <summary>A mutation argument refusal preserving its original ArgumentException base and parameter name.</summary>
public sealed class MapMutationArgumentException : ArgumentException
{
    IReadOnlyList<string>? _acceptedValues;
    public MapMutationArgumentException(MapDocumentRefusal reason, string message, string? paramName = null)
        : base(message, paramName) => Reason = reason;
    public MapMutationArgumentException(MapDocumentRefusal reason, string message, Exception inner, string? paramName = null)
        : base(message, paramName, inner) => Reason = reason;
    /// <summary>The mutation argument rule that refused the request.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The refused tool verb.</summary>
    public string? Operation { get; init; }
    /// <summary>The field whose input was refused.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The submitted text before parsing.</summary>
    public string? SubmittedText { get; init; }
    /// <summary>The enumeration whose named values the parser accepts, when one exists.</summary>
    public Type? EnumType { get; init; }
    /// <summary>The input entry or rule index, when known.</summary>
    public int? Index { get; init; }
    /// <summary>The scatter layer whose rule was refused.</summary>
    public string? LayerId { get; init; }
    /// <summary>The accepted named inputs, copied as an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? AcceptedValues
    {
        get => _acceptedValues;
        init => _acceptedValues = value is null ? null : ImmutableArray.Create(value.ToArray());
    }
}
