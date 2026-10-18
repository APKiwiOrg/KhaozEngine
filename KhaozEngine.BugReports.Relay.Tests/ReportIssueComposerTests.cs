using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportIssueComposerTests
{
    [Fact]
    public void ComposeIssue_Minimal_MatchesGolden() =>
        AssertGolden("issue-minimal.txt", Composer().ComposeIssue(IssueFixtures.Minimal()).Body);

    [Fact]
    public void ComposeIssue_Full_MatchesGolden() =>
        AssertGolden("issue-full.txt", Composer().ComposeIssue(IssueFixtures.Full()).Body);

    [Fact]
    public void ComposeIssue_HostileDescription_MatchesGolden()
    {
        IssueInput input = WithDescription(IssueFixtures.Minimal(), IssueFixtures.HostileDescription);

        AssertGolden("issue-hostile.txt", Composer().ComposeIssue(input).Body);
    }

    [Fact]
    public void ComposeIssue_SixtyFrames_ThumbnailsAre0_20_39_59()
    {
        string body = Composer().ComposeIssue(IssueFixtures.Full()).Body;
        string thumbnails = Assert.Single(body.Split('\n'), line => line.StartsWith("<a ", StringComparison.Ordinal));

        Assert.Equal(new[] { "000", "020", "039", "059" },
            System.Text.RegularExpressions.Regex.Matches(thumbnails, @"clip/(\d{3})\.jpg").Select(m => m.Groups[1].Value));
        Assert.Equal(4, thumbnails.Split("width=\"240\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ComposeIssue_TwoFrames_ShowsBoth()
    {
        IssueInput input = IssueFixtures.WithFrames(IssueFixtures.Minimal(), 2);
        string body = Composer().ComposeIssue(input).Body;

        Assert.Contains("/clip/000.jpg\" width=\"240\"></a> <a href=", body);
        Assert.Contains("/clip/001.jpg\" width=\"240\"></a>", body);
        Assert.Contains("[Play the clip](" + IssueFixtures.ViewerUrl + ")", body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void ComposeIssue_Mp4Only_LinksClipWithoutThumbnails(int frameCount)
    {
        IssueInput input = IssueFixtures.WithEntry(
            IssueFixtures.WithEntry(IssueFixtures.Minimal(), BundleEntryNames.ClipMp4), BundleEntryNames.ClipIndex) with
        {
            Clip = new ClipIndex(Enumerable.Range(0, frameCount)
                .Select(i => new ClipIndexFrame(i * 100, 16, 960, 540, BundleEntryNames.ClipMp4)).ToArray()),
        };
        string body = Composer().ComposeIssue(input).Body;

        Assert.Contains("### Clip\n\n[Play the clip](" + IssueFixtures.ViewerUrl + ")\n\n### Files", body);
        Assert.DoesNotContain("<img", body);
    }

    [Fact]
    public void ComposeIssue_NoFramesAndNoMp4_OmitsClip()
    {
        IssueInput input = IssueFixtures.Minimal() with { Clip = new ClipIndex(Array.Empty<ClipIndexFrame>()) };

        Assert.DoesNotContain("### Clip", Composer().ComposeIssue(input).Body);
    }

    [Fact]
    public void ComposeIssue_JpegScreenshot_UsesManifestEntry()
    {
        IssueInput input = IssueFixtures.WithEntry(IssueFixtures.Minimal(), BundleEntryNames.ScreenshotJpeg);
        string body = Composer().ComposeIssue(input).Body;

        Assert.Contains("### Screenshot\n\n![Screenshot](https://media.example.test/reports/" + IssueFixtures.Folder + "/screenshot.jpg)", body);
    }

    [Fact]
    public void ComposeIssue_LabelsAndTitle_FromOptionsAndBuildSection()
    {
        var options = IssueFixtures.Options() with { GameDisplayName = "Another game", Labels = new[] { "custom" } };
        GitHubIssueDraft issue = new ReportIssueComposer(options).ComposeIssue(IssueFixtures.Minimal());

        Assert.Equal("[Another game 0.29.3] Fell through the floor", issue.Title);
        Assert.Equal(new[] { "custom" }, issue.Labels);
    }

    [Fact]
    public void ComposeIssue_MissingVersion_UsesUnknown()
    {
        IssueInput input = IssueFixtures.Minimal() with { Sections = Array.Empty<CapturedSection>() };

        Assert.Equal("[Grimhollow unknown] Fell through the floor", Composer().ComposeIssue(input).Title);
    }

    [Fact]
    public void ComposeIssue_ReporterAndCategory_AreSafeTableCells()
    {
        IssueInput input = IssueFixtures.Minimal();
        input = input with
        {
            Report = input.Report with
            {
                Reporter = new ReporterIdentity("<@42>|`x`", "@Ash|<img>\nnext", "<@42>"),
                Manifest = input.Report.Manifest with { Category = "A|B\r\n@here" },
            },
        };
        string body = Composer().ComposeIssue(input).Body;

        Assert.Contains("| Reporter | \\@\u200bAsh\\|\\<img\\> next |", body);
        Assert.Contains("| Handle | `<@42>` |", body);
        Assert.Contains("| Account | `` <@42>\\|`x` `` |", body);
        Assert.Contains("| Category | A\\|B \\@\u200bhere |", body);
    }

    [Fact]
    public void ComposeIssue_CapturedTime_UsesUtc()
    {
        IssueInput input = IssueFixtures.Minimal();
        input = input with
        {
            Report = input.Report with
            {
                Manifest = input.Report.Manifest with { CapturedAt = new DateTimeOffset(2026, 10, 17, 11, 2, 3, TimeSpan.FromHours(10)) },
            },
        };

        Assert.Contains("| Captured | 2026-10-17 01:02:03 UTC |", Composer().ComposeIssue(input).Body);
    }

    [Fact]
    public void ComposeIssue_LogTail_LastFiftyLines_EachCutTo400()
    {
        IssueInput input = IssueFixtures.Minimal() with
        {
            LogTail = Enumerable.Range(0, 60).Select(i => i.ToString("D2", CultureInfo.InvariantCulture) + new string('x', 10_000)).ToArray(),
        };
        string body = Composer().ComposeIssue(input).Body;
        string code = body.Split("```text\n", StringSplitOptions.None)[1].Split("\n```", StringSplitOptions.None)[0];
        string[] lines = code.Split('\n');

        Assert.Equal(50, lines.Length);
        Assert.Equal("10" + new string('x', 398), lines[0]);
        Assert.Equal("59" + new string('x', 398), lines[^1]);
        Assert.All(lines, line => Assert.Equal(400, line.Length));
    }

    [Fact]
    public void ComposeIssue_MissingFactsOmitted()
    {
        string body = Composer().ComposeIssue(IssueFixtures.Minimal()).Body;

        Assert.DoesNotContain("| Backend |", body);
        Assert.DoesNotContain("| GPU |", body);
        Assert.DoesNotContain("| Server tick |", body);
        Assert.DoesNotContain("<details>", body);
        Assert.DoesNotContain("### Screenshot", body);
    }

    [Fact]
    public void ComposeIssue_SnapshotLink_OnlyWhenInBody()
    {
        IssueInput input = IssueFixtures.Full();
        string inBody = Composer().ComposeIssue(input).Body;
        input = input with { Report = input.Report with { ServerSnapshot = ServerSnapshotState.Commented } };

        Assert.Contains("- [Server snapshot]", inBody);
        Assert.DoesNotContain("- [Server snapshot]", Composer().ComposeIssue(input).Body);
    }

    [Fact]
    public void ComposeIssue_WorstCase_BodyAtMost60000()
    {
        IssueInput input = WorstCase();
        var options = IssueFixtures.Options() with { IssueFacts = LargeFacts(5) };
        string body = new ReportIssueComposer(options).ComposeIssue(input).Body;

        Assert.Equal(127, input.Report.Manifest.Entries.Count);
        Assert.True(body.Length <= 60_000, $"Body has {body.Length} characters.");
        Assert.Contains("### Files\n\n- [Viewer](" + IssueFixtures.ViewerUrl + ")\n- 127 more files in the report folder\n", body);
        Assert.DoesNotContain("- [sections/", body);
        Assert.Contains("<details>", body);
        Assert.Contains("> " + string.Concat(Enumerable.Repeat("\\&", 4000)), body);
    }

    [Fact]
    public void ComposeIssue_StillOversizedAfterFiles_OmitsLogTail()
    {
        var options = IssueFixtures.Options() with { IssueFacts = LargeFacts(40) };
        IssueInput input = WorstCase();
        input = input with { Report = input.Report with { Reporter = input.Report.Reporter with { DisplayName = new string('&', 12_000) } } };
        string body = new ReportIssueComposer(options).ComposeIssue(input).Body;

        Assert.True(body.Length <= 60_000, $"Body has {body.Length} characters.");
        Assert.Contains("- 127 more files in the report folder\n\nLog tail omitted for size. Open the viewer.\n", body);
        Assert.DoesNotContain("<details>", body);
    }

    [Fact]
    public void ComposeSnapshotComment_MatchesGolden() =>
        AssertGolden("comment-snapshot.txt", Composer().ComposeSnapshotComment(IssueFixtures.Full()));

    [Fact]
    public void ComposeSnapshotComment_NoSnapshotFacts_OmitsTable()
    {
        IssueInput input = IssueFixtures.Full() with { Snapshot = IssueFixtures.Snapshot(new Dictionary<string, JsonElement>()) };
        string comment = Composer().ComposeSnapshotComment(input);

        Assert.DoesNotContain("| Field |", comment);
        Assert.Contains("Received for report " + ReportIds.ReferenceCode(IssueFixtures.Id) + ".\n\n- [Server snapshot]", comment);
        Assert.EndsWith("- [Viewer](" + IssueFixtures.ViewerUrl + ")\n", comment);
    }

    [Fact]
    public void ComposeSnapshotComment_UsesSnapshotValuesAndNeutralisesFacts()
    {
        IssueInput input = IssueFixtures.Minimal() with
        {
            Snapshot = IssueFixtures.Snapshot(new Dictionary<string, JsonElement>
            {
                ["build"] = JsonSerializer.SerializeToElement(new { version = "server|@here\n<img>" }),
            }),
        };
        string comment = Composer().ComposeSnapshotComment(input);

        Assert.Contains("| Game version | server\\|\\@\u200bhere \\<img\\> |", comment);
        Assert.DoesNotContain("0.29.3", comment);
        Assert.DoesNotContain("| Platform |", comment);
    }

    [Theory]
    [InlineData("nested.text", "hello")]
    [InlineData("nested.number", "12.5")]
    [InlineData("nested.boolean", "true")]
    [InlineData("nested.object", "{\"x\":1}")]
    [InlineData("nested.array", "[1,false,\"x\"]")]
    [InlineData("nested.nil", null)]
    [InlineData("nested.absent", null)]
    [InlineData("nested.text.missing", null)]
    public void FactReader_DotPathAndJsonKinds(string property, string? expected)
    {
        IssueInput input = IssueFixtures.WithSection(IssueFixtures.Minimal(), "facts",
            "{\"nested\":{\"text\":\"hello\",\"number\":12.5,\"boolean\":true,\"object\":{ \"x\" : 1 },\"array\":[1, false, \"x\"],\"nil\":null}}");

        Assert.Equal(expected, IssueFactReader.Read(input, new IssueFact("Fact", "facts", property)));
    }

    [Fact]
    public void FactReader_ClientSectionWins_ThenSnapshotFallback()
    {
        IssueInput input = IssueFixtures.Full() with
        {
            Snapshot = IssueFixtures.Snapshot(new Dictionary<string, JsonElement>
            {
                ["build"] = JsonSerializer.SerializeToElement(new { version = "server version", missing = "not used" }),
                ["server"] = JsonSerializer.SerializeToElement(new { tick = 42 }),
            }),
        };

        Assert.Equal("0.29.3", IssueFactReader.Read(input, new IssueFact("Version", "build", "version")));
        Assert.Equal("42", IssueFactReader.Read(input, new IssueFact("Tick", "server", "tick")));
        Assert.Null(IssueFactReader.Read(input, new IssueFact("Missing", "build", "missing")));
        Assert.Null(IssueFactReader.Read(input, new IssueFact("Missing", "absent", "value")));
    }

    [Fact]
    public void FactReader_LongValue_CutByTextElementsWithEllipsis()
    {
        string value = string.Concat(Enumerable.Repeat("e\u0301", 130));
        IssueInput input = IssueFixtures.WithSection(IssueFixtures.Minimal(), "facts", JsonSerializer.Serialize(new { value }));
        string? fact = IssueFactReader.Read(input, new IssueFact("Fact", "facts", "value"));

        Assert.Equal(string.Concat(Enumerable.Repeat("e\u0301", 117)) + "...", fact);
        Assert.Equal(120, new StringInfo(fact!).LengthInTextElements);
    }

    [Fact]
    public void FactReader_Exactly120TextElements_Unchanged()
    {
        string value = new('x', 120);
        IssueInput input = IssueFixtures.WithSection(IssueFixtures.Minimal(), "facts", JsonSerializer.Serialize(new { value }));

        Assert.Equal(value, IssueFactReader.Read(input, new IssueFact("Fact", "facts", "value")));
    }

    public static TheoryData<string, string> HostileInlineText => new()
    {
        { "![pixel](https://attacker.example/pixel)", "\\!\\[pixel\\]\\(https\u200b\\:\\/\\/attacker\\.example\\/pixel\\)" },
        { "[visit](https://attacker.example/)", "\\[visit\\]\\(https\u200b\\:\\/\\/attacker\\.example\\/\\)" },
        { "https://attacker.example/path", "https\u200b\\:\\/\\/attacker\\.example\\/path" },
        { "www.attacker.example", "www\u200b\\.attacker\\.example" },
    };

    [Theory]
    [MemberData(nameof(HostileInlineText))]
    public void ComposeIssue_HostileDescription_IsInert(string payload, string expected)
    {
        string body = Composer().ComposeIssue(WithDescription(IssueFixtures.Minimal(), payload)).Body;

        AssertInertInlineText(expected, body.Split('\n')[2][2..]);
        Assert.Contains("[Viewer](" + IssueFixtures.ViewerUrl + ")", body);
    }

    [Theory]
    [MemberData(nameof(HostileInlineText))]
    public void ComposeIssue_HostileFactValue_IsInert(string payload, string expected)
    {
        IssueInput input = WithPlatformValue(payload);
        string body = Composer().ComposeIssue(input).Body;

        AssertInertInlineText("| Platform | " + expected + " |", Assert.Single(body.Split('\n'), line => line.StartsWith("| Platform |", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(HostileInlineText))]
    public void ComposeIssue_HostileDisplayName_IsInert(string payload, string expected)
    {
        IssueInput input = IssueFixtures.Minimal();
        input = input with { Report = input.Report with { Reporter = input.Report.Reporter with { DisplayName = payload } } };
        string body = Composer().ComposeIssue(input).Body;

        AssertInertInlineText("| Reporter | " + expected + " |", Assert.Single(body.Split('\n'), line => line.StartsWith("| Reporter |", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(HostileInlineText))]
    public void ComposeIssue_HostileCategory_IsInert(string payload, string expected)
    {
        IssueInput input = IssueFixtures.Minimal();
        input = input with { Report = input.Report with { Manifest = input.Report.Manifest with { Category = payload } } };
        string body = Composer().ComposeIssue(input).Body;

        AssertInertInlineText("| Category | " + expected + " |", Assert.Single(body.Split('\n'), line => line.StartsWith("| Category |", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(HostileInlineText))]
    public void ComposeIssue_HostileFactLabel_IsInert(string payload, string expected)
    {
        var options = IssueFixtures.Options() with { IssueFacts = new[] { new IssueFact(payload, "platform", "os") } };
        string body = new ReportIssueComposer(options).ComposeIssue(IssueFixtures.Minimal()).Body;

        AssertInertInlineText("| " + expected + " | macOS 26\\.0 |", Assert.Single(body.Split('\n'), line => line.EndsWith("| macOS 26\\.0 |", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(HostileInlineText))]
    public void ComposeSnapshotComment_HostileText_IsInert(string payload, string expected)
    {
        IssueInput input = WithSnapshotValue(payload);
        string comment = Composer().ComposeSnapshotComment(input);

        AssertInertInlineText("| Game version | " + expected + " |", Assert.Single(comment.Split('\n'), line => line.StartsWith("| Game version |", StringComparison.Ordinal)));
        Assert.Contains("[Viewer](" + IssueFixtures.ViewerUrl + ")", comment);
    }

    [Theory]
    [MemberData(nameof(HostileInlineText))]
    public void ComposeSnapshotComment_HostileFactLabel_IsInert(string payload, string expected)
    {
        var options = IssueFixtures.Options() with { IssueFacts = new[] { new IssueFact(payload, "build", "version") } };
        string comment = new ReportIssueComposer(options).ComposeSnapshotComment(WithSnapshotValue("value"));

        AssertInertInlineText("| " + expected + " | value |", Assert.Single(comment.Split('\n'), line => line.EndsWith("| value |", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("#\u000712", "\\#\u200b12")]
    [InlineData("GH\u0007-7", "GH\u200b\\-7")]
    [InlineData("git\u0007hub.com", "github\u200b\\.com")]
    public void ComposeIssue_RemovesControlsBeforeMatchingReferences(string payload, string expected)
    {
        string body = Composer().ComposeIssue(WithDescription(WithPlatformValue(payload), payload)).Body;

        Assert.Contains("> " + expected + "\n", body);
        Assert.Contains("| Platform | " + expected + " |", body);
    }

    [Theory]
    [InlineData("#\u000712", "\\#\u200b12")]
    [InlineData("GH\u0007-7", "GH\u200b\\-7")]
    [InlineData("git\u0007hub.com", "github\u200b\\.com")]
    public void ComposeSnapshotComment_RemovesControlsBeforeMatchingReferences(string payload, string expected) =>
        Assert.Contains("| Game version | " + expected + " |", Composer().ComposeSnapshotComment(WithSnapshotValue(payload)));

    [Fact]
    public void ComposeIssue_CombiningSequenceFact_IsBounded()
    {
        IssueInput input = WithPlatformValue("a" + new string('\u0301', 70_000));
        string body = Composer().ComposeIssue(input).Body;

        Assert.True(body.Length <= 60_000, $"Body has {body.Length} characters.");
        Assert.Contains("[Viewer](" + IssueFixtures.ViewerUrl + ")", body);
        Assert.Equal("...", IssueFactReader.Read(input, new IssueFact("Platform", "platform", "os")));
    }

    [Theory]
    [InlineData(119, 0)]
    [InlineData(120, 0)]
    [InlineData(121, 117)]
    public void FactReader_CombiningSequences_RespectUtf16AndTextElementBudgets(int count, int retained)
    {
        string value = string.Concat(Enumerable.Repeat("e\u0301", count));
        string expected = retained == 0 ? value : string.Concat(Enumerable.Repeat("e\u0301", retained)) + "...";
        string? fact = IssueFactReader.Read(WithPlatformValue(value), new IssueFact("Platform", "platform", "os"));

        Assert.Equal(expected, fact);
        Assert.True(fact!.Length <= 240);
        Assert.True(new StringInfo(fact).LengthInTextElements <= 120);
    }

    [Fact]
    public void FactReader_CombiningSequences_Utf16BudgetCutsBeforeTextElementLimit()
    {
        string value = string.Concat(Enumerable.Repeat("e\u0301\u0302", 100));
        string? fact = IssueFactReader.Read(WithPlatformValue(value), new IssueFact("Platform", "platform", "os"));

        Assert.Equal(string.Concat(Enumerable.Repeat("e\u0301\u0302", 79)) + "...", fact);
        Assert.Equal(240, fact!.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compose_FactLabelsAndCount_AreBounded(bool snapshot)
    {
        string oversizedLabel = "a" + new string('\u0301', 70_000);
        IssueFact[] facts = new[] { new IssueFact(oversizedLabel, "build", "version") }
            .Concat(Enumerable.Range(1, 40).Select(i => new IssueFact("Fact " + i.ToString(CultureInfo.InvariantCulture), "build", "version"))).ToArray();
        var composer = new ReportIssueComposer(IssueFixtures.Options() with { IssueFacts = facts });
        IssueInput input = WithSnapshotValue("value");
        string body = snapshot ? composer.ComposeSnapshotComment(input) : composer.ComposeIssue(input).Body;

        Assert.True(body.Length <= 60_000, $"Body has {body.Length} characters.");
        Assert.Contains("| \\.\\.\\. |", body);
        Assert.Contains("| Fact 39 |", body);
        Assert.DoesNotContain("| Fact 40 |", body);
        Assert.DoesNotContain('\u0301', body);
    }

    [Fact]
    public void ComposeIssue_LongFactLabel_CutByTextElements()
    {
        var options = IssueFixtures.Options() with { IssueFacts = new[] { new IssueFact(new string('x', 130), "platform", "os") } };
        string body = new ReportIssueComposer(options).ComposeIssue(IssueFixtures.Minimal()).Body;

        Assert.Contains("| " + new string('x', 117) + "\\.\\.\\. | macOS 26\\.0 |", body);
        Assert.DoesNotContain(new string('x', 118), body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposeIssue_OversizedAfterBothFallbacks_UsesMinimalBody(bool hasLog)
    {
        IssueInput input = IssueFixtures.Minimal();
        input = input with
        {
            Report = input.Report with { Reporter = input.Report.Reporter with { DisplayName = new string('x', 70_000) } },
            LogTail = hasLog ? new[] { "line" } : Array.Empty<string>(),
        };
        string body = Composer().ComposeIssue(input).Body;

        Assert.True(body.Length <= 60_000, $"Body has {body.Length} characters.");
        Assert.Equal("Report R-YMWQV0\n\n- [Viewer](" + IssueFixtures.ViewerUrl
            + ")\n- 2 files in the report folder\n\nReport details omitted for size. Open the viewer.\n", body);
    }

    [Fact]
    public void ComposeIssue_ViewerLinkExceedsBudget_RejectsInsteadOfReturningOversizedBody()
    {
        var options = IssueFixtures.Options() with
        {
            PublicMediaBase = new Uri("https://media.example.test/" + new string('x', 60_000) + "/"),
        };
        var composer = new ReportIssueComposer(options);

        Assert.Throws<InvalidOperationException>(() => composer.ComposeIssue(IssueFixtures.Minimal()));
    }

    static void AssertInertInlineText(string expected, string actual)
    {
        Assert.Equal(expected, actual);
        Assert.DoesNotMatch(@"(?<!\\)!?\[[^\n]*?(?<!\\)\]\(", actual);
        Assert.DoesNotContain("https://attacker", actual);
        Assert.DoesNotContain("www.attacker", actual);
    }

    static IssueInput WithPlatformValue(string value) => IssueFixtures.Minimal() with
    {
        Sections = new[] { IssueFixtures.Section("platform", JsonSerializer.Serialize(new { os = value })) },
    };

    static IssueInput WithSnapshotValue(string value) => IssueFixtures.Minimal() with
    {
        Snapshot = IssueFixtures.Snapshot(new Dictionary<string, JsonElement>
        {
            ["build"] = JsonSerializer.SerializeToElement(new { version = value }),
        }),
    };

    static ReportIssueComposer Composer() => new(IssueFixtures.Options());

    static void AssertGolden(string name, string actual)
    {
        string golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("{reference}", ReportIds.ReferenceCode(IssueFixtures.Id), StringComparison.Ordinal);
        Assert.Equal(golden, actual);
    }

    static IssueInput WithDescription(IssueInput input, string description) => input with
    {
        Report = input.Report with { Manifest = input.Report.Manifest with { Description = description } },
    };

    static IssueFact[] LargeFacts(int count) => Enumerable.Range(0, count)
        .Select(i => new IssueFact("Fact " + i.ToString(CultureInfo.InvariantCulture), "s" + new string('x', 47), "value")).ToArray();

    static IssueInput WorstCase()
    {
        IssueInput input = WithDescription(IssueFixtures.Minimal(), new string('&', 4000));
        CapturedSection[] sections = Enumerable.Range(0, 127).Select(i => IssueFixtures.Section(
            i == 0 ? "s" + new string('x', 47) : "s" + i.ToString("D3", CultureInfo.InvariantCulture) + new string('x', 44),
            JsonSerializer.Serialize(new { value = new string('&', 1000) }))).ToArray();
        return input with
        {
            Sections = sections,
            Report = input.Report with
            {
                Manifest = input.Report.Manifest with { Entries = sections.Select(s => IssueFixtures.Entry(BundleEntryNames.Section(s.Key))).ToArray() },
            },
            LogTail = Enumerable.Repeat(new string('x', 10_000), 50).ToArray(),
        };
    }
}
