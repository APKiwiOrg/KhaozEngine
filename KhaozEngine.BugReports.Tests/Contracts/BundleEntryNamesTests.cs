using System;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Contracts;

public sealed class BundleEntryNamesTests
{
    [Fact]
    public void Section_and_frame_names()
    {
        Assert.Equal("sections/build.json", BundleEntryNames.Section("build"));
        Assert.Equal("clip/007.jpg", BundleEntryNames.ClipFrame(7));
        Assert.Equal("clip/000.jpg", BundleEntryNames.ClipFrame(0));
        Assert.Equal("clip/119.jpg", BundleEntryNames.ClipFrame(119));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("Build")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("build\n")]
    [InlineData("é")]
    public void Section_rejects_bad_keys(string key)
    {
        Assert.False(BundleEntryNames.IsValidSectionKey(key));
        Assert.Throws<ArgumentException>(() => BundleEntryNames.Section(key));
    }

    [Fact]
    public void Section_key_limits_and_punctuation_are_exact()
    {
        string longest = "0" + new string('a', 47);
        Assert.True(BundleEntryNames.IsValidSectionKey("a"));
        Assert.True(BundleEntryNames.IsValidSectionKey("game.state_0-1"));
        Assert.True(BundleEntryNames.IsValidSectionKey(longest));
        Assert.Equal("sections/" + longest + ".json", BundleEntryNames.Section(longest));
        Assert.False(BundleEntryNames.IsValidSectionKey(longest + "a"));
        Assert.Throws<ArgumentException>(() => BundleEntryNames.Section(longest + "a"));
        Assert.False(BundleEntryNames.IsAllowed("sections/" + longest + "a.json"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(120)]
    public void ClipFrame_rejects_out_of_range(int i)
        => Assert.Throws<ArgumentOutOfRangeException>(() => BundleEntryNames.ClipFrame(i));

    [Theory]
    [InlineData("manifest.json", true)]
    [InlineData("screenshot.png", true)]
    [InlineData("screenshot.jpg", true)]
    [InlineData("clip/index.json", true)]
    [InlineData("clip.mp4", true)]
    [InlineData("sections/game.state.json", true)]
    [InlineData("clip/119.jpg", true)]
    [InlineData("clip/120.jpg", false)]
    [InlineData("clip/7.jpg", false)]
    [InlineData("clip/0000.jpg", false)]
    [InlineData("clip/+01.jpg", false)]
    [InlineData("clip/٠٠١.jpg", false)]
    [InlineData("../manifest.json", false)]
    [InlineData("/manifest.json", false)]
    [InlineData("sections\\a.json", false)]
    [InlineData("sections/../x.json", false)]
    [InlineData("sections/.json", false)]
    [InlineData("sections/.hidden.json", false)]
    [InlineData("sections/Build.json", false)]
    [InlineData("sections/a.json/extra", false)]
    [InlineData("manifest.json\n", false)]
    [InlineData("notes.txt", false)]
    public void IsAllowed_is_an_exact_whitelist(string name, bool allowed)
        => Assert.Equal(allowed, BundleEntryNames.IsAllowed(name));

    [Theory]
    [InlineData("grimhollow", true)]
    [InlineData("0-game", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-game", false)]
    [InlineData("Grimhollow", false)]
    [InlineData("grim_hollow", false)]
    [InlineData("grim.hollow", false)]
    public void IsValidGameId_matches_the_game_id_rule(string? gameId, bool valid)
        => Assert.Equal(valid, BundleEntryNames.IsValidGameId(gameId!));
}
