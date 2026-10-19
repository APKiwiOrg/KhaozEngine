using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

[Collection("AmbientLocalization")]
public sealed class BugReportIdentityTermsTests : BugReportFlowHarness
{
    [Fact]
    public async Task Finished_log_and_game_section_mask_reporter_names_subjects_and_property_names()
    {
        _subject = "discord:12345";
        using BugReportService service = NewService(() => ["Aster Dawn", _subject!]);
        service.RecentLog.Emit(new LogEntry(_time.GetUtcNow(), LogLevel.Info, "sign-in", "Joined Aster Dawn as discord:12345"));
        service.AddSection(new ProbeSection((writer, _) =>
        {
            writer.WriteStartObject();
            writer.WriteString("discord:12345", "Aster Dawn");
            writer.WriteEndObject();
        }, "identity"));

        BugReportSubmission sent = await SendReport(service);

        string log = EntryText(sent, BundleEntryNames.Section("log"));
        Assert.Contains("Joined [redacted] as [redacted]", log, StringComparison.Ordinal);
        using JsonDocument probe = JsonDocument.Parse(EntryText(sent, BundleEntryNames.Section("probe")));
        Assert.Equal("[redacted]", probe.RootElement.GetProperty("subject").GetString());
        using JsonDocument identity = JsonDocument.Parse(EntryText(sent, BundleEntryNames.Section("identity")));
        Assert.Equal("[redacted]", identity.RootElement.GetProperty("[redacted]").GetString());
        Assert.Equal("discord:12345", sent.Subject);
    }

    [Fact]
    public async Task Terms_are_read_once_at_finish_and_account_changes_reach_the_next_report()
    {
        string[] terms = ["before-sign-in"];
        int calls = 0;
        using BugReportService service = NewService(() => { calls++; return terms; });
        Assert.Equal(0, calls);
        _subject = "discord:12345";
        terms = ["discord:12345"];
        service.Flow.FinishStarting = () =>
        {
            Assert.Equal(0, calls);
            terms = ["discord:12345", "Grimhollow"];
        };

        BugReportSubmission first = await SendReport(service);
        Assert.Equal(1, calls);
        Assert.Contains("[redacted]", EntryText(first, BundleEntryNames.Section("probe")), StringComparison.Ordinal);
        Assert.DoesNotContain("Grimhollow", EntryText(first, BundleEntryNames.Section("build")), StringComparison.Ordinal);

        service.Flow.FinishStarting = null;
        _subject = "discord:67890";
        terms = ["discord:67890"];
        BugReportSubmission second = await SendReport(service);

        Assert.Equal(2, calls);
        Assert.Contains("[redacted]", EntryText(second, BundleEntryNames.Section("probe")), StringComparison.Ordinal);
        Assert.DoesNotContain("discord:67890", EntryText(second, BundleEntryNames.Section("probe")), StringComparison.Ordinal);
        Assert.Contains("Grimhollow", EntryText(second, BundleEntryNames.Section("build")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throwing_identity_source_still_sends_with_every_section_string_masked()
    {
        _subject = "discord:12345";
        using BugReportService service = NewService(() => throw new InvalidOperationException("Aster Dawn"));

        BugReportSubmission sent = await SendReport(service);

        foreach (string key in new[] { "build", "platform", "session", "input", "performance", "log", "probe" })
        {
            string json = EntryText(sent, BundleEntryNames.Section(key));
            Assert.DoesNotContain("discord:12345", json, StringComparison.Ordinal);
            using JsonDocument section = JsonDocument.Parse(json);
            Assert.All(section.RootElement.EnumerateObject(), property =>
                Assert.StartsWith("[redacted]", property.Name, StringComparison.Ordinal));
        }
    }

    BugReportService NewService(Func<IEnumerable<string>> source)
    {
        var options = new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            ReporterIdentityTerms = source,
        };
        var recorder = new FrameHistoryRecorder(new FakeCaptureHost(), options.Recording, _time);
        var queue = new BugReportOfflineQueue(Path.Combine(_root, "pending"), _time);
        var service = new BugReportService(options, recorder, _capture, new BugReportSubmitter(_sink, queue),
            () => _subject, HeadlessScreen, _ => true, _time);
        service.AddSection(_probe);
        return service;
    }

    async Task<BugReportSubmission> SendReport(BugReportService service)
    {
        Run(service);
        await SettleService(service);
        Run(service, Frame(pressed: [Key.F10]));
        Run(service, Frame(held: [Key.F10]));
        Run(service, Frame(text: "Fell through"));
        Run(service, Frame(pressed: [Key.Enter], held: [Key.LeftControl]));
        await SettleService(service);
        return _sink.Submissions[^1];
    }

    void Run(BugReportService service, InputState? input = null)
    {
        OnFrame(() => service.Overlay.Update(input ?? Frame(), Dt));
        if (service.Flow.PendingWork is { } work && !_work.Contains(work)) _work.Add(work);
    }

    async Task SettleService(BugReportService service)
    {
        if (service.Flow.PendingWork is { } work) await work.WaitAsync(TimeSpan.FromSeconds(30));
        Run(service);
    }
}
