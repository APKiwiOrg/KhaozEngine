using Xunit;

namespace KhaozEngine.Tests.App
{
    /// <summary>
    /// Test classes that mutate the process-wide <see cref="KhaozEngine.App.LocalizationContext.Catalog"/> share
    /// this collection so xUnit runs them sequentially (a mutable global static is not parallel-safe).
    /// </summary>
    [CollectionDefinition("AmbientLocalization", DisableParallelization = true)]
    public sealed class AmbientLocalizationCollection
    {
    }
}
