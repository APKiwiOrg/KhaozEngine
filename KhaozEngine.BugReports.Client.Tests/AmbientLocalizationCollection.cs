using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>
/// Test classes that read or replace the process-wide <see cref="KhaozEngine.App.LocalizationContext.Catalog"/>
/// share this collection so xUnit runs them one at a time.
/// </summary>
[CollectionDefinition("AmbientLocalization", DisableParallelization = true)]
public sealed class AmbientLocalizationCollection
{
}
