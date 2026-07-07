using System.Collections.Generic;

namespace KhaozEngine.Identity;

/// <summary>A provider credential that the server verified to a stable subject.</summary>
public readonly record struct VerifiedIdentity(
    string Subject, string ProviderId, string? DisplayName, IReadOnlyDictionary<string, string> Claims);
