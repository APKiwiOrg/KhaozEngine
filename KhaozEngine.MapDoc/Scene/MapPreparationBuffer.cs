using System;
using System.Buffers;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>A growable JSON output whose admission includes simultaneous old and new backing arrays.</summary>
internal sealed class MapPreparationBuffer(int maximum = int.MaxValue) : IBufferWriter<byte>, IDisposable
{
    readonly MapPreparationReservation? _workspace = MapSceneAdmission.Current?.Workspace();
    byte[] _buffer = [];
    int _length;
    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);
    public void Advance(int count)
    {
        if (count < 0 || count > _buffer.Length - _length) throw new ArgumentOutOfRangeException(nameof(count));
        if (count > maximum - _length) throw new MapDocumentException($"JSON payload exceeds {maximum} bytes");
        _length += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0) { Grow(sizeHint); return _buffer.AsMemory(_length); }
    public Span<byte> GetSpan(int sizeHint = 0) { Grow(sizeHint); return _buffer.AsSpan(_length); }
    void Grow(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        sizeHint = Math.Max(1, sizeHint);
        if (sizeHint <= _buffer.Length - _length) return;
        int capacity = checked((int)Math.Max((long)_length + sizeHint, Math.Max(256L, 2L * _buffer.Length)));
        _workspace?.Reserve(capacity);
        byte[] next;
        try { next = new byte[capacity]; _buffer.AsSpan(0, _length).CopyTo(next); }
        catch { _workspace?.Release(capacity); throw; }
        _workspace?.Release(_buffer.Length);
        _buffer = next;
    }
    internal byte[] ToArray(out MapPreparationReservation? ownership)
    {
        _workspace?.Reserve(_length);
        byte[] result;
        try { result = WrittenSpan.ToArray(); }
        catch { _workspace?.Release(_length); throw; }
        ownership = _workspace?.Transfer(_length);
        return result;
    }
    public void Dispose() { _workspace?.Dispose(); _buffer = []; _length = 0; }
}
