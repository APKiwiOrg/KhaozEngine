using System.Collections.Generic;

namespace KhaozEngine.BugReports.Relay;

/// <summary>One row of the issue's facts table, read from a report section.</summary>
/// <param name="Label">The row label shown in the issue.</param>
/// <param name="Section">The section key the value is read from.</param>
/// <param name="Property">The dot path of the value inside the section.</param>
public sealed record IssueFact(string Label, string Section, string Property)
{
    /// <summary>The facts every engine report carries, in table order.</summary>
    public static IReadOnlyList<IssueFact> EngineDefaults { get; } = new[]
    {
        new IssueFact("Platform", "platform", "os"),
        new IssueFact("Game version", "build", "version"),
        new IssueFact("Engine version", "build", "engineVersion"),
        new IssueFact("Backend", "graphics", "backend"),
        new IssueFact("GPU", "graphics", "adapter"),
    };
}
