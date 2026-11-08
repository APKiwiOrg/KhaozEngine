using System.Linq;
using System.Reflection;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalStoreRefusalConstructorFenceTests
{
    static readonly ConstructorInfo[] LegacyConstructors = typeof(JournalStoreException).GetConstructors()
        .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(JournalStoreRefusal)))
        .ToArray();

    [Fact]
    public void Engine_callers_always_supply_a_reason()
        => Assert.Empty(RefusalConstructorFence.EngineCallers(
            [typeof(JournalStoreException).Assembly, typeof(SqliteMutationJournalStore).Assembly,
                typeof(SqlServerMutationJournalStore).Assembly], LegacyConstructors));

    [Fact]
    public void Fence_finds_the_forbidden_constructor()
    {
        var callers = RefusalConstructorFence.EngineCallers([typeof(JournalStoreRefusalConstructorFenceTests).Assembly], LegacyConstructors);
        Assert.Contains($"{typeof(JournalStoreRefusalConstructorFenceTests).FullName}.{nameof(MessageOnlyControl)}", callers);
    }

    static JournalStoreException MessageOnlyControl() => new(JournalStoreFailureKind.Unavailable,
        JournalStoreFailureCertainty.DefinitelyNotCommitted, JournalStoreFailureScope.WholeStore, null, "consumer refusal");
}
