using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class TelemetryRecorderTests
{
    static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ke-telemetry-tests", Guid.NewGuid().ToString("N") + ".jsonl");

    static string[] ReadLinesShared(string path)
    {
        // Read while the writer may still hold the file open (crash-safety check): share read+write.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) != null)
            if (line.Length > 0) lines.Add(line);
        return lines.ToArray();
    }

    [Fact]
    public void Lifecycle_flags_reflect_start_and_stop()
    {
        var rec = new TelemetryRecorder();
        Assert.False(rec.IsRecording);
        Assert.Null(rec.CurrentPath);

        string path = TempPath();
        rec.Start(path);
        Assert.True(rec.IsRecording);
        Assert.Equal(path, rec.CurrentPath);

        rec.Stop();
        Assert.False(rec.IsRecording);
        Assert.Null(rec.CurrentPath);
    }

    [Fact]
    public void Each_sample_is_a_parseable_jsonl_object_with_t_and_channels()
    {
        string path = TempPath();
        var rec = new TelemetryRecorder();
        rec.Start(path);
        rec.Sample(0.0, new[] { new TelemetryChannel("fps", 59.7), new TelemetryChannel("rttMs", 48) });
        rec.Sample(0.5, new[] { new TelemetryChannel("fps", 60.0), new TelemetryChannel("rttMs", 50) });
        rec.Stop();

        string[] lines = ReadLinesShared(path);
        Assert.Equal(2, lines.Length);

        using JsonDocument d0 = JsonDocument.Parse(lines[0]);
        Assert.Equal(0.0, d0.RootElement.GetProperty("t").GetDouble());
        Assert.Equal(59.7, d0.RootElement.GetProperty("fps").GetDouble(), 3);
        Assert.Equal(48, d0.RootElement.GetProperty("rttMs").GetDouble());

        using JsonDocument d1 = JsonDocument.Parse(lines[1]);
        Assert.Equal(0.5, d1.RootElement.GetProperty("t").GetDouble());
        Assert.Equal(60.0, d1.RootElement.GetProperty("fps").GetDouble(), 3);
    }

    [Fact]
    public void Lines_are_flushed_per_sample_so_a_partial_file_is_valid_without_stop()
    {
        string path = TempPath();
        var rec = new TelemetryRecorder();
        rec.Start(path);
        rec.Sample(1.0, new[] { new TelemetryChannel("x", 1) });
        rec.Sample(2.0, new[] { new TelemetryChannel("x", 2) });
        // No Stop() — simulate a crash; the flushed lines must already parse.

        string[] lines = ReadLinesShared(path);
        Assert.Equal(2, lines.Length);
        foreach (string line in lines)
            using (JsonDocument.Parse(line)) { } // throws if any line is truncated/invalid

        rec.Stop();
    }

    [Fact]
    public void Nonfinite_channel_values_serialize_as_json_null()
    {
        string path = TempPath();
        var rec = new TelemetryRecorder();
        rec.Start(path);
        rec.Sample(0.0, new[] { new TelemetryChannel("nan", double.NaN), new TelemetryChannel("inf", double.PositiveInfinity) });
        rec.Stop();

        string[] lines = ReadLinesShared(path);
        using JsonDocument d = JsonDocument.Parse(lines[0]); // must still be valid JSON
        Assert.Equal(JsonValueKind.Null, d.RootElement.GetProperty("nan").ValueKind);
        Assert.Equal(JsonValueKind.Null, d.RootElement.GetProperty("inf").ValueKind);
    }

    [Fact]
    public void Sample_before_start_is_a_noop()
    {
        var rec = new TelemetryRecorder();
        rec.Sample(0.0, new[] { new TelemetryChannel("x", 1) }); // must not throw
        Assert.False(rec.IsRecording);
    }

    [Fact]
    public void Channel_names_with_quotes_are_escaped()
    {
        string path = TempPath();
        var rec = new TelemetryRecorder();
        rec.Start(path);
        rec.Sample(0.0, new[] { new TelemetryChannel("a\"b", 1) });
        rec.Stop();

        string[] lines = ReadLinesShared(path);
        using JsonDocument d = JsonDocument.Parse(lines[0]); // throws if escaping is wrong
        Assert.Equal(1, d.RootElement.GetProperty("a\"b").GetDouble());
    }
}
