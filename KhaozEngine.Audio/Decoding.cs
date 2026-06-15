using System;
using System.IO;

namespace KhaozEngine.Audio.Decoding;

/// <summary>Streams interleaved 16-bit PCM from an audio file. Decoder-agnostic (WAV/OGG/MP3).</summary>
internal interface IPcmDecoder : IDisposable
{
    int Channels { get; }
    int SampleRate { get; }
    /// <summary>Read up to <paramref name="count"/> samples (shorts) into <paramref name="buffer"/>; returns the count read (0 = end of stream).</summary>
    int ReadSamples(short[] buffer, int offset, int count);
}

internal static class PcmDecoders
{
    public static IPcmDecoder Open(string path)
    {
        var s = File.OpenRead(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ogg" => new OggDecoder(s),
            ".mp3" => new Mp3Decoder(s),
            ".wav" => new WavDecoder(s),
            var ext => throw new NotSupportedException("unsupported audio format: " + ext),
        };
    }

    internal static short ToShort(float f) => (short)Math.Clamp((int)MathF.Round(f * 32767f), short.MinValue, short.MaxValue);
}

/// <summary>Streams 16-bit PCM from a RIFF/WAVE file.</summary>
internal sealed class WavDecoder : IPcmDecoder
{
    readonly Stream _s;
    readonly BinaryReader _r;
    long _dataEnd;
    public int Channels { get; }
    public int SampleRate { get; }

    public WavDecoder(Stream s)
    {
        _s = s; _r = new BinaryReader(s);
        if (new string(_r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a RIFF file");
        _r.ReadInt32();
        if (new string(_r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAVE file");
        int channels = 2, rate = 44100, bits = 16;
        long dataStart = 0, dataLen = 0;
        while (_s.Position + 8 <= _s.Length)
        {
            string id = new string(_r.ReadChars(4));
            int sz = _r.ReadInt32();
            if (id == "fmt ")
            {
                _r.ReadInt16(); channels = _r.ReadInt16(); rate = _r.ReadInt32();
                _r.ReadInt32(); _r.ReadInt16(); bits = _r.ReadInt16();
                if (sz > 16) _s.Seek(sz - 16, SeekOrigin.Current);
            }
            else if (id == "data") { dataStart = _s.Position; dataLen = sz; _s.Seek(sz, SeekOrigin.Current); }
            else _s.Seek(sz, SeekOrigin.Current);
        }
        if (bits != 16) throw new NotSupportedException("only 16-bit PCM WAV is supported");
        Channels = channels; SampleRate = rate;
        _dataEnd = dataStart + dataLen;
        _s.Seek(dataStart, SeekOrigin.Begin);
    }

    public int ReadSamples(short[] buffer, int offset, int count)
    {
        int avail = (int)((_dataEnd - _s.Position) / 2);
        int n = Math.Min(count, avail);
        for (int i = 0; i < n; i++) buffer[offset + i] = _r.ReadInt16();
        return n;
    }

    public void Dispose() => _r.Dispose();
}

/// <summary>Streams 16-bit PCM from an Ogg Vorbis file (NVorbis).</summary>
internal sealed class OggDecoder : IPcmDecoder
{
    readonly NVorbis.VorbisReader _r;
    float[] _f = Array.Empty<float>();
    public int Channels => _r.Channels;
    public int SampleRate => _r.SampleRate;

    public OggDecoder(Stream s) => _r = new NVorbis.VorbisReader(s, true);

    public int ReadSamples(short[] buffer, int offset, int count)
    {
        if (_f.Length < count) _f = new float[count];
        int n = _r.ReadSamples(_f, 0, count);
        for (int i = 0; i < n; i++) buffer[offset + i] = PcmDecoders.ToShort(_f[i]);
        return n;
    }

    public void Dispose() => _r.Dispose();
}

/// <summary>Streams 16-bit PCM from an MP3 file (NLayer).</summary>
internal sealed class Mp3Decoder : IPcmDecoder
{
    readonly NLayer.MpegFile _f;
    float[] _buf = Array.Empty<float>();
    public int Channels => _f.Channels;
    public int SampleRate => _f.SampleRate;

    public Mp3Decoder(Stream s) => _f = new NLayer.MpegFile(s);

    public int ReadSamples(short[] buffer, int offset, int count)
    {
        if (_buf.Length < count) _buf = new float[count];
        int n = _f.ReadSamples(_buf, 0, count);
        for (int i = 0; i < n; i++) buffer[offset + i] = PcmDecoders.ToShort(_buf[i]);
        return n;
    }

    public void Dispose() => _f.Dispose();
}
