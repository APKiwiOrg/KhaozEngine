using System;
using System.IO;
using System.Text;
using KhaozEngine.Automation;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The bounded read, off the socket. The cap is the security-relevant half of the transport, because the token
/// travels inside the line and everything up to the token check is reachable by any local process that can connect.
/// A stream that counts what it handed over is what proves the reader STOPS at the cap rather than refusing after
/// the fact.
/// </summary>
public class AutomationLineReaderTests
{
    [Fact]
    public void ALineWithNoNewlineIsRefusedAtTheCapWithoutReadingTheRest()
    {
        var source = new CountingStream(new string('a', 1024 * 1024));
        var reader = new AutomationLineReader(source, AutomationHost.MaxRequestLineBytes);

        AutomationLine read = reader.ReadLine();

        Assert.Equal(AutomationLineOutcome.TooLong, read.Outcome);
        // One staging chunk of slack: the refusal happens on the chunk that carries the cap past the line.
        Assert.True(
            source.BytesRead <= AutomationHost.MaxRequestLineBytes + 4096,
            "read " + source.BytesRead + " bytes for a capped line");
    }

    [Fact]
    public void ALineExactlyAtTheCapIsStillARequest()
    {
        string line = new('b', AutomationHost.MaxRequestLineBytes);
        var reader = new AutomationLineReader(new CountingStream(line + "\n"), AutomationHost.MaxRequestLineBytes);

        AutomationLine read = reader.ReadLine();

        Assert.Equal(AutomationLineOutcome.Line, read.Outcome);
        Assert.Equal(line, read.Text);
    }

    [Fact]
    public void LinesArriveInOrderAcrossChunkBoundariesAndCrlfIsTrimmed()
    {
        string padding = new('x', 5000);                    // longer than the staging chunk, so it spans reads
        var source = new CountingStream("{\"a\":1}\n" + padding + "\r\n");
        var reader = new AutomationLineReader(source, AutomationHost.MaxRequestLineBytes);

        Assert.Equal("{\"a\":1}", reader.ReadLine().Text);
        Assert.Equal(padding, reader.ReadLine().Text);
        Assert.Equal(AutomationLineOutcome.PeerClosed, reader.ReadLine().Outcome);
    }

    [Fact]
    public void APartialLineAtTheEndOfTheStreamReadsAsAClose()
    {
        var reader = new AutomationLineReader(new CountingStream("{\"no\":\"newline\"}"), AutomationHost.MaxRequestLineBytes);

        Assert.Equal(AutomationLineOutcome.PeerClosed, reader.ReadLine().Outcome);
    }

    /// <summary>A finite in-memory stream that remembers how much of itself the reader actually took.</summary>
    sealed class CountingStream : Stream
    {
        readonly byte[] _bytes;
        int _position;

        public CountingStream(string content) => _bytes = Encoding.UTF8.GetBytes(content);

        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(count, _bytes.Length - _position);
            Array.Copy(_bytes, _position, buffer, offset, take);
            _position += take;
            BytesRead += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
