using System;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D;

/// <summary>Per-thread trimmed names, with round-robin replacement to bound retained text and measurers.</summary>
internal sealed class NameplateEllipsisCache
{
    [ThreadStatic]
    private static NameplateEllipsisCache? _shared;

    private readonly Entry[] _entries = new Entry[32];
    private int _count;
    private int _next;

    internal static NameplateEllipsisCache Shared => _shared ??= new NameplateEllipsisCache();

    internal bool TryGet(ITextMeasurer measurer, string source, float capWidth, float scale, out string trimmed)
    {
        for (int i = 0; i < _count; i++)
        {
            ref readonly Entry entry = ref _entries[i];
            if (ReferenceEquals(entry.Measurer, measurer)
                && string.Equals(entry.Source, source, StringComparison.Ordinal)
                && entry.CapWidth.Equals(capWidth) && entry.Scale.Equals(scale))
            {
                trimmed = entry.Trimmed;
                return true;
            }
        }

        trimmed = string.Empty;
        return false;
    }

    internal void Store(ITextMeasurer measurer, string source, float capWidth, float scale, string trimmed)
    {
        _entries[_next] = new Entry(measurer, source, capWidth, scale, trimmed);
        _next = (_next + 1) % _entries.Length;
        if (_count < _entries.Length) _count++;
    }

    private readonly struct Entry(ITextMeasurer measurer, string source, float capWidth, float scale, string trimmed)
    {
        internal ITextMeasurer Measurer { get; } = measurer;
        internal string Source { get; } = source;
        internal float CapWidth { get; } = capWidth;
        internal float Scale { get; } = scale;
        internal string Trimmed { get; } = trimmed;
    }
}
