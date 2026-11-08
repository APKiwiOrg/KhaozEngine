using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Catalog.SqlServer;
using KhaozEngine.Server.Admin;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

public sealed class ContentAuthoringRefusalConstructorFenceTests
{
    static readonly ConstructorInfo[] LegacyConstructors = typeof(ContentAuthoringException).GetConstructors()
        .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(ContentAuthoringRefusal)))
        .ToArray();

    [Fact]
    public void Engine_callers_always_supply_a_reason()
        => Assert.Empty(RefusalConstructorFence.EngineCallers(
            [typeof(ContentAuthoringException).Assembly, typeof(SqliteContentAuthoringStore).Assembly,
                typeof(SqlServerContentAuthoringStore).Assembly, typeof(AdminHttpServer).Assembly], LegacyConstructors));

    [Fact]
    public void Fence_finds_every_forbidden_constructor()
    {
        var callers = RefusalConstructorFence.EngineCallers([typeof(ContentAuthoringRefusalConstructorFenceTests).Assembly], LegacyConstructors);
        foreach (string control in new[] { nameof(EmptyControl), nameof(MessageControl), nameof(InnerControl), nameof(RowControl), nameof(FindingsControl) })
            Assert.Contains($"{typeof(ContentAuthoringRefusalConstructorFenceTests).FullName}.{control}", callers);
    }

    static ContentAuthoringException EmptyControl() => new();
    static ContentAuthoringException MessageControl() => new("consumer refusal");
    static ContentAuthoringException InnerControl() => new("consumer refusal", new Exception());
    static ContentAuthoringException RowControl() => new("consumer refusal", default, 1, "consumer-token");
    static ContentAuthoringException FindingsControl() => new("consumer refusal", default, 1, "consumer-token", Array.Empty<ContentFinding>());
}
