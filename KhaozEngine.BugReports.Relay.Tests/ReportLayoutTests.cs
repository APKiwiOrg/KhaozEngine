using System;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportLayoutTests
{
    const string Id = "0192f3a4b5c67d8e9f0a1b2c3d4e5f60";
    static readonly DateTimeOffset ReceivedAt = new(2026, 10, 17, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void NewFolder_HasGameMonthIdAndHexToken()
    {
        string folder = ReportLayout.NewFolder("grimhollow", Id, ReceivedAt);

        Assert.Matches("^grimhollow/2026-10/" + Id + "-[0-9a-f]{32}$", folder);
    }

    [Fact]
    public void NewFolder_TwoCalls_DifferInToken()
    {
        string first = ReportLayout.NewFolder("grimhollow", Id, ReceivedAt);
        string second = ReportLayout.NewFolder("grimhollow", Id, ReceivedAt);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NewFolder_LastInstantOfMonthWithOffset_UsesUtcMonth()
    {
        var receivedAt = new DateTimeOffset(2026, 11, 1, 9, 59, 59, 900, TimeSpan.FromHours(10));

        string folder = ReportLayout.NewFolder("grimhollow", Id, receivedAt);

        Assert.Matches("^grimhollow/2026-10/" + Id + "-[0-9a-f]{32}$", folder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Grimhollow")]
    [InlineData("g/other")]
    [InlineData("../g")]
    public void NewFolder_InvalidGameId_Throws(string gameId)
    {
        Assert.Throws<ArgumentException>(() => ReportLayout.NewFolder(gameId, Id, ReceivedAt));
    }

    [Fact]
    public void PrivatePaths_AreKeyedByReportId()
    {
        Assert.Equal("records/" + Id + ".json", ReportLayout.ReportRecord(Id));
        Assert.Equal("accepted/" + Id + ".json", ReportLayout.AcceptedIndex(Id));
        Assert.Equal("snapshots/" + Id + ".json", ReportLayout.PendingSnapshot(Id));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("0192f3a4b5c64d8e9f0a1b2c3d4e5f60")]
    [InlineData("0192F3A4B5C67D8E9F0A1B2C3D4E5F60")]
    [InlineData("../other")]
    public void Layout_InvalidReportId_Throws(string reportId)
    {
        Assert.Throws<ArgumentException>(() => ReportLayout.NewFolder("grimhollow", reportId, ReceivedAt));
        Assert.Throws<ArgumentException>(() => ReportLayout.ReportRecord(reportId));
        Assert.Throws<ArgumentException>(() => ReportLayout.AcceptedIndex(reportId));
        Assert.Throws<ArgumentException>(() => ReportLayout.PendingSnapshot(reportId));
    }

    [Fact]
    public void Entry_JoinsFolderAndNestedName()
    {
        Assert.Equal("g/2026-10/a/clip/007.jpg", ReportLayout.Entry("g/2026-10/a", "clip/007.jpg"));
    }

    [Theory]
    [InlineData("", "viewer.html")]
    [InlineData("/g", "viewer.html")]
    [InlineData("g/", "viewer.html")]
    [InlineData("g/../other", "viewer.html")]
    [InlineData("g", "")]
    [InlineData("g", "/viewer.html")]
    [InlineData("g", "../viewer.html")]
    [InlineData("g", "clip//007.jpg")]
    [InlineData("g", "Viewer.html")]
    public void Entry_InvalidPath_Throws(string folder, string name)
    {
        Assert.Throws<ArgumentException>(() => ReportLayout.Entry(folder, name));
    }

    [Theory]
    [InlineData("manifest.json", "application/json; charset=utf-8", "no-cache")]
    [InlineData("viewer.html", "text/html; charset=utf-8", "no-cache")]
    [InlineData("screenshot.png", "image/png", "public, max-age=86400")]
    [InlineData("clip/007.jpg", "image/jpeg", "public, max-age=86400")]
    [InlineData("clip.mp4", "video/mp4", "public, max-age=86400")]
    public void HeadersFor_ByExtension(string name, string contentType, string cacheControl)
    {
        Assert.Equal(new ReportBlobHeaders(contentType, cacheControl), ReportLayout.HeadersFor(name));
    }

    [Fact]
    public void HeadersFor_UnknownExtension_Throws()
    {
        Assert.Throws<ArgumentException>(() => ReportLayout.HeadersFor("notes.txt"));
    }

    [Fact]
    public void PublicUrl_JoinsBaseAndPath()
    {
        Uri url = ReportLayout.PublicUrl(new Uri("https://media.example.test/reports/"), "g/2026-10/a/viewer.html");

        Assert.Equal("https://media.example.test/reports/g/2026-10/a/viewer.html", url.AbsoluteUri);
    }

    [Fact]
    public void PublicUrl_BaseWithoutTrailingSlash_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ReportLayout.PublicUrl(new Uri("https://media.example.test/reports"), "g/viewer.html"));
    }

    [Theory]
    [InlineData("/other/viewer.html")]
    [InlineData("../viewer.html")]
    [InlineData("g/viewer.html?token=secret")]
    public void PublicUrl_InvalidPath_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => ReportLayout.PublicUrl(new Uri("https://media.example.test/reports/"), path));
    }
}
