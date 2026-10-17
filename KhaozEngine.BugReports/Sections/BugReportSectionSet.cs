using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports;

/// <summary>
/// The sections a report carries, captured in two steps. <see cref="CaptureAll"/> runs on the thread that triggers
/// the report and only isolates: each section writes into its own capped buffer, and a section that throws, writes
/// more than 4 MiB, leaves a container open or writes anything but one object becomes an error object.
/// <see cref="Finish"/> runs off that thread and does the slow work: redaction, the error message cut, structural
/// truncation and the 1 MiB section total. Add sections and capture from one thread.
/// </summary>
public sealed class BugReportSectionSet
{
    /// <summary>The smallest budget a section may have. A section left with less is dropped.</summary>
    public const int MinSectionBytes = 64;

    /// <summary>How long <see cref="Finish"/> may spend redacting before it masks every remaining string.</summary>
    public static readonly TimeSpan DefaultFinishBudget = TimeSpan.FromSeconds(2);

    /// <summary>The most bytes one section may write during capture before it becomes an error object.</summary>
    internal const int MaxRawSectionBytes = 4 * 1024 * 1024;

    /// <summary>How much of a failing section's exception message capture keeps.</summary>
    const int MaxRawErrorMessage = 8192;

    /// <summary>How much of an error message survives <see cref="Finish"/>.</summary>
    const int MaxFinishedErrorMessage = 512;

    const string NotOneObject = "Section output must be one JSON object.";

    readonly List<Entry> entries = [];
    readonly List<string> keys = [];

    /// <summary>Creates an empty set.</summary>
    public BugReportSectionSet()
    {
        Keys = keys.AsReadOnly();
    }

    /// <summary>The registered keys in registration order.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Registers <paramref name="section"/>. Its key and budget are read once, here.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="section"/> is null.</exception>
    /// <exception cref="ArgumentException">The key is not a valid section key or is already registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The budget is under <see cref="MinSectionBytes"/>.</exception>
    public void Add(IBugReportSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        string key = section.Key;
        int maxBytes = section.MaxBytes;
        if (!BundleEntryNames.IsValidSectionKey(key))
            throw new ArgumentException("A section key must match [a-z0-9][a-z0-9._-]{0,47}.", nameof(section));
        if (keys.Contains(key))
            throw new ArgumentException("A section with key '" + key + "' is already registered.", nameof(section));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, MinSectionBytes, nameof(section) + "." + nameof(section.MaxBytes));
        entries.Add(new Entry(key, maxBytes, section));
        keys.Add(key);
    }

    /// <summary>
    /// Runs every section in registration order and returns what each wrote, unredacted and untruncated. Never
    /// throws for a section's fault, and never redacts. Pass the result to <see cref="Finish"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public IReadOnlyList<RawSection> CaptureAll(BugReportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var raw = new RawSection[entries.Count];
        for (int i = 0; i < raw.Length; i++)
            raw[i] = Capture(entries[i], context);
        return Array.AsReadOnly(raw);
    }

    /// <summary>
    /// Turns raw sections into finished ones, in input order. Every property name and string value passes through
    /// <paramref name="redactor"/>. Once <paramref name="budget"/> has elapsed every remaining string becomes
    /// <see cref="ReportRedactor.Mask"/>, and numbers, booleans and nulls are kept. A name that repeats after that
    /// gets a <c>#n</c> suffix. An error message is cut to 512 UTF-16 units after redaction. Each section is then cut
    /// to the smaller of its own budget and what is left of the 1 MiB total, and dropped with a warning when that is
    /// under <see cref="MinSectionBytes"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="raw"/>, an entry or <paramref name="redactor"/> is null.</exception>
    public static IReadOnlyList<CapturedSection> Finish(IReadOnlyList<RawSection> raw, ReportRedactor redactor, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(redactor);
        Stopwatch clock = Stopwatch.StartNew();
        string Redact(string text) => clock.Elapsed >= budget ? ReportRedactor.Mask : redactor.Redact(text);

        var finished = new List<CapturedSection>(raw.Count);
        int used = 0;
        foreach (RawSection section in raw)
        {
            ArgumentNullException.ThrowIfNull(section, nameof(raw));
            int sectionBudget = Math.Min(section.MaxBytes, BugReportLimits.MaxSectionBytesTotal - used);
            // Checked before redacting so a dropped section spends none of the redaction budget.
            if (sectionBudget < MinSectionBytes)
            {
                Log.Warn("Bug report section '" + section.Key + "' dropped: only "
                    + sectionBudget.ToString(CultureInfo.InvariantCulture) + " bytes of its budget remain.");
                continue;
            }

            bool isError = section.IsError;
            byte[]? json = SectionJson.Redact(section.Json.Span, Redact);
            if (json is null)
            {
                json = SectionJson.ErrorObject(new InvalidOperationException(NotOneObject), MaxRawErrorMessage);
                isError = true;
            }
            if (isError) json = SectionJson.CutErrorMessage(json, MaxFinishedErrorMessage);
            json = SectionJson.Truncate(json, sectionBudget);
            used += json.Length;
            finished.Add(new CapturedSection(section.Key, json));
        }
        return finished.AsReadOnly();
    }

    static RawSection Capture(Entry entry, BugReportContext context)
    {
        var output = new CappedBufferWriter(MaxRawSectionBytes);
        try
        {
            using (var writer = new Utf8JsonWriter(output, SectionJson.WriterOptions))
            {
                entry.Section.Write(writer, context);
                writer.Flush();
                if (writer.CurrentDepth != 0) return Error(entry, new InvalidOperationException("Section left a JSON container open."));
            }
            ReadOnlySpan<byte> json = output.WrittenSpan;
            if (json.IsEmpty || json[0] != (byte)'{') return Error(entry, new InvalidOperationException(NotOneObject));
            return new RawSection(entry.Key, entry.MaxBytes, json.ToArray(), IsError: false);
        }
        catch (Exception exception)
        {
            return Error(entry, exception);
        }
    }

    static RawSection Error(Entry entry, Exception exception) =>
        new(entry.Key, entry.MaxBytes, SectionJson.ErrorObject(exception, MaxRawErrorMessage), IsError: true);

    readonly record struct Entry(string Key, int MaxBytes, IBugReportSection Section);
}
