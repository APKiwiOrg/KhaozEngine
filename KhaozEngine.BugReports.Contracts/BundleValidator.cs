using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace KhaozEngine.BugReports.Contracts;

// Checks a report bundle against the version 1 contract. The relay runs this on untrusted uploads, so
// it never inflates an entry, never reads past an entry's declared length and never throws for any
// input bytes. Checks run in a fixed order and the first failure wins.
public static class BundleValidator
{
    const int MaxGameIdLength = 32;
    const int MaxCategoryLength = 64;
    const int MaxSectionDepth = 64;

    public static BundleValidationResult Validate(Stream zip, long maxBytes = BugReportLimits.MaxBundleBytes)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        Stream source = zip;
        long archiveLength;
        if (zip.CanSeek)
        {
            archiveLength = zip.Length;
            if (archiveLength > maxBytes) return Fail(BundleErrors.TooLarge);
        }
        else
        {
            var copy = new MemoryStream();
            ReadAtMost(zip, copy, maxBytes == long.MaxValue ? maxBytes : maxBytes + 1);
            if (copy.Length > maxBytes) return Fail(BundleErrors.TooLarge);
            copy.Position = 0;
            source = copy;
            archiveLength = copy.Length;
        }

        ZipArchive archive;
        List<ZipArchiveEntry> entries;
        try
        {
            archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            entries = [.. archive.Entries];
        }
        catch (Exception e) when (IsZipFault(e))
        {
            return Fail(BundleErrors.NotAZip);
        }

        using (archive)
        {
            var reader = new EntryReader(archiveLength);
            try
            {
                return ValidateArchive(entries, reader);
            }
            catch (Exception e) when (IsZipFault(e))
            {
                return reader.Current is { } name ? Fail(BundleErrors.NotAZip, name) : Fail(BundleErrors.NotAZip);
            }
        }
    }

    static BundleValidationResult ValidateArchive(List<ZipArchiveEntry> entries, EntryReader reader)
    {
        if (entries.Count > BugReportLimits.MaxEntries) return Fail(BundleErrors.TooManyEntries);

        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in entries)
        {
            string name = entry.FullName;
            if (!BundleEntryNames.IsAllowed(name)) return Fail(BundleErrors.EntryNotAllowed, name);
            if (!byName.TryAdd(name, entry)) return Fail(BundleErrors.DuplicateEntry, name);
            if (entry.IsEncrypted) return Fail(BundleErrors.EncryptedEntry, name);
            if (entry.CompressedLength != entry.Length) return Fail(BundleErrors.CompressedEntry, name);
        }

        if (!byName.TryGetValue(BundleEntryNames.Manifest, out ZipArchiveEntry? manifestEntry))
            return Fail(BundleErrors.ManifestMissing);
        BugReportManifest manifest;
        try
        {
            manifest = BugReportJson.DeserializeManifest(reader.ReadAll(manifestEntry));
        }
        catch (JsonException)
        {
            return Fail(BundleErrors.ManifestInvalid);
        }

        if (CheckFields(manifest) is { } fieldError) return Fail(fieldError);
        if (CheckEntrySet(manifest, entries) is { } mismatch) return Fail(BundleErrors.EntryMismatch, mismatch);

        var listed = new Dictionary<string, BundleEntry>(StringComparer.Ordinal);
        foreach (BundleEntry entry in manifest.Entries) listed.Add(entry.Name, entry);
        foreach (ZipArchiveEntry entry in entries)
        {
            if (entry.FullName == BundleEntryNames.Manifest) continue;
            BundleEntry expected = listed[entry.FullName];
            (long size, string sha256) = reader.Hash(entry);
            if (size != expected.Size) return Fail(BundleErrors.SizeMismatch, entry.FullName);
            if (!string.Equals(sha256, expected.Sha256, StringComparison.Ordinal))
                return Fail(BundleErrors.HashMismatch, entry.FullName);
        }

        var frameSizes = new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in entries)
            if (CheckImage(entry, reader, frameSizes) is false) return Fail(BundleErrors.ImageInvalid, entry.FullName);

        if (CheckSections(entries, reader) is { } sectionError) return Fail(sectionError);

        if (!CheckClip(byName, frameSizes, reader)) return Fail(BundleErrors.ClipIndexInvalid);

        return new BundleValidationResult(true, null, manifest);
    }

    static string? CheckFields(BugReportManifest manifest)
    {
        if (manifest.Entries is null) return BundleErrors.ManifestInvalid;
        foreach (BundleEntry? entry in manifest.Entries)
            if (entry?.Name is null || entry.Sha256 is null) return BundleErrors.ManifestInvalid;
        if (manifest.FormatVersion != BugReportFormat.Version) return BundleErrors.FormatVersion;
        if (!ReportIds.IsValid(manifest.ReportId)) return BundleErrors.ReportIdInvalid;
        if (!IsValidGameId(manifest.GameId)) return BundleErrors.GameIdInvalid;
        if (string.IsNullOrWhiteSpace(manifest.Description) || manifest.Description.Length > BugReportLimits.MaxDescriptionChars)
            return BundleErrors.DescriptionInvalid;
        if (manifest.Category is null || manifest.Category.Length is < 1 or > MaxCategoryLength)
            return BundleErrors.CategoryInvalid;
        if (!Enum.IsDefined(manifest.Kind)) return BundleErrors.ManifestInvalid;
        return null;
    }

    // Returns the first name listed but absent, or present but unlisted. A name listed twice, or the
    // manifest listing itself, is a mismatch too.
    static string? CheckEntrySet(BugReportManifest manifest, List<ZipArchiveEntry> entries)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in entries)
            if (entry.FullName != BundleEntryNames.Manifest) present.Add(entry.FullName);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (BundleEntry entry in manifest.Entries)
            if (!present.Contains(entry.Name) || !listed.Add(entry.Name)) return entry.Name;
        foreach (string name in present)
            if (!listed.Contains(name)) return name;
        return null;
    }

    static bool IsValidGameId(string gameId)
    {
        if (gameId is null || gameId.Length is < 1 or > MaxGameIdLength) return false;
        if (gameId[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9')) return false;
        foreach (char c in gameId)
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
        return true;
    }

    // Null for entries that are not images, otherwise whether the header parses with both edges in
    // 1 to 2560. Clip frame sizes are recorded for the index check.
    static bool? CheckImage(ZipArchiveEntry entry, EntryReader reader, Dictionary<string, (int, int)> frameSizes)
    {
        string name = entry.FullName;
        bool png = name == BundleEntryNames.ScreenshotPng;
        bool frame = name.StartsWith("clip/", StringComparison.Ordinal) && name.EndsWith(".jpg", StringComparison.Ordinal);
        if (!png && !frame && name != BundleEntryNames.ScreenshotJpeg) return null;

        byte[] data = reader.ReadAll(entry);
        int width, height;
        bool parsed = png ? ImageHeaders.TryReadPng(data, out width, out height) : ImageHeaders.TryReadJpeg(data, out width, out height);
        if (!parsed || !IsValidEdge(width) || !IsValidEdge(height)) return false;
        if (frame) frameSizes[name] = (width, height);
        return true;
    }

    static bool IsValidEdge(int edge) => edge is >= 1 and <= BugReportLimits.MaxScreenshotLongEdge;

    // The running total is checked before each section is read, so oversized state is never parsed.
    static string? CheckSections(List<ZipArchiveEntry> entries, EntryReader reader)
    {
        var options = new JsonDocumentOptions { MaxDepth = MaxSectionDepth };
        long total = 0;
        foreach (ZipArchiveEntry entry in entries)
        {
            if (!entry.FullName.StartsWith("sections/", StringComparison.Ordinal)) continue;
            total += entry.Length;
            if (total > BugReportLimits.MaxSectionBytesTotal) return BundleErrors.SectionsTooLarge;
            try
            {
                using JsonDocument document = JsonDocument.Parse(reader.ReadAll(entry), options);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return BundleErrors.SectionInvalid + ": " + entry.FullName;
            }
            catch (JsonException)
            {
                return BundleErrors.SectionInvalid + ": " + entry.FullName;
            }
        }
        return null;
    }

    static bool CheckClip(Dictionary<string, ZipArchiveEntry> byName, Dictionary<string, (int Width, int Height)> frameSizes,
        EntryReader reader)
    {
        bool hasMp4 = byName.ContainsKey(BundleEntryNames.ClipMp4);
        if (!byName.TryGetValue(BundleEntryNames.ClipIndex, out ZipArchiveEntry? indexEntry))
            return frameSizes.Count == 0 && !hasMp4;

        ClipIndex index;
        try
        {
            index = BugReportJson.DeserializeClipIndex(reader.ReadAll(indexEntry));
        }
        catch (JsonException)
        {
            return false;
        }
        if (index.Frames is null || index.Frames.Count > BundleEntryNames.MaxClipFrames) return false;

        int referenced = 0;
        double previousOffset = 0;
        for (int i = 0; i < index.Frames.Count; i++)
        {
            ClipIndexFrame? frame = index.Frames[i];
            if (frame?.Entry is null) return false;
            if (!double.IsFinite(frame.OffsetMilliseconds) || frame.OffsetMilliseconds < previousOffset) return false;
            if (!double.IsFinite(frame.RenderMilliseconds) || frame.RenderMilliseconds < 0) return false;
            previousOffset = frame.OffsetMilliseconds;

            if (hasMp4 && frame.Entry == BundleEntryNames.ClipMp4) continue;
            if (frame.Entry != BundleEntryNames.ClipFrame(i) || !frameSizes.TryGetValue(frame.Entry, out var size)) return false;
            if (size.Width != frame.Width || size.Height != frame.Height) return false;
            referenced++;
        }
        return referenced == frameSizes.Count;
    }

    static bool IsZipFault(Exception e)
        => e is InvalidDataException or IOException or NotSupportedException or ArgumentException or OverflowException;

    static BundleValidationResult Fail(string code) => new(false, code, null);

    static BundleValidationResult Fail(string code, string entryName) => new(false, code + ": " + entryName, null);

    static long ReadAtMost(Stream source, Stream destination, long limit)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (total < limit)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - total));
            if (read == 0) break;
            destination.Write(buffer, 0, read);
            total += read;
        }
        return total;
    }

    // Reads stored entries. No read goes past the entry's declared length plus one byte, or past the
    // archive's own length, whichever is smaller. Current names the entry being read for fault reports.
    sealed class EntryReader(long archiveLength)
    {
        public string? Current { get; private set; }

        public byte[] ReadAll(ZipArchiveEntry entry)
        {
            Current = entry.FullName;
            using Stream stream = entry.Open();
            var buffer = new MemoryStream();
            ReadAtMost(stream, buffer, Limit(entry));
            Current = null;
            return buffer.ToArray();
        }

        public (long Size, string Sha256) Hash(ZipArchiveEntry entry)
        {
            Current = entry.FullName;
            using Stream stream = entry.Open();
            using var hash = new HashingSink();
            long size = ReadAtMost(stream, hash, Limit(entry));
            Current = null;
            return (size, hash.Finish());
        }

        long Limit(ZipArchiveEntry entry) => Math.Min(entry.Length, archiveLength) + 1;
    }

    sealed class HashingSink : Stream
    {
        readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public string Finish() => Convert.ToHexStringLower(hash.GetHashAndReset());

        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
