using KhaozEngine.MapEdit;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class ResourcePathContainmentTests
{
    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("../")]
    [InlineData("..\\")]
    [InlineData("..\\x/y")]
    [InlineData("../x\\y")]
    [InlineData("a/../../x")]
    [InlineData("a\\..\\..\\x")]
    [InlineData("/abs")]
    [InlineData("\\abs")]
    [InlineData("C:\\abs")]
    [InlineData("C:/abs")]
    [InlineData("C:")]
    [InlineData("\\\\server\\share")]
    [InlineData("//server/share")]
    [InlineData("")]
    public void EscapingOrRootedRelativePaths_AreOutsideTheRoot(string relative) =>
        Assert.False(ResourcePathContainment.IsInsideRoot(relative));

    [Theory]
    [InlineData(".")]
    [InlineData("x")]
    [InlineData("published/prefab/hut/abc")]
    [InlineData("published\\prefab\\hut\\abc")]
    [InlineData("..hidden")]
    [InlineData("...x")]
    [InlineData("a/..hidden/b")]
    [InlineData("a\\...\\b")]
    public void RootOrDescendantRelativePaths_AreInsideTheRoot(string relative) =>
        Assert.True(ResourcePathContainment.IsInsideRoot(relative));
}
