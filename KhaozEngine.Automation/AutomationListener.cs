using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// Gate 3's socket: a loopback-only TCP listener on an ephemeral port, one JSON object per line in each
    /// direction, no HTTP stack and no framing library, because the whole point of the transport is that it is too
    /// small to have bugs of its own.
    /// <para>
    /// One background thread accepts, one thread serves each connection. A malformed line gets an error reply and
    /// the connection stays open, since the caller can recover from its own typo. A wrong or missing token gets ONE
    /// refusal and then the connection closes, so a guesser pays a reconnect per attempt. A line past
    /// <see cref="AutomationHost.MaxRequestLineBytes"/> and a connection past its read deadline each get the same
    /// treatment as a refusal, before either one has cost the host anything.
    /// </para>
    /// </summary>
    sealed class AutomationListener : IDisposable
    {
        readonly AutomationOptions _options;
        readonly TcpListener _listener;
        readonly string _token;
        readonly Func<long> _frame;
        readonly Func<AutomationRequest, Task<AutomationReply>> _dispatch;
        readonly List<TcpClient> _clients = new();
        readonly object _clientsLock = new();
        volatile bool _stopping;
        int _inFlight;

        /// <summary>The ephemeral port the loopback listener bound.</summary>
        public int Port { get; }

        /// <summary>Bind loopback on an ephemeral port and start accepting. Throws if the bind fails, because a host
        /// that cannot listen must not go on to advertise a port in the handshake file.</summary>
        public AutomationListener(
            AutomationOptions options, string token, Func<long> frame, Func<AutomationRequest, Task<AutomationReply>> dispatch)
        {
            _options = options;
            _token = token;
            _frame = frame;
            _dispatch = dispatch;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            var accept = new Thread(AcceptLoop) { IsBackground = true, Name = "ke-automation-accept" };
            accept.Start();
        }

        /// <summary>Accept connections until <see cref="Dispose"/>. Same rule as <see cref="Serve"/>: nothing escapes
        /// this thread. <c>Stop()</c> unblocks the pending accept by faulting it, which is the ordinary shutdown path
        /// rather than an error, and any other failure ends the endpoint for this run, which is exactly the case
        /// <see cref="AutomationOptions.Log"/> exists to report: from the bridge's side it is indistinguishable from
        /// a game that never opened one.</summary>
        void AcceptLoop()
        {
            try
            {
                while (!_stopping)
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    lock (_clientsLock)
                    {
                        if (_stopping) { client.Dispose(); return; }
                        _clients.Add(client);
                    }
                    var serve = new Thread(() => Serve(client)) { IsBackground = true, Name = "ke-automation-serve" };
                    serve.Start();
                }
            }
            catch (Exception ex)
            {
                Report("automation: the accept loop stopped, so the endpoint is gone for the rest of this run", ex);
            }
        }

        /// <summary>
        /// Serve one connection to the end, on its own thread.
        /// <para>
        /// <b>Nothing escapes.</b> This runs on a background thread inside a RUNNING GAME, so an escaped exception
        /// takes the whole process down, and the failure modes here are all the same event seen from different
        /// angles: the peer vanished mid-line, or <see cref="Dispose"/> closed the socket under us. A narrow catch
        /// list was tried and was not enough, because <c>TcpClient.NoDelay</c> on an already-disposed client throws
        /// <see cref="NullReferenceException"/> rather than <see cref="ObjectDisposedException"/>, which crashed a
        /// test host. So the catch is broad, on purpose, and every one of them ends the same way: this connection is
        /// gone and the game keeps running. What it no longer does is end SILENTLY.
        /// </para>
        /// <para>
        /// The receive deadline starts short, because the first line is the one that must carry the token, and goes
        /// to the idle deadline once a line has authenticated.
        /// </para>
        /// </summary>
        void Serve(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = Milliseconds(_options.FirstLineTimeout);
                using NetworkStream stream = client.GetStream();
                var reader = new AutomationLineReader(stream, AutomationHost.MaxRequestLineBytes);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                while (!_stopping)
                {
                    AutomationLine read = reader.ReadLine();
                    if (read.Outcome == AutomationLineOutcome.PeerClosed) return;
                    if (read.Outcome == AutomationLineOutcome.TimedOut)
                    {
                        Report("automation: a connection went " + client.ReceiveTimeout
                            + " ms without a complete request line, so it was closed", null);
                        return;
                    }
                    if (read.Outcome == AutomationLineOutcome.TooLong)
                    {
                        writer.WriteLine(AutomationReply.Failure(0, _frame(), TooLongError).ToJsonLine());
                        Report("automation: a request line passed " + AutomationHost.MaxRequestLineBytes
                            + " bytes with no newline, so it was refused and the connection closed", null);
                        client.ReceiveTimeout = RefusalDrainPollMilliseconds;
                        reader.DiscardPending(TimeSpan.FromMilliseconds(RefusalDrainMilliseconds));
                        return;
                    }

                    string line = read.Text!;
                    if (!AutomationRequest.TryParse(line, out AutomationRequest? request, out string? error))
                    {
                        writer.WriteLine(AutomationReply.Failure(0, _frame(), error!).ToJsonLine());
                        continue;                            // malformed is recoverable: stay open
                    }
                    if (!AutomationHandshake.TokenMatches(_token, request.Token))
                    {
                        writer.WriteLine(AutomationReply.Failure(request.Id, _frame(), "unauthorized").ToJsonLine());
                        return;                              // one refusal, then close
                    }
                    client.ReceiveTimeout = Milliseconds(_options.IdleTimeout);

                    Interlocked.Increment(ref _inFlight);
                    try
                    {
                        AutomationReply reply = _dispatch(request).GetAwaiter().GetResult();
                        writer.WriteLine(reply.ToJsonLine());
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_stopping) Report("automation: a connection ended on an error", ex);
            }
            finally
            {
                lock (_clientsLock) _clients.Remove(client);
                try { client.Dispose(); } catch (Exception) { }
            }
        }

        /// <summary>
        /// Wait, briefly, for every reply already handed to the dispatcher to reach the wire. The host calls this
        /// after it has failed the pending commands and BEFORE it closes the sockets, because the reply a command
        /// fails with is written by the serving thread and a socket closed under it turns that reply into an EOF the
        /// bridge has to guess at. Bounded, so a wedged window thread costs the head this much and no more.
        /// </summary>
        public void DrainReplies(TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            while (Volatile.Read(ref _inFlight) > 0 && elapsed.Elapsed < timeout) Thread.Sleep(1);
        }

        /// <summary>Stop accepting and drop every live connection, which unblocks each serving thread's read.</summary>
        public void Dispose()
        {
            _stopping = true;
            try { _listener.Stop(); } catch (Exception) { }

            TcpClient[] live;
            lock (_clientsLock)
            {
                live = _clients.ToArray();
                _clients.Clear();
            }
            foreach (TcpClient client in live)
            {
                try { client.Close(); } catch (Exception) { }
            }
        }

        /// <summary>Report to the head's hook, which is a game's log and therefore not to be trusted with this
        /// thread: a hook that throws would take the process down through exactly the path this class exists to
        /// keep closed.</summary>
        void Report(string message, Exception? error)
        {
            try { _options.Log?.Invoke(message, error); }
            catch (Exception) { }
        }

        /// <summary>A non-positive timeout means the socket waits forever, which is what 0 says to Windows sockets.</summary>
        static int Milliseconds(TimeSpan span) =>
            span <= TimeSpan.Zero ? 0 : (int)Math.Min(span.TotalMilliseconds, int.MaxValue);

        /// <summary>How long a refused connection spends dropping what the peer is still sending, so the close is a
        /// FIN behind the error line rather than a reset that throws it away.</summary>
        const int RefusalDrainMilliseconds = 250;

        /// <summary>The receive deadline used during that drain, so a peer that went quiet without closing costs one
        /// poll rather than the whole budget.</summary>
        const int RefusalDrainPollMilliseconds = 50;

        /// <summary>The one error a line past the cap gets, naming the cap so the caller can size its own writes.</summary>
        internal static readonly string TooLongError =
            "request line exceeds " + AutomationHost.MaxRequestLineBytes + " bytes";
    }
}
