using System;
using System.Collections.Generic;
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
    /// refusal and then the connection closes, so a guesser pays a reconnect per attempt.
    /// </para>
    /// </summary>
    sealed class AutomationListener : IDisposable
    {
        readonly TcpListener _listener;
        readonly string _token;
        readonly Func<long> _frame;
        readonly Func<AutomationRequest, Task<AutomationReply>> _dispatch;
        readonly List<TcpClient> _clients = new();
        readonly object _clientsLock = new();
        volatile bool _stopping;

        /// <summary>The ephemeral port the loopback listener bound.</summary>
        public int Port { get; }

        /// <summary>Bind loopback on an ephemeral port and start accepting. Throws if the bind fails, because a host
        /// that cannot listen must not go on to advertise a port in the handshake file.</summary>
        public AutomationListener(string token, Func<long> frame, Func<AutomationRequest, Task<AutomationReply>> dispatch)
        {
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
        /// rather than an error, and any other failure simply ends the endpoint for this run.</summary>
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
            catch (Exception) { }
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
        /// gone and the game keeps running.
        /// </para>
        /// </summary>
        void Serve(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                while (!_stopping)
                {
                    string? line = reader.ReadLine();
                    if (line is null) return;                // peer closed

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
                    AutomationReply reply = _dispatch(request).GetAwaiter().GetResult();
                    writer.WriteLine(reply.ToJsonLine());
                }
            }
            catch (Exception) { }                            // see the note above: this thread never throws outward
            finally
            {
                lock (_clientsLock) _clients.Remove(client);
                try { client.Dispose(); } catch (Exception) { }
            }
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
    }
}
