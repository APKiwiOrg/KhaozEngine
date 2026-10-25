using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Payload publication completes before its completion instant can be known. Reserve two numeric
// slots so recording that instant never serializes or writes the raw frame payload a second time.
internal sealed class RendererPerfPublicationFiles(string directory)
{
    const int NumberWidth = 32;
    RendererPerfUnitBudget? _budget;
    internal void SetBudget(RendererPerfUnitBudget budget) => _budget = budget;
    internal readonly record struct Slot(string Name, int Start, int End);
    Slot[] _slots = [];
    string? _finalPath;

    internal void Write(RendererPerfReport report, bool final)
    {
        byte[] json = RendererPerfReportJson.Serialize(report);
        string stem = Path.Combine(directory, report.Manifest.UnitId + (final ? "" : ".partial"));
        if (final && report.Publication.FinalUnitPublished)
        {
            (json, _slots) = Reserve(json);
            _finalPath = stem + ".json";
        }
        Atomic(stem + ".json", json, final && report.Publication.FinalUnitPublished ? _budget : null);
        string markdown = $"# Renderer performance unit {report.Manifest.UnitId}\n\n"
            + $"State {report.Publication.State}. Promotion {report.Assessment.ValidForPromotion}.\n\n"
            + $"Blocks {report.Blocks.Length}/{report.Manifest.Blocks.Length}. Controls {report.Controls.Length}/{report.Manifest.Controls.Length}.\n\n"
            + $"{report.Publication.OriginalFailure}\n" + string.Join("\n", report.Assessment.Reasons) + "\n";
        Atomic(stem + ".md", Encoding.UTF8.GetBytes(markdown), final && report.Publication.FinalUnitPublished ? _budget : null);
    }

    internal void RecordCompletion(RendererPerfReport report)
    {
        if (_finalPath is null || _slots.Length != 2) throw new InvalidOperationException("No final payload was published.");
        using var stream = new FileStream(_finalPath, FileMode.Open, FileAccess.Write, FileShare.None);
        Span<byte> number = stackalloc byte[NumberWidth];
        foreach (var slot in _slots)
        {
            _budget?.Check("renderer.publication.slot.before");
            double value = (slot.Name == "elapsedMilliseconds" ? report.Publication.ElapsedMilliseconds
                : report.Publication.EstimatedCostUsd).Value ?? throw new InvalidOperationException("Publication accounting is unavailable.");
            if (!double.IsFinite(value)) throw new InvalidOperationException("Publication accounting is nonfinite.");
            number.Fill((byte)' ');
            int length = Encoding.UTF8.GetBytes(value.ToString("R", CultureInfo.InvariantCulture), number);
            if (length > NumberWidth) throw new InvalidOperationException("Publication accounting exceeds its numeric slot.");
            stream.Position = slot.Start;
            stream.Write(number);
            _budget?.Check("renderer.publication.slot.after");
        }
        stream.Flush();
    }

    internal static (byte[] Bytes, Slot[] Slots) Reserve(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var slots = new List<Slot>();
        bool publication = false, value = false;
        string? metric = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.CurrentDepth == 1) publication = reader.ValueTextEquals("publication");
                if (publication && reader.CurrentDepth == 2) metric = reader.GetString();
                value = publication && reader.CurrentDepth == 3 && reader.ValueTextEquals("value")
                    && metric is "elapsedMilliseconds" or "estimatedCostUsd";
            }
            else if (value)
            {
                if (reader.TokenType != JsonTokenType.Number) throw new InvalidOperationException("Publication accounting must be numeric.");
                slots.Add(new(metric!, checked((int)reader.TokenStartIndex), checked((int)reader.BytesConsumed)));
                value = false;
            }
        }
        if (slots.Count != 2) throw new InvalidOperationException("Publication accounting slots are missing.");
        int padding = 0;
        foreach (var slot in slots) padding += NumberWidth - (slot.End - slot.Start);
        if (bytes.Length + padding > RendererPerfReportJson.MaximumBytes)
            throw new RendererPerfProtocolException("Json", "Publication accounting exceeds the fixed report byte cap.");
        byte[] result = new byte[bytes.Length + padding];
        int from = 0, to = 0;
        var reserved = new List<Slot>();
        foreach (var slot in slots)
        {
            int count = slot.End - from;
            bytes.AsSpan(from, count).CopyTo(result.AsSpan(to));
            int start = to + slot.Start - from;
            to += count;
            int spaces = NumberWidth - (slot.End - slot.Start);
            result.AsSpan(to, spaces).Fill((byte)' ');
            to += spaces;
            from = slot.End;
            reserved.Add(new(slot.Name, start, start + NumberWidth));
        }
        bytes.AsSpan(from).CopyTo(result.AsSpan(to));
        return (result, [.. reserved]);
    }

    internal static void Atomic(string path, byte[] bytes, RendererPerfUnitBudget? budget = null)
    {
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            for (int offset = 0; offset < bytes.Length;)
            {
                budget?.Check("renderer.publication.chunk.before");
                int count = Math.Min(65536, bytes.Length - offset);
                stream.Write(bytes.AsSpan(offset, count)); offset = checked(offset + count);
                budget?.Check("renderer.publication.chunk.after");
            }
            stream.Flush();
        }
        budget?.Check("renderer.publication.rename.before");
        File.Move(temporary, path, overwrite: true);
        budget?.Check("renderer.publication.rename.after");
    }
}
