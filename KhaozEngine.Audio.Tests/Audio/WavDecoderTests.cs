using System.IO;
using System.Text;
using KhaozEngine.Audio.Decoding;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Root-cause coverage for the corrupt/truncated music crash (#111): a WAV whose 'data' chunk header
/// declares more bytes than the file actually contains makes the streaming decoder read past the real end
/// of the file. This is the failure the music frame-loop guard has to contain instead of crashing the game.
/// Also covers the RIFF chunk walk itself (#112): the mandatory pad byte on an odd-sized chunk, and a
/// corrupt chunk size that used to seek backwards forever.
/// </summary>
public sealed class WavDecoderTests
{
    [Fact]
    public void ReadSamples_DataChunkLongerThanFile_ThrowsEndOfStream()
    {
        // Models an interrupted copy or a truncated download: the header promises 100 KB of PCM, only 8
        // bytes are actually present.
        byte[] wav = BuildTruncatedWav(declaredDataBytes: 100_000, actualDataBytes: 8);
        using var decoder = new WavDecoder(new MemoryStream(wav));

        var buffer = new short[4096];
        // The decoder streams the real bytes it has, then runs off the end of the truncated file and throws.
        Assert.Throws<EndOfStreamException>(() => decoder.ReadSamples(buffer, 0, buffer.Length));
    }

    [Fact]
    public void OddSizedChunkBeforeFmt_DoesNotDesyncTheChunkWalk()
    {
        // A 5-byte LIST chunk (a DAW/mastering-tool metadata chunk) ahead of 'fmt '. RIFF pads it to 6 bytes,
        // and a walk that skips only the declared 5 lands one byte short and reads the rest of the file
        // off-by-one, so 'fmt ' and 'data' are never found and the hardcoded 2ch/44100Hz defaults stand in.
        byte[] wav = BuildWavWithLeadingChunk("LIST", new byte[] { 1, 2, 3, 4, 5 }, channels: 1, sampleRate: 22050,
            samples: new short[] { 1000, -1000, 2000, -2000 });
        using var decoder = new WavDecoder(new MemoryStream(wav));

        Assert.Equal(1, decoder.Channels);
        Assert.Equal(22050, decoder.SampleRate);

        var buffer = new short[8];
        Assert.Equal(4, decoder.ReadSamples(buffer, 0, buffer.Length));
        Assert.Equal(new short[] { 1000, -1000, 2000, -2000 }, buffer[..4]);
    }

    [Fact]
    public void NegativeChunkSize_ThrowsInvalidData()
    {
        // A hand-crafted or corrupted file whose 'data' chunk declares a negative size. Seeking by it goes
        // BACKWARD, so the walk can re-read the same bytes forever instead of reaching the end of the stream.
        byte[] wav = BuildTruncatedWav(declaredDataBytes: -4, actualDataBytes: 64);

        Assert.Throws<InvalidDataException>(() => new WavDecoder(new MemoryStream(wav)));
    }

    /// <summary>
    /// Builds a valid 16-bit PCM RIFF/WAVE file with one extra chunk (<paramref name="chunkId"/> /
    /// <paramref name="chunkPayload"/>) ahead of 'fmt ', RIFF-padded to an even boundary as the spec requires.
    /// </summary>
    private static byte[] BuildWavWithLeadingChunk(string chunkId, byte[] chunkPayload, short channels,
        int sampleRate, short[] samples)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(0);                               // RIFF size (the decoder reads and ignores this)
            w.Write(Encoding.ASCII.GetBytes("WAVE"));

            w.Write(Encoding.ASCII.GetBytes(chunkId));
            w.Write(chunkPayload.Length);
            w.Write(chunkPayload);
            if ((chunkPayload.Length & 1) != 0) w.Write((byte)0);   // the mandatory RIFF pad byte

            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);                              // fmt chunk size
            w.Write((short)1);                        // audio format = PCM
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(sampleRate * channels * 2);       // byte rate
            w.Write((short)(channels * 2));           // block align
            w.Write((short)16);                       // bits per sample

            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(samples.Length * 2);
            foreach (short s in samples) w.Write(s);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a valid 16-bit PCM RIFF/WAVE header whose 'data' chunk claims <paramref name="declaredDataBytes"/>
    /// but is followed by only <paramref name="actualDataBytes"/> of PCM, i.e. a truncated file.
    /// </summary>
    private static byte[] BuildTruncatedWav(int declaredDataBytes, int actualDataBytes)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + declaredDataBytes);          // RIFF size (the decoder reads and ignores this)
            w.Write(Encoding.ASCII.GetBytes("WAVE"));

            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);                              // fmt chunk size
            w.Write((short)1);                        // audio format = PCM
            w.Write((short)2);                        // channels
            w.Write(44100);                           // sample rate
            w.Write(44100 * 2 * 2);                   // byte rate
            w.Write((short)4);                        // block align
            w.Write((short)16);                       // bits per sample

            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(declaredDataBytes);               // DECLARED data size (far more than is present)
            w.Write(new byte[actualDataBytes]);       // the little PCM that actually made it into the file
        }
        return ms.ToArray();
    }
}
