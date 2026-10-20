using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>A report whose media would break the bundle limits is still sent, with less media.</summary>
[Collection("AmbientLocalization")]
public sealed class BugReportFlowFitTests : BugReportFlowHarness
{
    [Fact]
    public async Task Full_clip_with_built_in_sections_still_sends()
    {
        for (int i = 1; i <= 6; i++)
        {
            _sections.Add(new ProbeSection((writer, _) =>
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }, "extra" + i));
        }
        ReportClipFrame first = FakeCapture.DefaultClip(1).Frames[0];
        _capture.Clip = new ReportClip(Enumerable.Range(0, 120)
            .Select(i => first with { Offset = TimeSpan.FromMilliseconds(83 * i), RenderMilliseconds = i })
            .ToArray());
        BugReportFlow flow = await SignedIn();
        Open(flow);

        Send(flow);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        BundleValidationResult validation = BundleValidator.Validate(new MemoryStream(submission.Bundle.ToArray()));
        Assert.True(validation.IsValid, validation.Error);
        Assert.Contains(BundleEntryNames.ScreenshotPng, EntryNames(submission));
        // The manifest, 7 sections, the screenshot and the index leave 118 of the 128 entries for frames.
        Assert.Equal(BugReportLimits.MaxEntries, EntryNames(submission).Length);
        ClipIndex index = BugReportJson.DeserializeClipIndex(System.Text.Encoding.UTF8.GetBytes(
            EntryText(submission, BundleEntryNames.ClipIndex)));
        Assert.Equal(Enumerable.Range(2, 118).Select(i => (double)i), index.Frames.Select(f => f.RenderMilliseconds));
        Assert.Equal(0, index.Frames[0].OffsetMilliseconds);
        Assert.Equal("Bug report sent. Reference " + ReportIds.ReferenceCode(submission.ReportId), Toast(flow));
    }

    [Fact]
    public async Task Oversized_media_falls_back_without_losing_the_report()
    {
        byte[] png = FakeCapture.DefaultScreenshot().Bytes.ToArray();
        byte[] huge = new byte[BugReportLimits.MaxBundleBytes + 1];
        png.CopyTo(huge, 0);
        _capture.Screenshot = () => Task.FromResult(new ReportImage("image/png", FakeCapture.ShotWidth, FakeCapture.ShotHeight, huge));
        BugReportFlow flow = await SignedIn();
        Open(flow);

        Send(flow);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        BundleValidationResult validation = BundleValidator.Validate(new MemoryStream(submission.Bundle.ToArray()));
        Assert.True(validation.IsValid, validation.Error);
        Assert.DoesNotContain(EntryNames(submission), n => n.StartsWith("screenshot", StringComparison.Ordinal));
        Assert.Contains("sections/probe.json", EntryNames(submission));
        Assert.Equal("Bug report sent. Reference " + ReportIds.ReferenceCode(submission.ReportId), Toast(flow));
    }
}
