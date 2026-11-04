using System;
using KhaozEngine.TileEdit.Tools;
using ModelContextProtocol;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

public class ToolGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Guard_preserves_the_original_exception(bool alreadyMcp)
    {
        Exception original = alreadyMcp ? new McpException("refused tool") : new ArgumentException("refused input");

        var ex = Assert.Throws<McpException>(() => ToolGuard.Guard<int>(() => throw original));

        Assert.Same(original, alreadyMcp ? ex : ex.InnerException);
    }
}
