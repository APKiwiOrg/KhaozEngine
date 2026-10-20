using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Imaging;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal static class TestBundles
{
    static readonly byte[] Png = PngWriter.Encode(new byte[16 * 16 * 4], 16, 16);
    static readonly byte[] Jpeg = JpegWriter.Encode(new byte[16 * 16 * 4], 16, 16, 92);

    public static byte[] Valid(string reportId, string gameId = "grimhollow")
    {
        var sections = new[]
        {
            new CapturedSection("build", "{\"version\":\"0.12.3\"}"u8.ToArray()),
            new CapturedSection("platform", "{\"os\":\"macOS\"}"u8.ToArray()),
            new CapturedSection("log", "{\"lines\":[\"connected\",\"tick 42\"]}"u8.ToArray()),
        };
        var clip = new ReportClip(Enumerable.Range(0, 3)
            .Select(i => new ReportClipFrame(TimeSpan.FromMilliseconds(i * 100), 16, 16, 16, Jpeg)).ToArray());
        return BugReportBundleWriter.Write(new BugReportDraft(reportId, gameId, BugReportKind.Bug,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), sections,
            new ReportImage("image/png", 16, 16, Png), clip)
        {
            Description = "Fell through the floor",
            Category = "Gameplay"
        });
    }

    public static byte[] WithEntryBytes(byte[] bundle, string name, byte[] bytes)
    {
        using var source = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream input = entry.Open();
                using var data = new MemoryStream();
                input.CopyTo(data);
                byte[] content = data.ToArray();
                if (entry.FullName == name) content = bytes;
                if (entry.FullName == BundleEntryNames.Manifest)
                {
                    JsonNode manifest = JsonNode.Parse(content)!;
                    JsonNode row = manifest["entries"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == name)!;
                    row["size"] = bytes.Length;
                    row["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    content = Encoding.UTF8.GetBytes(manifest.ToJsonString());
                }
                using Stream target = zip.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open();
                target.Write(content);
            }
        }
        return output.ToArray();
    }

    public static byte[] Traversal()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = zip.CreateEntry("../escape.json", CompressionLevel.NoCompression).Open();
            entry.Write("{}"u8);
        }
        return output.ToArray();
    }
}
