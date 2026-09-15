using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// Thrown by <see cref="ContentTypeRegistry.RegisterContentType"/> when a registration breaks one of the
/// rules of contracts 4.2 to 4.5 and 4.7: a reserved type id, a band the caller is not entitled to, a
/// duplicate id or key, an illegal chunk slot count or row cap, a chunk bound no reader could load, or a
/// codec whose written fields differ from its schema.
/// <para>
/// It THROWS rather than returning a result, deliberately: registration runs once at process start, before
/// any pack loads, so the failure belongs on the line that caused it rather than in a report nobody reads.
/// </para>
/// </summary>
public sealed class ContentRegistrationException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public ContentRegistrationException()
    {
    }

    /// <summary>Creates the exception, naming the rule that was broken and both sides of it.</summary>
    public ContentRegistrationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ContentRegistrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
