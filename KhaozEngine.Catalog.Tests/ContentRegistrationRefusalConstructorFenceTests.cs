using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

public sealed class ContentRegistrationRefusalConstructorFenceTests
{
    static readonly ConstructorInfo[] LegacyConstructors = typeof(ContentRegistrationException).GetConstructors()
        .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(ContentRegistrationRefusal)))
        .ToArray();

    [Fact]
    public void Engine_callers_always_supply_a_reason()
        => Assert.Empty(RefusalConstructorFence.EngineCallers([typeof(ContentRegistrationException).Assembly], LegacyConstructors));

    [Fact]
    public void Fence_finds_every_forbidden_constructor()
    {
        var callers = RefusalConstructorFence.EngineCallers([typeof(ContentRegistrationRefusalConstructorFenceTests).Assembly], LegacyConstructors);
        foreach (string control in new[] { nameof(EmptyControl), nameof(MessageControl), nameof(InnerControl) })
            Assert.Contains($"{typeof(ContentRegistrationRefusalConstructorFenceTests).FullName}.{control}", callers);
    }

    static ContentRegistrationException EmptyControl() => new();
    static ContentRegistrationException MessageControl() => new("consumer refusal");
    static ContentRegistrationException InnerControl() => new("consumer refusal", new Exception());
}
