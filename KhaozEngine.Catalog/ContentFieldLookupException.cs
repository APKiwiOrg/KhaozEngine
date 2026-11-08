using System;

namespace KhaozEngine.Catalog;

/// <summary>A required field lookup refused by the runtime's registry or schema.
/// Existing <see cref="InvalidOperationException"/> catches remain valid.</summary>
public sealed class ContentFieldLookupException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and developer-facing explanation.</summary>
    public ContentFieldLookupException(ContentFieldLookupRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the lookup.</summary>
    public ContentFieldLookupRefusal Reason { get; }

    /// <summary>The requested type id.</summary>
    public ContentTypeId? Type { get; init; }

    /// <summary>The registered type key, only for <see cref="ContentFieldLookupRefusal.FieldUndeclared"/>.</summary>
    public string? TypeKey { get; init; }

    /// <summary>The requested field name.</summary>
    public string? FieldName { get; init; }
}
