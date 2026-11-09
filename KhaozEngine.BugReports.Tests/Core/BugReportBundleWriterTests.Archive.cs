using System;
using System.Collections.Generic;
using System.IO.Compression;
using KhaozEngine.BugReports;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed partial class BugReportBundleWriterTests
{
    [Fact]
    public void Entries_are_stored_not_compressed()
    {
        List<ZipArchiveEntry> entries = Entries(BugReportBundleWriter.Write(Draft(screenshot: Png(), clip: ThreeFrames())));

        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
    }

    [Fact]
    public void Entries_carry_the_capture_time_and_the_same_draft_writes_the_same_bytes()
    {
        BugReportDraft draft = Draft(screenshot: Png(), clip: ThreeFrames());

        byte[] first = BugReportBundleWriter.Write(draft);
        List<ZipArchiveEntry> entries = Entries(first);

        // DOS times keep even seconds, so 05.678 reads back as 04.
        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal(new DateTime(2026, 10, 17, 3, 4, 4), entry.LastWriteTime.DateTime));
        Assert.Equal(first, BugReportBundleWriter.Write(draft));
    }
}
