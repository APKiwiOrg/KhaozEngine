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
            long body = _s.Position;
            // A negative size seeks BACKWARD, and the only way out of this loop is reaching the end of the
            // stream, so a corrupt size can leave the position oscillating instead of ever getting there.
            // Refuse it as a parse error rather than spinning on the decode thread.
            if (sz < 0) throw new InvalidDataException($"WAV chunk '{id}' declares a negative size ({sz})");
            // RIFF pads every chunk to an even byte boundary and does NOT count that pad byte in the size, so
            // skipping only sz leaves every later chunk header one byte out. An odd-sized LIST/bext/cue chunk
            // ahead of fmt /data (common from DAWs and mastering tools) desyncs the whole walk that way, and the
            // hardcoded defaults below then silently stand in for the real format.
            long next = body + sz + (sz & 1);
            if (id == "fmt ")
            {
                if (sz < 16 || body + 16 > _s.Length) throw new InvalidDataException("WAV 'fmt ' chunk is truncated");
                _r.ReadInt16(); channels = _r.ReadInt16(); rate = _r.ReadInt32();
                _r.ReadInt32(); _r.ReadInt16(); bits = _r.ReadInt16();
            }
            else if (id == "data") { dataStart = body; dataLen = sz; }
            // A 'data' chunk that overruns the file is the truncated-download case, tolerated here and surfacing
            // as an EndOfStreamException at read time. Any OTHER chunk claiming to run past the end is simply
            // corrupt, and walking on from there only reads garbage as the next chunk header.
            else if (next > _s.Length) throw new InvalidDataException($"WAV chunk '{id}' runs past the end of the file");
            _s.Seek(next, SeekOrigin.Begin);
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
        for (int i = 0; i < n; i++) buffer[offset + i] = AudioConvert.ToShort(_f[i]);
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
        for (int i = 0; i < n; i++) buffer[offset + i] = AudioConvert.ToShort(_buf[i]);
        return n;
    }

    public void Dispose() => _f.Dispose();
}
