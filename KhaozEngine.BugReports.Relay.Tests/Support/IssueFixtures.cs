using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal static class IssueFixtures
{
    public const string Id = "0192f3a4b5c67d8e9f0a1b2c3d4e5f60";
    public const string Folder = "grimhollow/2026-10/" + Id + "-00112233445566778899aabbccddeeff";
    public const string ViewerUrl = "https://media.example.test/reports/" + Folder + "/viewer.html";
    public const string HostileDescription = "</details>\n| a | b |\n```\n<img src=x>\n> quoted\n@here #1";
    static readonly DateTimeOffset CapturedAt = new(2026, 10, 17, 1, 2, 3, TimeSpan.Zero);

    public static BugReportRelayOptions Options() => new()
    {
        GameId = "grimhollow",
        GameDisplayName = "Grimhollow",
        RepositoryOwner = "example",
        RepositoryName = "reports",
        Labels = new[] { "kind/bug", "game/grimhollow" },
        PublicMediaBase = new Uri("https://media.example.test/reports/"),
        ServiceKey = new string('s', 32),
        IssueFacts = IssueFact.EngineDefaults.Concat(new[]
        {
            new IssueFact("Server tick", "server", "tick"),
            new IssueFact("Players", "server", "players"),
        }).ToArray(),
    };

    public static IssueInput Minimal()
    {
        CapturedSection[] sections =
        {
            Section("build", "{\"version\":\"0.29.3\",\"engineVersion\":\"21.2.0\"}"),
            Section("platform", "{\"os\":\"macOS 26.0\"}"),
        };
        var manifest = new BugReportManifest(1, Id, "grimhollow", BugReportKind.Bug, "Gameplay",
            "Fell through the floor\nnear the mill", CapturedAt,
            sections.Select(s => Entry(BundleEntryNames.Section(s.Key))).ToArray());
        var record = new ReportRecord(1, Id, ReportIds.ReferenceCode(Id), Folder, CapturedAt.AddMinutes(1),
            new ReporterIdentity("discord:42", "Ash", null), manifest, null, ServerSnapshotState.None);
        return new IssueInput(record, sections, null, Array.Empty<string>(), null);
    }

    public static IssueInput Full()
    {
        IssueInput input = WithSection(Minimal(), "graphics", "{\"backend\":\"Metal\",\"adapter\":\"Apple M3\"}");
        input = WithFrames(WithEntry(input, BundleEntryNames.ScreenshotPng), 60);
        return input with
        {
            Report = input.Report with
            {
                Reporter = input.Report.Reporter with { Handle = "ash#0001" },
                ServerSnapshot = ServerSnapshotState.InBody,
            },
            LogTail = new[] { "Connected", "Tick 1234", "Disconnected" },
            Snapshot = Snapshot(new Dictionary<string, JsonElement>
            {
                ["server"] = JsonSerializer.SerializeToElement(new { tick = 1234, players = 2 }),
            }),
        };
    }

    public static CapturedSection Section(string key, string json) => new(key, Encoding.UTF8.GetBytes(json));

    public static BundleEntry Entry(string name) => new(name, 1, new string('a', 64));

    public static IssueInput WithEntry(IssueInput input, string name) => input with
    {
        Report = input.Report with
        {
            Manifest = input.Report.Manifest with { Entries = input.Report.Manifest.Entries.Append(Entry(name)).ToArray() },
        },
    };

    public static IssueInput WithSection(IssueInput input, string key, string json) => WithEntry(input, BundleEntryNames.Section(key)) with
    {
        Sections = input.Sections.Append(Section(key, json)).ToArray(),
    };

    public static IssueInput WithFrames(IssueInput input, int count)
    {
        ClipIndexFrame[] frames = Enumerable.Range(0, count)
            .Select(i => new ClipIndexFrame(i * 100, 16, 960, 540, BundleEntryNames.ClipFrame(i))).ToArray();
        input = WithEntry(input, BundleEntryNames.ClipIndex);
        return input with
        {
            Clip = new ClipIndex(frames),
            Report = input.Report with
            {
                Manifest = input.Report.Manifest with
                {
                    Entries = input.Report.Manifest.Entries.Concat(frames.Select(f => Entry(f.Entry))).ToArray(),
                },
            },
        };
    }

    public static ServerSnapshotWire Snapshot(IReadOnlyDictionary<string, JsonElement> sections) =>
        new(1, Id, "discord:42", CapturedAt.AddSeconds(5), sections);
}
