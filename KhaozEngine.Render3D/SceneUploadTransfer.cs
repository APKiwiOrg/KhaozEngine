using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>One copy of an admitted upload into a buffer or one texture level, advanced within a byte allowance. A
/// buffer copy keeps four-byte alignment until its tail. A texture copy progresses within one row at a time in whole
/// texels, so an allowance too small for one RGBA texel makes no progress.</summary>
internal sealed class SceneUploadTransfer
{
    readonly IGpuBuffer? _buffer;
    readonly IGpuTexture? _texture;
    readonly byte[] _bytes;
    readonly int _width;
    readonly uint _mip, _layer;
    int _offset;

    internal SceneUploadTransfer(IGpuBuffer buffer, byte[] bytes) { _buffer = buffer; _bytes = bytes; }

    internal SceneUploadTransfer(IGpuTexture texture, byte[] bytes, int width, uint mip, uint layer)
    { _texture = texture; _bytes = bytes; _width = width; _mip = mip; _layer = layer; }

    internal bool Finished => _offset == _bytes.Length;

    internal long Advance(IGpuDevice device, long allowance)
    {
        int count = (int)Math.Min(_bytes.Length - _offset, Math.Min(allowance, int.MaxValue));
        if (_buffer is not null)
        {
            // All production buffer formats and destination offsets have a two-byte minimum alignment.
            if (count < _bytes.Length - _offset) count -= count % 4;
            if (count == 0) return 0;
            device.UpdateBuffer(_buffer, (uint)_offset, (ReadOnlySpan<byte>)_bytes.AsSpan(_offset, count));
        }
        else
        {
            int rowBytes = checked(_width * 4), x = _offset % rowBytes;
            count = Math.Min(count, rowBytes - x); count -= count % 4;
            if (count == 0) return 0;
            byte[] row = _bytes.AsSpan(_offset, count).ToArray();
            device.UpdateTexture(_texture!, row, (uint)(x / 4), (uint)(_offset / rowBytes), (uint)(count / 4), 1, _mip, _layer);
        }
        _offset += count;
        return count;
    }
}
