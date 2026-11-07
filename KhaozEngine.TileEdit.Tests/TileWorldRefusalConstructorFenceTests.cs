using System;
using System.Reflection;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileEdit;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

public sealed class TileWorldRefusalConstructorFenceTests
{
    static readonly ConstructorInfo[] MessageOnlyConstructors =
    {
        typeof(TileWorldException).GetConstructor(new[] { typeof(string) })!,
        typeof(TileWorldException).GetConstructor(new[] { typeof(string), typeof(Exception) })!,
    };

    [Fact]
    public void Engine_callers_always_supply_a_reason()
    {
        // Scan every method, including compiler-generated lambda bodies, in every owning assembly.
        var callers = RefusalConstructorFence.EngineCallers(new[]
        {
            typeof(TileWorldException).Assembly,
            typeof(PgmReader).Assembly,
            typeof(TileWorldColliders).Assembly,
            typeof(TileGroundMaterials).Assembly,
            typeof(TileEditSession).Assembly,
        }, MessageOnlyConstructors);

        Assert.Empty(callers);
    }

    [Theory]
    [InlineData(nameof(MessageOnlyControl))]
    [InlineData(nameof(MessageAndInnerControl))]
    public void Fence_finds_each_forbidden_constructor(string control)
    {
        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(TileWorldRefusalConstructorFenceTests).Assembly }, MessageOnlyConstructors);

        Assert.Contains($"{typeof(TileWorldRefusalConstructorFenceTests).FullName}.{control}", callers);
    }

    static TileWorldException MessageOnlyControl() => new("Constructor fence positive control.");

    static TileWorldException MessageAndInnerControl() =>
        new("Constructor fence positive control.", new InvalidOperationException());
}
