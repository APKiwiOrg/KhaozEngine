using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// Thrown at boot step 7b when a registered <see cref="IContentLoadIndex"/> fails, or when one is asked for
/// before the step that builds them has finished.
/// <para>
/// It THROWS rather than returning a reason, and that is the rule spec 9.4 states for an index: a type whose
/// index cannot be built FAILS THE BOOT CLOSED rather than leaving a partial index behind, because a partial
/// index answers plausible wrong numbers on a tick and a refused boot does not. The exit path is spec 9.6's,
/// exit code 3 with the type named, which is what <see cref="Type"/> and <see cref="TypeKey"/> are carried
/// for: the operator's line names the type rather than the stack.
/// </para>
/// <para>
/// The index is untrusted code the way a per-type validator is, so the ORIGINAL failure is kept as the inner
/// exception rather than flattened into a message.
/// </para>
/// </summary>
public sealed class ContentLoadIndexException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public ContentLoadIndexException()
    {
    }

    /// <summary>Creates the exception, naming what failed.</summary>
    public ContentLoadIndexException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ContentLoadIndexException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception carrying the type whose index failed.</summary>
    /// <param name="message">The human-readable refusal, which the boot's stderr line is built from.</param>
    /// <param name="type">The content type the index was registered against.</param>
    /// <param name="typeKey">That type's stable key, or empty when the failure names no one type.</param>
    /// <param name="innerException">The index's own failure, kept rather than flattened.</param>
    public ContentLoadIndexException(string message, ContentTypeId type, string typeKey, Exception? innerException)
        : base(message, innerException)
    {
        Type = type;
        TypeKey = typeKey;
    }

    /// <summary>The content type whose index failed, or type 0 when the failure names no one type.</summary>
    public ContentTypeId Type { get; }

    /// <summary>That type's stable key, which is what an operator reads.</summary>
    public string TypeKey { get; } = string.Empty;
}
