using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.Tests.BugReports;

// Builds hand-made report bundles. Every mutator returns the builder and Build() writes a fresh zip,
// recomputing the manifest from the current entries before any manifest edit is applied.
internal sealed class TestBundles
{
    public const string ReportId = "0192f1c2a3b47c4d8e9f0a1b2c3d4e5f";

    readonly List<(string Name, byte[] Data)> entries = [];
    readonly HashSet<string> deflated = new(StringComparer.Ordinal);
    Func<BugReportManifest, BugReportManifest> editManifest = m => m;
    Func<string, string> editManifestJson = j => j;
    bool includeManifest = true;

    public static MemoryStream Valid() => Default().Build();

    public static TestBundles Default() => new TestBundles()
        .WithEntry("sections/build.json", Encoding.UTF8.GetBytes("{\"a\":1}"))
        .WithEntry(BundleEntryNames.ScreenshotPng, Png(4, 4))
        .WithEntry(BundleEntryNames.ClipIndex, ClipIndexJson(
            new ClipIndexFrame(0, 16.5, 4, 4, "clip/000.jpg"),
            new ClipIndexFrame(83.25, 41, 4, 4, "clip/001.jpg")))
        .WithEntry("clip/000.jpg", Jpeg(4, 4))
        .WithEntry("clip/001.jpg", Jpeg(4, 4));

    public TestBundles WithEntry(string name, byte[] data)
    {
        if (name == BundleEntryNames.Manifest)
            throw new ArgumentException("Use WithManifest to change the manifest.", nameof(name));
        int index = entries.FindIndex(e => e.Name == name);
        if (index >= 0) entries[index] = (name, data);
        else entries.Add((name, data));
        return this;
    }

    public TestBundles WithDuplicate(string name)
    {
        entries.Add(entries.First(e => e.Name == name));
        return this;
    }

    public TestBundles WithoutEntry(string name)
    {
        if (name == BundleEntryNames.Manifest) includeManifest = false;
        else entries.RemoveAll(e => e.Name == name);
        return this;
    }

    public TestBundles WithManifest(Func<BugReportManifest, BugReportManifest> edit)
    {
        Func<BugReportManifest, BugReportManifest> previous = editManifest;
        editManifest = m => edit(previous(m));
        return this;
    }

    public TestBundles WithManifestJson(Func<string, string> edit)
    {
        Func<string, string> previous = editManifestJson;
        editManifestJson = j => edit(previous(j));
        return this;
    }

    public TestBundles Deflated(string name)
    {
        deflated.Add(name);
        return this;
    }

    public MemoryStream Build()
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeManifest)
                Write(archive, BundleEntryNames.Manifest, ManifestBytes(), CompressionLevel.NoCompression);
            foreach ((string name, byte[] data) in entries)
                Write(archive, name, data, deflated.Contains(name) ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
        }
        output.Position = 0;
        return output;
    }

    byte[] ManifestBytes()
    {
        var listed = entries.Select(e => new BundleEntry(e.Name, e.Data.Length, Sha256(e.Data))).ToList();
        var manifest = new BugReportManifest(BugReportFormat.Version, ReportId, "grimhollow", BugReportKind.Bug,
            "Gameplay", "Fell through\nthe floor", new DateTimeOffset(2026, 10, 17, 3, 4, 5, 678, TimeSpan.Zero), listed);
        string json = Encoding.UTF8.GetString(BugReportJson.SerializeManifest(editManifest(manifest)));
        return Encoding.UTF8.GetBytes(editManifestJson(json));
    }

    static void Write(ZipArchive archive, string name, byte[] data, CompressionLevel level)
    {
        using Stream stream = archive.CreateEntry(name, level).Open();
        stream.Write(data);
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static byte[] ClipIndexJson(params ClipIndexFrame[] frames) => BugReportJson.SerializeClipIndex(new ClipIndex(frames));

    // Signature and IHDR only. The validator reads headers, so no pixel data is needed.
    public static byte[] Png(int width, int height)
    {
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0, 0, 0, 0, 0, 0, 0x08, 0x06, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        WriteBigEndian(png, 16, width);
        WriteBigEndian(png, 20, height);
        return png;
    }

    // SOI, APP0, SOF0, SOS, EOI. Width and height sit in the SOF0 segment.
    public static byte[] Jpeg(int width, int height)
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0, 0, 0, 0, 0x01, 0x01, 0x11, 0x00,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
            0xFF, 0xD9,
        ];
        jpeg[25] = (byte)(height >> 8);
        jpeg[26] = (byte)height;
        jpeg[27] = (byte)(width >> 8);
        jpeg[28] = (byte)width;
        return jpeg;
    }

    // SOI, APP0, SOS, EOI with no frame header.
    public static byte[] JpegWithoutFrame() =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
        0xFF, 0xD9,
    ];

    static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
