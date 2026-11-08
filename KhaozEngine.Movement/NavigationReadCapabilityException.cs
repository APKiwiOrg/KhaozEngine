using System;

namespace KhaozEngine.Movement;

/// <summary>A navigation provider capability refusal. Existing not-supported catches remain valid.</summary>
public sealed class NavigationReadCapabilityException : NotSupportedException
{
    /// <summary>Creates a refusal with the unchanged developer message.</summary>
    public NavigationReadCapabilityException(NavigationReadRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>The required read policy.</summary>
    public NavigationReadRefusal Reason { get; }
    /// <summary>The interface the refused participant must declare.</summary>
    public Type? RequiredCapability { get; init; }
    /// <summary>The participant that lacks the required capability.</summary>
    public NavigationSourceRole? SourceRole { get; init; }
}
