using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>The shared success, pre-commit failure and post-commit failure cases for each import provider.</summary>
public static class CatalogBundleImportCases
{
    /// <summary>Each concrete provider supplies these rows once on its own theory override.</summary>
    public static TheoryData<ContentPublishStep?> Outcomes => new()
    {
        null,
        ContentPublishStep.BeforeCommit,
        ContentPublishStep.DuringSweep,
    };
}
