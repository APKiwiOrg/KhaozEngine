using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.BugReports.Contracts;

// Checks a report bundle against the version 1 contract. The relay runs this on untrusted uploads, so
// it never throws for any input bytes and never reads more than an entry's declared length plus one
// byte. Entries must be stored, which is checked as equal compressed and uncompressed lengths. A
// crafted compressed entry that passes that test is decompressed only up to the same bound. Checks
// run in a fixed order and the first failure wins.
public static class BundleValidator
{
    const int MaxManifestBytes = 64 * 1024;
    const int MaxClipIndexBytes = 64 * 1024;

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
            if (ZipDirectoryScan.Check(source, archiveLength, BugReportLimits.MaxEntries) is { } directoryError)
                return Fail(directoryError);
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
                return ValidateArchive(entries, reader, archiveLength);
            }
            catch (Exception e) when (IsZipFault(e))
            {
                return reader.Current is { } name ? Fail(BundleErrors.NotAZip, name) : Fail(BundleErrors.NotAZip);
            }
        }
    }

    static BundleValidationResult ValidateArchive(List<ZipArchiveEntry> entries, EntryReader reader, long archiveLength)
    {
        if (entries.Count > BugReportLimits.MaxEntries) return Fail(BundleErrors.TooManyEntries);

        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in entries)
        {
            string name = entry.FullName;
            if (!BundleEntryNames.IsAllowed(name)) return Fail(BundleErrors.EntryNotAllowed, name);
            if (!byName.TryAdd(name, entry)) return Fail(BundleErrors.DuplicateEntry, name);
            if (name is BundleEntryNames.ScreenshotPng or BundleEntryNames.ScreenshotJpeg
                && byName.ContainsKey(BundleEntryNames.ScreenshotPng) && byName.ContainsKey(BundleEntryNames.ScreenshotJpeg))
                return Fail(BundleErrors.DuplicateEntry, BundleEntryNames.ScreenshotJpeg);
            if (entry.IsEncrypted) return Fail(BundleErrors.EncryptedEntry, name);
            if (entry.CompressedLength != entry.Length) return Fail(BundleErrors.CompressedEntry, name);
        }

        // Stored entries occupy disjoint bytes, so their lengths can never sum past the archive. Records
        // sharing one local entry would otherwise multiply the hashing work below.
        long stored = 0;
        foreach (ZipArchiveEntry entry in entries)
        {
            if (entry.Length < 0 || entry.Length > archiveLength - stored) return Fail(BundleErrors.OverlappingEntries);
            stored += entry.Length;
        }

        if (!byName.TryGetValue(BundleEntryNames.Manifest, out ZipArchiveEntry? manifestEntry))
            return Fail(BundleErrors.ManifestMissing);
        if (manifestEntry.Length > MaxManifestBytes) return Fail(BundleErrors.ManifestInvalid);
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

        if (byName.TryGetValue(BundleEntryNames.ClipMp4, out ZipArchiveEntry? mp4) && !IsMp4(reader.ReadPrefix(mp4, 8)))
            return Fail(BundleErrors.MediaInvalid, BundleEntryNames.ClipMp4);

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
        if (!BundleEntryNames.IsValidGameId(manifest.GameId)) return BundleErrors.GameIdInvalid;
        if (string.IsNullOrWhiteSpace(manifest.Description) || manifest.Description.Length > BugReportLimits.MaxDescriptionChars)
            return BundleErrors.DescriptionInvalid;
        if (manifest.Category is null || manifest.Category.Length is < 1 or > BugReportLimits.MaxCategoryChars)
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

    // Null for entries that are not images, otherwise whether the header parses with both edges in
    // 1 to 2560. Clip frame sizes are recorded for the index check.
    static bool? CheckImage(ZipArchiveEntry entry, EntryReader reader, Dictionary<string, (int, int)> frameSizes)
    {
        string name = entry.FullName;
        bool png = name == BundleEntryNames.ScreenshotPng;
        bool frame = name.StartsWith("clip/", StringComparison.Ordinal) && name.EndsWith(".jpg", StringComparison.Ordinal);
        if (!png && !frame && name != BundleEntryNames.ScreenshotJpeg) return null;

        byte[] data = reader.ReadAll(entry);
        string mediaType = png ? ImageHeaders.PngMediaType : ImageHeaders.JpegMediaType;
        if (!ImageHeaders.TryReadSize(mediaType, data, out int width, out int height) || !IsValidEdge(width) || !IsValidEdge(height))
            return false;
        if (frame) frameSizes[name] = (width, height);
        return true;
    }

    static bool IsMp4(byte[] prefix) => prefix.Length == 8 && prefix.AsSpan(4).SequenceEqual("ftyp"u8);

    static bool IsValidEdge(int edge) => edge is >= 1 and <= BugReportLimits.MaxScreenshotLongEdge;

    // The running total is checked before each section is read, so oversized state is never parsed.
    static string? CheckSections(List<ZipArchiveEntry> entries, EntryReader reader)
    {
        long total = 0;
        foreach (ZipArchiveEntry entry in entries)
        {
            if (!entry.FullName.StartsWith("sections/", StringComparison.Ordinal)) continue;
            total += entry.Length;
            if (total > BugReportLimits.MaxSectionBytesTotal) return BundleErrors.SectionsTooLarge;
            if (!BugReportJson.IsValidSection(reader.ReadAll(entry))) return Coded(BundleErrors.SectionInvalid, entry.FullName);
        }
        return null;
    }

    static bool CheckClip(Dictionary<string, ZipArchiveEntry> byName, Dictionary<string, (int Width, int Height)> frameSizes,
        EntryReader reader)
    {
        bool hasMp4 = byName.ContainsKey(BundleEntryNames.ClipMp4);
        if (!byName.TryGetValue(BundleEntryNames.ClipIndex, out ZipArchiveEntry? indexEntry))
            return frameSizes.Count == 0 && !hasMp4;

        if (indexEntry.Length > MaxClipIndexBytes) return false;
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
        if (hasMp4 && frameSizes.Count > 0) return false;

        int referenced = 0;
        double previousOffset = 0;
        for (int i = 0; i < index.Frames.Count; i++)
        {
            ClipIndexFrame? frame = index.Frames[i];
            if (frame?.Entry is null) return false;
            if (!double.IsFinite(frame.OffsetMilliseconds) || frame.OffsetMilliseconds < previousOffset
                || frame.OffsetMilliseconds > BugReportLimits.MaxClipOffsetMilliseconds) return false;
            if (!double.IsFinite(frame.RenderMilliseconds) || frame.RenderMilliseconds is < 0 or > BugReportLimits.MaxRenderMilliseconds)
                return false;
            previousOffset = frame.OffsetMilliseconds;

            if (hasMp4)
            {
                if (frame.Entry != BundleEntryNames.ClipMp4) return false;
                if (!IsValidEdge(frame.Width) || !IsValidEdge(frame.Height)) return false;
                continue;
            }
            if (frame.Entry != BundleEntryNames.ClipFrame(i) || !frameSizes.TryGetValue(frame.Entry, out var size)) return false;
            if (size.Width != frame.Width || size.Height != frame.Height) return false;
            referenced++;
        }
        return referenced == frameSizes.Count;
    }

    static bool IsZipFault(Exception e)
        => e is InvalidDataException or IOException or NotSupportedException or ArgumentException or OverflowException;

    static BundleValidationResult Fail(string code) => new(false, code, null);

    static BundleValidationResult Fail(string code, string entryName) => new(false, Coded(code, entryName), null);

    static string Coded(string code, string entryName) => code + ": " + SafeName(entryName);

    // Entry names come from the upload. Control, format, line separator and paragraph separator
    // characters become '?', as do unpaired surrogates. Names over 64 characters are cut to 61 plus
    // "...", and a pair split by the cut becomes '?'.
    internal static string SafeName(string name)
    {
        const int MaxLength = 64;
        const int Kept = MaxLength - 3;
        bool cut = name.Length > MaxLength;
        int length = cut ? Kept : name.Length;
        Span<char> buffer = stackalloc char[MaxLength];
        for (int i = 0; i < length; i++)
        {
            char c = name[i];
            if (char.IsHighSurrogate(c) && i + 1 < length && char.IsLowSurrogate(name[i + 1]))
            {
                bool hidden = IsHidden(Rune.GetUnicodeCategory(new Rune(c, name[i + 1])));
                buffer[i] = hidden ? '?' : c;
                buffer[i + 1] = hidden ? '?' : name[i + 1];
                i++;
                continue;
            }
            buffer[i] = char.IsSurrogate(c) || IsHidden(char.GetUnicodeCategory(c)) ? '?' : c;
        }
        if (cut) "...".AsSpan().CopyTo(buffer[Kept..]);
        return new string(buffer[..(cut ? MaxLength : length)]);
    }

    static bool IsHidden(UnicodeCategory category) => category is UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

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

        public byte[] ReadPrefix(ZipArchiveEntry entry, int count)
        {
            Current = entry.FullName;
            using Stream stream = entry.Open();
            var buffer = new MemoryStream();
            ReadAtMost(stream, buffer, count);
            Current = null;
            return buffer.ToArray();
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
