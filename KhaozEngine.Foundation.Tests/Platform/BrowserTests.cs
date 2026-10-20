using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using KhaozEngine.Platform;
using Xunit;

namespace KhaozEngine.Tests.Platform;

public sealed class BrowserTests
{
    static readonly Uri SpacedFile = new(Path.Combine(Path.GetTempPath(), "My Reports", "a b", "viewer.html"));

    [Theory]
    [InlineData("osx", "open")]
    [InlineData("linux", "xdg-open")]
    public void Url_with_spaces_is_passed_as_one_argument(string os, string command)
    {
        ProcessStartInfo info = Browser.CreateStartInfo(SpacedFile, os == "osx" ? OSPlatform.OSX : OSPlatform.Linux);

        Assert.Equal(command, info.FileName);
        string argument = Assert.Single(info.ArgumentList);
        Assert.Equal(SpacedFile.AbsoluteUri, argument);
        Assert.Contains("My%20Reports/a%20b/viewer.html", argument, StringComparison.Ordinal);
        Assert.Equal("", info.Arguments);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void Windows_opens_the_url_through_the_shell()
    {
        ProcessStartInfo info = Browser.CreateStartInfo(SpacedFile, OSPlatform.Windows);

        Assert.Equal(SpacedFile.ToString(), info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Empty(info.ArgumentList);
    }
}
