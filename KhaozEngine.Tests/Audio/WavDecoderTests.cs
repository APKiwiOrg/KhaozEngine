using System.IO;
using System.Text;
using KhaozEngine.Audio.Decoding;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Root-cause coverage for the corrupt/truncated music crash (#111): a WAV whose 'data' chunk header
/// declares more bytes than the file actually contains makes the streaming decoder read past the real end
/// of the file. This is the failure the music frame-loop guard has to contain instead of crashing the game.
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
