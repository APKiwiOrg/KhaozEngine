using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace KhaozEngine.Automation
{
    /// <summary>What one read off the wire produced.</summary>
    enum AutomationLineOutcome
    {
        /// <summary>A complete request line, in <see cref="AutomationLine.Text"/>.</summary>
        Line,

        /// <summary>The peer closed, or half-closed with a partial line behind it.</summary>
        PeerClosed,

        /// <summary>The line passed the cap before a newline arrived, so nothing more of it was buffered.</summary>
        TooLong,

        /// <summary>The socket's receive deadline expired with no complete line.</summary>
        TimedOut,
    }

    /// <summary>One read's outcome and, when there is one, the line it produced.</summary>
    readonly record struct AutomationLine(AutomationLineOutcome Outcome, string? Text);

    /// <summary>
    /// A newline-delimited reader with a hard byte cap, standing in for <c>StreamReader.ReadLine</c>, which has
    /// neither a cap nor a way to tell a deadline apart from a failure.
    /// <para>
    /// The cap is the point. The token travels INSIDE the first line, so everything up to the token check is
    /// reachable by any local process that can connect, and an unbounded accumulate lets one of them grow the host's
    /// heap by whatever it cares to write (measured: 200 MB written with no newline took the managed heap past a
    /// gigabyte in under two seconds, with the connection still open). Past the cap this reader stops copying and
    /// says so, so the bytes still arriving are read into a fixed chunk and dropped.
    /// </para>
    /// </summary>
    sealed class AutomationLineReader
    {
        /// <summary>The fixed staging buffer every read lands in, whatever the peer is sending.</summary>
        const int ChunkBytes = 4096;

        readonly Stream _stream;
        readonly int _maxBytes;
        readonly byte[] _chunk = new byte[ChunkBytes];
        readonly MemoryStream _line = new();
        int _start;
        int _available;

        public AutomationLineReader(Stream stream, int maxBytes)
        {
            _stream = stream;
            _maxBytes = maxBytes;
        }

        /// <summary>
        /// Read the next line, blocking until one arrives, the peer closes, the cap is passed or the socket's
        /// receive deadline expires. A <see cref="AutomationLineOutcome.TooLong"/> line is not recoverable, because
        /// what is left of it on the wire is not a request and cannot be resynchronised against one, so the caller
        /// answers it once and closes.
        /// </summary>
        public AutomationLine ReadLine()
        {
            _line.SetLength(0);
            while (true)
            {
                if (_available == 0)
                {
                    int read;
                    try
                    {
                        read = _stream.Read(_chunk, 0, ChunkBytes);
                    }
                    catch (IOException ex) when (IsTimeout(ex))
                    {
                        return new AutomationLine(AutomationLineOutcome.TimedOut, null);
                    }
                    if (read <= 0) return new AutomationLine(AutomationLineOutcome.PeerClosed, null);
                    _start = 0;
                    _available = read;
                }

                int newline = Array.IndexOf(_chunk, (byte)'\n', _start, _available);
                int take = newline >= 0 ? newline - _start : _available;
                if (_line.Length + take > _maxBytes) return new AutomationLine(AutomationLineOutcome.TooLong, null);

                _line.Write(_chunk, _start, take);
                if (newline < 0)
                {
                    _available = 0;
                    continue;
                }

                _available -= take + 1;
                _start = newline + 1;
                return new AutomationLine(AutomationLineOutcome.Line, Decode());
            }
        }

        /// <summary>
        /// Read and DISCARD whatever the peer is still sending, into the same fixed chunk, until it stops, closes,
        /// or <paramref name="timeout"/> passes. Called after a refusal, and it is what makes the refusal readable:
        /// closing a socket that still has unread bytes queued RESETS the connection, and a reset throws away the
        /// error line the peer was just sent, so a caller that wrote too much would see a crash rather than the
        /// reason it was refused. Bounded, and it buffers nothing.
        /// </summary>
        public void DiscardPending(TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            _available = 0;
            while (elapsed.Elapsed < timeout)
            {
                int read;
                try
                {
                    read = _stream.Read(_chunk, 0, ChunkBytes);
                }
                catch (IOException ex) when (IsTimeout(ex))
                {
                    return;
                }
                if (read <= 0) return;
            }
        }

        /// <summary>Decode the buffered line, dropping the carriage return a CRLF writer leaves behind.</summary>
        string Decode()
        {
            int length = (int)_line.Length;
            if (length > 0 && _line.GetBuffer()[length - 1] == (byte)'\r') length--;
            return Encoding.UTF8.GetString(_line.GetBuffer(), 0, length);
        }

        /// <summary>A receive deadline surfaces as an <see cref="IOException"/> wrapping the socket's own timeout.</summary>
        static bool IsTimeout(IOException ex) =>
            ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut };
    }
}
