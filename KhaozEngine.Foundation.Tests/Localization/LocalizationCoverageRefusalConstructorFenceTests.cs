using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Localization.TestKit;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Localization;

public sealed class LocalizationCoverageRefusalConstructorFenceTests
{
    static readonly ConstructorInfo[] LegacyConstructors = typeof(LocalizationCoverageException).GetConstructors()
        .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(LocalizationCoverageRefusal)))
        .ToArray();

    [Fact]
    public void Engine_callers_always_supply_a_reason()
        => Assert.Empty(RefusalConstructorFence.EngineCallers([typeof(LocalizationCoverageException).Assembly], LegacyConstructors));

    [Fact]
    public void Fence_finds_every_forbidden_constructor()
    {
        var callers = RefusalConstructorFence.EngineCallers([typeof(LocalizationCoverageRefusalConstructorFenceTests).Assembly], LegacyConstructors);
        foreach (string control in new[] { nameof(MessageControl) })
            Assert.Contains($"{typeof(LocalizationCoverageRefusalConstructorFenceTests).FullName}.{control}", callers);
    }

    static LocalizationCoverageException MessageControl() => new("consumer refusal");
}
