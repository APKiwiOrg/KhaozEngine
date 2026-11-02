using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal;

internal enum PassLabelEventKind { Begin, End, Skip }

/// <summary>EncoderOrdinal is the ordinal the next encoder on this command buffer takes, so an event with ordinal N
/// happened after encoder N-1 began and before encoder N began. Encoder N-1 may still be open.</summary>
internal readonly record struct PassLabelEvent(int CallbackIndex, long CommandBuffer, int EncoderOrdinal,
    string Kind, int LabelId, int Occurrence);

internal sealed record PassLabelsSnapshot(string[] Labels, FrameTimingBufferSnapshot<PassLabelEvent> Events,
    long LabelOverflow, long StackOverflow, long UnbalancedEnds, long AbandonedScopes, long ForeignEvents,
    long ObserverFailures)
{
    internal static PassLabelsSnapshot Unavailable() => new([], new([], 0, 0), 0, 0, 0, 0, 0, 0);
}

/// <summary>Render3D's logical pass keys for the frame command list. Only the callback thread records, except for the
/// two failure counters. Interning, the open-scope stack and the event stream never allocate once constructed.</summary>
internal sealed class MetalPassLabels
{
    internal const string DirectionalPass = "shadow.directional";
    internal const int DirectionalLabel = 0;
    const int MaxDepth = 64;

    readonly Dictionary<string, int> _ids;
    readonly string[] _labels;
    readonly int[] _stackLabels = new int[MaxDepth];
    readonly int[] _stackOccurrences = new int[MaxDepth];
    readonly FrameTimingBuffer<PassLabelEvent> _events;
    int _labelCount, _depth, _directionalOpen;
    long _labelOverflow, _stackOverflow, _unbalancedEnds, _abandonedScopes, _foreignEvents, _observerFailures;

    internal MetalPassLabels(int labels, int events)
    {
        if (labels < 1) throw new ArgumentOutOfRangeException(nameof(labels));
        _labels = new string[labels];
        _ids = new(labels, StringComparer.Ordinal);
        _events = new(events);
        // Reserved first so the experiment compares ids and never loses this label to table overflow.
        Intern(DirectionalPass);
        _ids.TryGetValue(DirectionalPass, out _);
    }

    internal bool DirectionalOpen => _directionalOpen > 0;

    internal void Innermost(out int label, out int occurrence)
    {
        label = _depth > 0 ? _stackLabels[Math.Min(_depth, MaxDepth) - 1] : -1;
        occurrence = _depth > 0 ? _stackOccurrences[Math.Min(_depth, MaxDepth) - 1] : 0;
    }

    /// <summary>A new frame recording. A scope still open belongs to a recording that is over.</summary>
    internal void BeginRecording()
    {
        if (_depth > 0) _abandonedScopes += _depth;
        _depth = 0;
        _directionalOpen = 0;
    }

    /// <returns>The label id this event carried, or -1 once the table is full.</returns>
    internal int Observe(int callback, long commandBuffer, int encoderOrdinal, PassLabelEventKind kind, string id, int occurrence)
    {
        int label = Intern(id);
        _events.Add(new(callback, commandBuffer, encoderOrdinal,
            kind == PassLabelEventKind.Begin ? "begin" : kind == PassLabelEventKind.End ? "end" : "skip", label, occurrence));
        if (kind == PassLabelEventKind.Begin) Push(label, occurrence);
        else if (kind == PassLabelEventKind.End) Pop(label, occurrence);
        return label;
    }

    internal void Foreign() => Interlocked.Increment(ref _foreignEvents);
    internal void ObserverFailed() => Interlocked.Increment(ref _observerFailures);

    int Intern(string id)
    {
        if (_ids.TryGetValue(id, out int label)) return label;
        if (_labelCount == _labels.Length) { _labelOverflow++; return -1; }
        _labels[_labelCount] = id;
        _ids.Add(id, _labelCount);
        return _labelCount++;
    }

    void Push(int label, int occurrence)
    {
        if (_depth >= MaxDepth) { _stackOverflow++; _depth++; return; }
        _stackLabels[_depth] = label;
        _stackOccurrences[_depth] = occurrence;
        _depth++;
        if (label == DirectionalLabel) _directionalOpen++;
    }

    void Pop(int label, int occurrence)
    {
        if (_depth == 0) { _unbalancedEnds++; return; }
        // A push past the bound stored nothing, so its end only unwinds the count.
        if (_depth > MaxDepth) { _depth--; return; }
        if (_stackLabels[_depth - 1] != label || _stackOccurrences[_depth - 1] != occurrence)
        {
            _unbalancedEnds++;
            return;
        }
        _depth--;
        if (label == DirectionalLabel) _directionalOpen--;
    }

    internal PassLabelsSnapshot Snapshot() => new(_labels.Take(_labelCount).ToArray(), _events.Snapshot(), _labelOverflow,
        _stackOverflow, _unbalancedEnds, _abandonedScopes, Interlocked.Read(ref _foreignEvents), Interlocked.Read(ref _observerFailures));
}
