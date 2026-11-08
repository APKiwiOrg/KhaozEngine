using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Exact, recording-local coverage for one buffer, offset and stream shape, across rebinds.</summary>
internal sealed class MetalVertexReadCoverage(MetalVertexWindowPool windowPool)
{
    // The primary run only grows. Every skipped window remains covered even after disconnected draws.
    bool _hasRun;
    ulong _first, _last;
    MetalVertexWindowPool.Store? _windows;
    MetalRecordedReads.Cursor[] _cursors = [];
    internal bool Reachable { get; private set; }
    internal long StepsForTests { get; private set; }

    internal void Clear()
    {
        _hasRun = Reachable = false;
        if (_windows is { } store) windowPool.Return(store);
        _windows = null;
        StepsForTests = 0;
    }

    internal void Record(MetalRecordedReads reads, MetalBuffer buffer, uint offset,
        in MetalVertexReadLayout.Stream stream, ulong first, ulong last, bool reachable)
    {
        if (reachable && Reachable) return;
        if (!reachable && Covers(first, last)) return;
        // Reachable indexed attributes can have different counts at the buffer's last partial vertex.
        // Draw windows share one count, so adjacent attribute bytes can safely use a single exact read.
        var attributes = reachable ? stream.Attributes : stream.WindowAttributes;
        if (_cursors.Length < attributes.Length)
            Array.Resize(ref _cursors, attributes.Length);
        for (int i = 0; i < attributes.Length; i++)
        {
            var attribute = attributes[i];
            ulong start = offset + attribute.Offset;
            if (reachable)
            {
                if (start + attribute.Bytes > buffer.SizeInBytes || stream.Stride == 0) continue;
                reads.AddStrided(buffer.CpuResource, start, attribute.Bytes, stream.Stride,
                    (buffer.SizeInBytes - start - attribute.Bytes) / stream.Stride + 1, ref _cursors[i]);
            }
            else reads.AddStrided(buffer.CpuResource, start + first * stream.Stride,
                attribute.Bytes, stream.Stride, last - first + 1, ref _cursors[i]);
        }
        // Publish coverage only after every attribute has been recorded successfully.
        if (reachable) Reachable = true;
        else if (!_hasRun)
        {
            _first = first;
            _last = last;
            _hasRun = true;
        }
        else if (first <= _last + 1 && last + 1 >= _first)
        {
            _first = Math.Min(_first, first);
            _last = Math.Max(_last, last);
        }
        else
        {
            // A same-start shorter window is covered too. Never claim the space between distinct windows.
            _windows = windowPool.Record(_windows, first, last);
        }
    }

    bool Covers(ulong first, ulong last)
    {
        StepsForTests += 2;
        if (_hasRun && first >= _first && last <= _last) return true;
        StepsForTests++;
        return _windows is { } store && store.Values.TryGetValue(first, out ulong end) && last <= end;
    }
}
