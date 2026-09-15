using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// Thrown when a pack STORE is asked to do something no content-addressed store may do: write bytes under a
/// name they do not digest to, or write under a name that is not a content address at all.
/// <para>
/// It THROWS rather than returning a reason, and that is the one place in this package where a pack failure
/// does. Everything on the READ side is total, because those bytes came from a remote peer and a decoder
/// handed remote bytes must be a function. A bad PUT is the publisher's own programming error, on the
/// publisher's own machine, with the correct bytes still in memory on the line above, so it belongs on that
/// line rather than in a report nobody reads.
/// </para>
/// </summary>
public sealed class ContentPackException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public ContentPackException()
    {
    }

    /// <summary>Creates the exception, naming what was refused.</summary>
    public ContentPackException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ContentPackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception carrying the address it was asked to write under and why that failed.</summary>
    /// <param name="message">The human-readable refusal.</param>
    /// <param name="hash">The content address the caller supplied.</param>
    /// <param name="reason">The stable reason token, from the fetch half of the fixed list.</param>
    public ContentPackException(string message, string? hash, string? reason)
        : base(message)
    {
        Hash = hash;
        Reason = reason;
    }

    /// <summary>The content address the caller supplied, or null.</summary>
    public string? Hash { get; }

    /// <summary>The stable reason token, or null. An operator's log line is built from this and not the message.</summary>
    public string? Reason { get; }
}
