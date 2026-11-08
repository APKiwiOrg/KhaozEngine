using System;

namespace KhaozEngine;

/// <summary>
/// The base of every engine refusal for malformed input data whose BCL type would have been the sealed
/// <see cref="System.IO.InvalidDataException"/>. Catching this base handles any such refusal. The concrete
/// type carries the family's <c>Reason</c>.
/// </summary>
public abstract class MalformedDataException : Exception
{
    /// <summary>Create a malformed-data refusal with a developer-facing message.</summary>
    protected MalformedDataException(string message)
        : base(message)
    {
    }

    /// <summary>Create a malformed-data refusal preserving the original error, if any.</summary>
    protected MalformedDataException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
