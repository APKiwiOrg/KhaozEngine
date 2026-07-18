using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

public class SqlServerWorldStoreEscapeTests
{
    [Fact]
    public void LikeEscape_EscapesMetacharacters()
    {
        Assert.Equal("ban\\_x", SqlServerWorldStore.LikeEscape("ban_x"));
        Assert.Equal("a\\%b", SqlServerWorldStore.LikeEscape("a%b"));
        Assert.Equal("x\\[y", SqlServerWorldStore.LikeEscape("x[y"));
        Assert.Equal("p\\\\q", SqlServerWorldStore.LikeEscape("p\\q"));
    }
}
