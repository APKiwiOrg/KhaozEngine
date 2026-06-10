using KhaozEngine.Localization;
using Xunit;

namespace KhaozEngine.Tests;

public class LocalizationManagerTests
{
    [Fact]
    public void DefaultCultureCode_IsEnUs()
    {
        Assert.Equal("en-US", LocalizationManager.DEFAULT_CULTURE_CODE);
    }
}
