using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Windowing;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// The dev-only playtest endpoint: a loopback JSON-lines listener whose commands are queued off the socket
    /// thread and applied on the window thread at the frame boundary, following the cross-thread precedent the
    /// single-instance guard already sets in <c>GameApp</c>.
    /// <para>
    /// <b>Three gates, all required at once.</b> Gate 1 is the head's <c>Condition="'$(Configuration)' == 'Debug'"</c>
    /// on the <c>PackageReference</c>, so a Release binary contains none of this code. Gate 2 is
    /// <see cref="AutomationOptions.Enabled"/> AND the <c>KE_AUTOMATION=1</c> environment variable, so an ordinary
    /// Debug playtest opens no socket. Gate 3 is the transport: loopback only, an ephemeral port, and a per-run
    /// random token every request must carry. <see cref="Start"/> is the only method the gates guard, and when they
    /// refuse it there is no thread, no socket and no handshake file.
    /// </para>
    /// <para>
    /// <see cref="Pump"/>, <see cref="Submit"/> and <see cref="Register"/> work whether or not the host started, on
    /// purpose: they are the machine the gates decide whether to WIRE UP, and keeping the gate at the wiring point
    /// is what makes the whole endpoint testable headlessly with no socket and no window.
    /// </para>
    /// <para>
    /// It never sets <c>KE_MAX_FRAMES</c>. That flag ends the process at a frame count, which is right for a smoke
    /// test and wrong for a session an agent steps interactively.
    /// </para>
    /// </summary>
    public sealed partial class AutomationHost : IDisposable
    {
        /// <summary>The environment variable that arms gate 2's second half. Must read exactly <see cref="EnabledValue"/>.</summary>
        public const string EnvironmentVariable = "KE_AUTOMATION";

        /// <summary>The only value of <see cref="EnvironmentVariable"/> that arms the host.</summary>
        public const string EnabledValue = "1";

        /// <summary>The handshake file's name inside <see cref="AutomationOptions.HandshakeDirectory"/>.</summary>
        public const string HandshakeFileName = "automation.json";

        /// <summary>
        /// The most bytes one request line may carry before the connection is refused and closed. 64 KiB is two
        /// orders of magnitude past the longest command this protocol has (a <c>call</c> with a JSON argument
        /// document), and the cap exists because the token travels inside the line: without it any local process
        /// could grow the host's heap by whatever it cared to write, having authenticated nothing.
        /// </summary>
        public const int MaxRequestLineBytes = 64 * 1024;

        /// <summary>How long <see cref="Dispose"/> waits for the replies it just failed to reach the wire before it
        /// closes the sockets under them. Bounded, so a wedged window thread cannot hang a head's shutdown.</summary>
        static readonly TimeSpan ReplyDrainTimeout = TimeSpan.FromMilliseconds(250);

        readonly AutomationOptions _options;
        readonly AutomationInputInjector _injector = new();
        readonly Dictionary<string, Func<JsonElement, JsonNode?>> _verbs = new(StringComparer.Ordinal);
        readonly ConcurrentQueue<PendingCommand> _incoming = new();
        readonly List<PendingCommand> _waiting = new();
        readonly object _submitLock = new();
        readonly object _waitingLock = new();

        AutomationListener? _listener;
        EventHandler? _processExit;
        string? _token;
        long _frame;
        volatile bool _running;
        volatile bool _disposed;

        /// <summary>Configure a host with no window attached. The head owns the wiring: hand <see cref="Pump"/> to
        /// <c>AppWindow.InputFilter</c> and set <see cref="QuitRequested"/> yourself. Prefer the
        /// <see cref="AutomationHost(AppWindow, AutomationOptions)"/> overload, which does both.</summary>
        public AutomationHost(AutomationOptions options) => _options = options;

        /// <summary>The frame the pump is on, counted from 1. Read from any thread.</summary>
        public long Frame => Interlocked.Read(ref _frame);

        /// <summary>True once <see cref="Start"/> passed the gates and bound the listener.</summary>
        public bool IsRunning => _running;

        /// <summary>The bound loopback port, or 0 while the host is not running.</summary>
        public int Port => _listener?.Port ?? 0;

        /// <summary>The per-run token, or null while the host is not running.</summary>
        public string? Token => _token;

        /// <summary>Where the handshake file goes: <see cref="AutomationOptions.HandshakeDirectory"/> plus
        /// <see cref="HandshakeFileName"/>.</summary>
        public string HandshakeFilePath =>
            System.IO.Path.Combine(_options.HandshakeDirectory, HandshakeFileName);

        /// <summary>
        /// The game's state document, invoked on the window thread at the frame boundary when a <c>state</c> command
        /// is applied. Null (the default) makes <c>state</c> an error, since a host with no provider has nothing
        /// truthful to say.
        /// </summary>
        public Func<JsonNode?>? StateProvider { get; set; }

        /// <summary>
        /// What a <c>quit</c> command does, invoked on the window thread. The
        /// <see cref="AutomationHost(AppWindow, AutomationOptions)"/> overload points this at
        /// <c>AppWindow.Close</c> unless the head has already set something.
        /// </summary>
        public Action? QuitRequested { get; set; }

        /// <summary>True while <c>KE_AUTOMATION</c> reads exactly <see cref="EnabledValue"/>.</summary>
        public static bool EnvironmentAllows =>
            string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), EnabledValue, StringComparison.Ordinal);

        /// <summary>
        /// Register a named verb the <c>call</c> command can run on the window thread. The engine defines the seam
        /// and knows nothing about what the verb does: projecting a tile to a screen pixel needs the live camera, and
        /// only the game has it. Register everything BEFORE <see cref="Start"/>, so no request can arrive against a
        /// half-built table. Re-registering a name replaces it.
        /// </summary>
        public void Register(string name, Func<JsonElement, JsonNode?> verb)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(verb);
            _verbs[name] = verb;
        }

        /// <summary>
        /// Run the gates and, if they all pass, bind the loopback listener on an ephemeral port, mint the per-run
        /// token, write the handshake file, and wire the window. Idempotent, and a no-op that touches nothing when
        /// any gate refuses.
        /// <para>
        /// It also hooks <see cref="AppDomain.ProcessExit"/> to delete the handshake file, because the ordinary
        /// shutdown of a game is <c>quit</c> to <c>AppWindow.Close</c> to <c>Run</c> returning, and a head written
        /// from the wiring example never disposes anything on that path. Without the hook every run left a file
        /// naming a dead port. A hard crash still cannot be covered, which is why the file carries the pid and a
        /// bridge is told to check it is alive before trusting the rest.
        /// </para>
        /// </summary>
        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            if (!_options.Enabled || !EnvironmentAllows) return;

            _token = AutomationHandshake.NewToken();
            _listener = new AutomationListener(_options, _token, () => Frame, Submit);
            AutomationHandshake.Write(
                HandshakeFilePath, _listener.Port, _token, AutomationHandshake.CurrentProcessId, DateTimeOffset.UtcNow);
            string path = HandshakeFilePath;
            _processExit = (_, _) => AutomationHandshake.Delete(path);
            AppDomain.CurrentDomain.ProcessExit += _processExit;
            _running = true;
            AttachWindow();
        }

        /// <summary>Test seam (InternalsVisibleTo): whether the process-exit cleanup is currently subscribed, so a
        /// test can pin the subscribe and the unsubscribe without exiting the test process to observe them.</summary>
        internal bool HasProcessExitHandler => _processExit is not null;

        /// <summary>Test seam (InternalsVisibleTo): how many commands are parked on a frame that has not arrived, so
        /// a test can pump until one is genuinely waiting instead of sleeping and hoping.</summary>
        internal int WaitingCount
        {
            get { lock (_waitingLock) return _waiting.Count; }
        }

        /// <summary>
        /// Queue one request and hand back the task its reply completes on. <c>ping</c> is the one command answered
        /// here rather than on the window thread, because it is the bridge's readiness check and has to work in the
        /// window between the handshake file appearing and the first frame running. Everything else is queued, and a
        /// malformed argument fails fast here with a precise message rather than a frame later.
        /// <para>
        /// The disposed check and the enqueue are one atomic step, under the lock <see cref="Dispose"/> takes to
        /// set the flag. Read and enqueue as two steps left a window in which a command landed in a queue nobody
        /// would ever drain, and the socket thread waiting on its reply blocked for the life of the process.
        /// </para>
        /// </summary>
        public Task<AutomationReply> Submit(AutomationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            long frame = Frame;

            if (request.Command == "ping")
                return Task.FromResult(AutomationReply.Success(request.Id, frame, new JsonObject()));

            if (!TryPrepare(request, out PendingCommand? pending, out string? error))
                return Task.FromResult(AutomationReply.Failure(request.Id, frame, error!));

            lock (_submitLock)
            {
                if (_disposed)
                    return Task.FromResult(AutomationReply.Failure(request.Id, frame, StoppedError));
                _incoming.Enqueue(pending!);
            }
            return pending!.Completion.Task;
        }

        /// <summary>
        /// The input filter and the frame pump in one call: <c>AppWindow</c> invokes this once per frame with the
        /// snapshot <c>BuildInput()</c> just built, on the window thread, before anything downstream reads it.
        /// It advances the frame counter, fires any auto-release due, applies every queued command, completes the
        /// commands whose frame has arrived, and returns the composed snapshot.
        /// </summary>
        public InputState Pump(InputState real)
        {
            ArgumentNullException.ThrowIfNull(real);
            long frame = Interlocked.Increment(ref _frame);

            _injector.ExpireHolds(frame);
            while (_incoming.TryDequeue(out PendingCommand? pending)) Apply(pending, frame);
            CompleteDue(frame);

            InputState composed = _injector.Compose(real);
            _injector.EndFrame();
            return composed;
        }

        /// <summary>
        /// Unwire the window, fail every command still waiting so no connection hangs, then stop the listener and
        /// delete the handshake file. Safe to call twice.
        /// <para>
        /// <b>The order is the contract.</b> The failure reply is written by the SERVING thread, so a listener
        /// disposed first closes the socket out from under it and the client that asked for <c>step 9999</c> gets an
        /// EOF rather than the documented error line. So the commands fail first, the listener is given
        /// <see cref="ReplyDrainTimeout"/> to put those replies on the wire, and only then do the sockets close.
        /// </para>
        /// <para>
        /// <b>Threading.</b> Call it on the window thread, or once the loop has returned and nothing is pumping.
        /// The queue handoff and the waiting list are both locked, so a racing <see cref="Submit"/> or
        /// <see cref="Pump"/> cannot lose a command, but the window seams (<c>InputFilter</c>, the throttle) are
        /// plain property writes and the injector is documented as window-thread-only, so disposing from a third
        /// thread mid-frame is not supported.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            lock (_submitLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            DetachWindow();
            UnhookProcessExit();

            long frame = Frame;
            PendingCommand[] waiting;
            lock (_waitingLock)
            {
                waiting = _waiting.ToArray();
                _waiting.Clear();
            }
            foreach (PendingCommand pending in waiting)
                pending.Completion.TrySetResult(AutomationReply.Failure(pending.Request.Id, frame, StoppedError));
            while (_incoming.TryDequeue(out PendingCommand? queued))
                queued.Completion.TrySetResult(AutomationReply.Failure(queued.Request.Id, frame, StoppedError));

            _listener?.DrainReplies(ReplyDrainTimeout);
            _listener?.Dispose();
            _listener = null;
            if (_running) AutomationHandshake.Delete(HandshakeFilePath);
            _running = false;
            _token = null;
        }

        /// <summary>Drop the process-exit cleanup, so a disposed host leaves nothing subscribed to an event that
        /// outlives it and holds it alive.</summary>
        void UnhookProcessExit()
        {
            if (_processExit is null) return;
            try { AppDomain.CurrentDomain.ProcessExit -= _processExit; }
            catch (Exception) { }
            _processExit = null;
        }

        const string StoppedError = "automation host stopped";
    }
}
