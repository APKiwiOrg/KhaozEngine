using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary><see cref="ReportBundleBuilder"/> driven directly, past what a registered section set allows.</summary>
public sealed class ReportBundleBuilderTests
{
    static ReportBundleBuilder.Request Request(int sections, ReportImage? screenshot, ReportClip? clip) =>
        new(ReportIds.NewId(), "grimhollow", new DateTimeOffset(2026, 10, 17, 3, 4, 5, TimeSpan.Zero),
            Enumerable.Range(0, sections).Select(i => new CapturedSection("s" + i, Encoding.UTF8.GetBytes("{}"))).ToArray(),
            "Fell through", "Other", screenshot, clip);

    static string[] Names(byte[] bundle)
    {
        using var zip = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        return zip.Entries.Select(entry => entry.FullName).ToArray();
    }

    [Theory]
    [InlineData(125)]
    [InlineData(126)]
    [InlineData(127)]
    public void Fitter_with_no_room_for_frames_sends_without_the_clip(int sections)
    {
        byte[] bundle = ReportBundleBuilder.Write(Request(sections, FakeCapture.DefaultScreenshot(), FakeCapture.DefaultClip()));

        BundleValidationResult validation = BundleValidator.Validate(new MemoryStream(bundle));
        Assert.True(validation.IsValid, validation.Error);
        Assert.DoesNotContain(Names(bundle), name => name.StartsWith("clip/", StringComparison.Ordinal));
        Assert.Null(ReportBundleBuilder.FitClip(FakeCapture.DefaultClip(), Request(sections, FakeCapture.DefaultScreenshot(), null).Sections,
            FakeCapture.DefaultScreenshot()));
    }
}
