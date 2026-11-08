using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Dungeon;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Dungeon;

public sealed class DungeonJsonRefusalConstructorFenceTests
{
    static readonly ConstructorInfo[] LegacyConstructors = typeof(DungeonJsonException).GetConstructors()
        .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(DungeonJsonRefusal)))
        .ToArray();

    [Fact]
    public void Engine_callers_always_supply_a_reason()
        => Assert.Empty(RefusalConstructorFence.EngineCallers([typeof(DungeonJsonException).Assembly], LegacyConstructors));

    [Fact]
    public void Fence_finds_every_forbidden_constructor()
    {
        var callers = RefusalConstructorFence.EngineCallers([typeof(DungeonJsonRefusalConstructorFenceTests).Assembly], LegacyConstructors);
        foreach (string control in new[] { nameof(MessageControl) })
            Assert.Contains($"{typeof(DungeonJsonRefusalConstructorFenceTests).FullName}.{control}", callers);
    }

    static DungeonJsonException MessageControl() => new("consumer refusal");
}
