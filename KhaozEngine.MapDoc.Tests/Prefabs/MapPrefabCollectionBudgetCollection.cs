using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

// Real ceiling fixtures serialize large valid collections. Keep them out of parallel test execution to bound memory.
[CollectionDefinition("prefab-collection-budgets", DisableParallelization = true)]
public sealed class MapPrefabCollectionBudgetCollection;
