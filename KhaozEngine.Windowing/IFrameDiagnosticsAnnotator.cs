using System;

namespace KhaozEngine.Windowing;

/// <summary>Private recorder seam. Register once before Run. Sampling runs on the frame thread at the opening
/// boundary, before prepare or update, and describes the frame just completed. Sample must not allocate or do I/O.
/// Values are signed integers. Encode floats with BitConverter.SingleToInt32Bits for exact comparisons.</summary>
public interface IFrameDiagnosticsAnnotator
{
    /// <summary>Fixed channel names in sample order. Registration copies the names before measurement.</summary>
    string[] Channels { get; }
    /// <summary>Fill every channel for the previous completed frame without allocation or I/O.</summary>
    void Sample(Span<long> values);
}

internal sealed class FrameDiagnosticsAnnotations
{
    readonly IFrameDiagnosticsAnnotator? _annotator;
    readonly string[] _channels;
    readonly long[] _values;

    internal FrameDiagnosticsAnnotations(IFrameDiagnosticsAnnotator? annotator, int capacity)
    {
        _annotator = annotator;
        _channels = annotator is null ? [] : (string[])annotator.Channels.Clone();
        if (_channels.Length > 256) throw new ArgumentException("At most 256 channels are supported.", nameof(annotator));
        for (int i = 0; i < _channels.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(_channels[i]) || Array.IndexOf(_channels, _channels[i]) != i)
                throw new ArgumentException("Channel names must be nonempty and unique.", nameof(annotator));
        }
        _values = new long[capacity * _channels.Length];
    }

    internal void Sample(int index) => _annotator?.Sample(_values.AsSpan(index * _channels.Length, _channels.Length));
    internal FrameDiagnosticsAnnotationReport Report(int count)
    {
        var rows = new long[count][];
        for (int i = 0; i < count; i++) rows[i] = _values.AsSpan(i * _channels.Length, _channels.Length).ToArray();
        return new(_channels, rows);
    }
}

internal sealed record FrameDiagnosticsAnnotationReport(string[] Channels, long[][] Values);
