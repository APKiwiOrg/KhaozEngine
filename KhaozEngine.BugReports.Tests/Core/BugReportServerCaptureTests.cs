using System;
using System.Text;
using System.Text.Json;
using KhaozEngine.BugReports;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class BugReportServerCaptureTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 17, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void TryAccept_allows_five_per_account_per_rolling_hour()
    {
        var time = new ManualTimeProvider(Now);
        var capture = new BugReportServerCapture(time);

        for (int i = 0; i < 5; i++) Assert.True(capture.TryAccept("discord:42"));
        Assert.False(capture.TryAccept("discord:42"));
        Assert.True(capture.TryAccept("discord:43"));
        time.Advance(TimeSpan.FromHours(1));
        Assert.True(capture.TryAccept("discord:42"));
        Assert.True(capture.TryAccept("discord:43"));
    }

    [Fact]
    public void TryAccept_expires_each_accepted_timestamp_without_counting_rejections()
    {
        var time = new ManualTimeProvider(Now);
        var capture = new BugReportServerCapture(time, perAccountPerHour: 2);
        Assert.True(capture.TryAccept("discord:42"));
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.True(capture.TryAccept("discord:42"));
        time.Advance(TimeSpan.FromMinutes(29));
        Assert.False(capture.TryAccept("discord:42"));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(capture.TryAccept("discord:42"));
        Assert.False(capture.TryAccept("discord:42"));
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.True(capture.TryAccept("discord:42"));
        Assert.False(capture.TryAccept("discord:42"));
    }

    [Fact]
    public void Capture_runs_sections_with_the_report_id_and_time()
    {
        var time = new ManualTimeProvider(Now);
        var capture = new BugReportServerCapture(time);
        BugReportContext? observed = null;
        int sectionThread = 0;
        capture.Add(new TestSection((writer, context) =>
        {
            observed = context;
            sectionThread = Environment.CurrentManagedThreadId;
            writer.WriteStartObject();
            writer.WriteNumber("tick", 42);
            writer.WriteEndObject();
        }));
        time.Advance(TimeSpan.FromSeconds(7));
        int callerThread = Environment.CurrentManagedThreadId;

        ServerSnapshot snapshot = capture.Capture(TestBundles.ReportId, "discord:42");

        Assert.Equal(callerThread, sectionThread);
        Assert.Equal(new BugReportContext(TestBundles.ReportId, Now.AddSeconds(7), "discord:42"), observed);
        Assert.Equal(TestBundles.ReportId, snapshot.ReportId);
        Assert.Equal(Now.AddSeconds(7), snapshot.CapturedAt);
        Assert.Equal("world", Assert.Single(snapshot.Sections).Key);
    }

    [Fact]
    public void Capture_passes_the_subject_into_the_context()
    {
        var capture = new BugReportServerCapture(new ManualTimeProvider(Now));
        string? subject = null;
        capture.Add(new TestSection((writer, context) =>
        {
            subject = context.Subject;
            writer.WriteStartObject();
            writer.WriteEndObject();
        }));

        ServerSnapshot snapshot = capture.Capture(TestBundles.ReportId, "discord:42");

        Assert.Equal("discord:42", subject);
        Assert.Equal("discord:42", snapshot.Subject);
    }

    [Fact]
    public void Capture_does_not_redact()
    {
        const string json = "{\"token\":\"Bearer abcdefgh12\"}";
        var capture = new BugReportServerCapture(new ManualTimeProvider(Now));
        capture.Add(new TestSection((writer, _) => writer.WriteRawValue(json)));

        ServerSnapshot snapshot = capture.Capture(TestBundles.ReportId, "discord:42");

        Assert.Equal(Encoding.UTF8.GetBytes(json), Assert.Single(snapshot.Sections).Json.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("0192F1C2A3B47C4D8E9F0A1B2C3D4E5F")]
    [InlineData("0192f1c2a3b46c4d8e9f0a1b2c3d4e5f")]
    public void Capture_rejects_invalid_ids(string reportId)
    {
        var capture = new BugReportServerCapture(new ManualTimeProvider(Now));
        bool ran = false;
        capture.Add(new TestSection((_, _) => ran = true));

        Assert.Throws<ArgumentException>(() => capture.Capture(reportId, "discord:42"));
        Assert.False(ran);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_quota_is_rejected(int quota) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BugReportServerCapture(new ManualTimeProvider(Now), quota));

    sealed class TestSection(Action<Utf8JsonWriter, BugReportContext> write) : IBugReportSection
    {
        public string Key => "world";
        public int MaxBytes => 1024;
        public void Write(Utf8JsonWriter writer, BugReportContext context) => write(writer, context);
    }
}
