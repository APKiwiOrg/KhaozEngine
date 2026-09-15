using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The ONE exception type this package throws (spec 2.3), carrying the offending content type, the
/// definition id and a stable reason token beside the human-readable message.
/// <para>
/// It THROWS where the read side returns a reason, and the split is deliberate. A decoder is handed bytes
/// from a remote peer, so it must be total. An authoring refusal happens on the publisher's own machine,
/// against a store the publisher opened, with the caller's own arguments still on the line above, so it
/// belongs on that line. The API boundary of spec 10 turns the reason into a 400 or a 409 with a finding.
/// </para>
/// <para>
/// <see cref="Reason"/> is what an operator's log line and a counter key on, never the message, the same
/// rule the pack decoders' reason tokens follow.
/// </para>
/// </summary>
public sealed class ContentAuthoringException : Exception
{
    /// <summary>
    /// A second edit named a target the open draft already holds under a DIFFERENT operation (spec 4.4). A
    /// draft holds one pending intent per row, so the second edit is refused rather than silently flipping
    /// the first one's operation and dropping its fields.
    /// </summary>
    public const string EditTargetCollisionReason = "edit-target-collision";

    /// <summary>Creates the exception with no message.</summary>
    public ContentAuthoringException()
    {
    }

    /// <summary>Creates the exception, naming what was refused.</summary>
    public ContentAuthoringException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ContentAuthoringException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception carrying the offending row and the reason it was refused.</summary>
    /// <param name="message">The human-readable refusal, naming both sides of the rule that was broken.</param>
    /// <param name="type">The content type the refusal is about, or the default when it is about none.</param>
    /// <param name="id">The definition id, or 0 when the refusal is about the store as a whole.</param>
    /// <param name="reason">The stable reason token an operator's log line is built from.</param>
    public ContentAuthoringException(string message, ContentTypeId type, int id, string? reason)
        : base(message)
    {
        Type = type;
        Id = id;
        Reason = reason;
    }

    /// <summary>The content type the refusal is about. The default type id 0 means none.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The definition id the refusal is about, or 0 when it is about the store as a whole.</summary>
    public int Id { get; }

    /// <summary>The stable reason token, or null. A log line and a counter key on this, not on the message.</summary>
    public string? Reason { get; }
}
