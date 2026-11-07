using System;
using System.Reflection;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class GitHubApiRefusalConstructorFenceTests
{
    static readonly ConstructorInfo[] MessageOnlyConstructors =
    {
        typeof(GitHubApiException).GetConstructor(new[] { typeof(int), typeof(bool), typeof(TimeSpan?), typeof(string) })!,
    };

    [Fact]
    public void Engine_callers_always_supply_a_reason()
    {
        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(GitHubApiException).Assembly }, MessageOnlyConstructors);

        Assert.Empty(callers);
    }

    [Fact]
    public void Fence_finds_the_forbidden_constructor()
    {
        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(GitHubApiRefusalConstructorFenceTests).Assembly }, MessageOnlyConstructors);

        Assert.Contains($"{typeof(GitHubApiRefusalConstructorFenceTests).FullName}.{nameof(MessageOnlyControl)}", callers);
    }

    static GitHubApiException MessageOnlyControl() => new(500, false, null, "Constructor fence positive control.");
}
