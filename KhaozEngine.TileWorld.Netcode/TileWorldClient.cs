using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Simulation;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Handler for an opaque game message from the server. The engine never looks inside
/// <paramref name="payload"/>: what a <paramref name="kind"/> means is the game's business.</summary>
/// <param name="kind">The game-defined message kind.</param>
/// <param name="payload">The message body, a slice of the receive buffer that is only valid for the call.</param>
public delegate void TileClientMessageHandler(ushort kind, ReadOnlySpan<byte> payload);

/// <summary>
/// The tile client: prediction for the local player, a replication view for everybody else, and its OWN command
/// tick, deliberately phase-offset from the server's rather than driven by snapshot arrival. A client whose command
/// tick is slaved to incoming snapshots hides every ordering bug a real, independently-clocked client runs into,
/// which is why the loopback harness in this package's tests phases the two apart on purpose.
/// <para>Three clocks, and they are not the same clock. <see cref="Tick"/> drives the COMMAND clock, one command
/// predicted and sent per whole <see cref="TileWorldClientConfig.TickSeconds"/>. <see cref="Poll"/> drives the
/// RECEIVE path, once per frame, at whatever rate frames happen. <see cref="AdvancePresentation"/> drives the
/// RENDER clock, which eases the local player between command ticks and carries the delayed remote timeline. A
/// head calls all three every frame and the accumulators sort out the rest.</para>
/// <para>Nothing here touches a GPU, a window or a world document. The client is handed a baked
/// <see cref="TileCollisionMap"/> and speaks tiles, and <see cref="TilePresenter"/> is the one seam where a tile
/// becomes a place on screen.</para>
/// </summary>
public sealed partial class TileWorldClient : IDisposable
{
    readonly TileWorldClientConfig config;
    readonly NetClient net;
    readonly FixedTickHost clock;
    readonly Action<long> onCommandTick;
    readonly Dictionary<long, RemoteSample> remoteSamples = new();
    readonly List<long> goneRemotes = new();
    TileCommand queued;
    double presentationClock;
    bool seeded;

    /// <summary>
    /// Builds a client over a transport and the SAME collision map the server baked, which is the determinism
    /// contract in one argument: both heads step the same simulator over the same tiles.
    /// </summary>
    /// <param name="transport">The transport to the server. The CALLER owns it, see <see cref="Dispose"/>.</param>
    /// <param name="config">The clock, the cadence and the two goal bounds. See <see cref="TileWorldClientConfig"/>.</param>
    /// <param name="map">The baked collision map, from the same world files the server baked.</param>
    /// <param name="targets">Resolves interaction targets, null on a head with no interactions wired. When it is
    /// null an <see cref="TileCommandKind.Interact"/> is still sent and still answered by the server, it is simply
    /// not predicted, which reads as the click taking one round trip to land. This is the OBJECT space only. The
    /// second seam, the ENTITY space a <see cref="TileCommandKind.Attack"/> names, is not a parameter: the client
    /// builds its own <see cref="TileRemoteTargets"/> over itself, because the only honest answer to "where is that
    /// entity" on a client is the client's own newest snapshot.</param>
    /// <param name="connectToken">The token the door reads, from <see cref="TileProtocol.BuildConnectToken"/>.
    /// Null presents an empty token, which only a server with no gate admits.</param>
    /// <param name="registry">The replication registry both heads share, the mirror of
    /// <see cref="TileWorldServer"/>'s own parameter and the same rules. Null builds
    /// <see cref="TileProtocol.CreateRegistry"/>, which is the one a client with no game components needs. A game
    /// that registered its own at or above <see cref="TileProtocol.FirstGameTypeId"/> on the server MUST pass the
    /// matching registry here: an unregistered extension is SKIPPED on the way in, forward compatibility being the
    /// reason, so the components simply never arrive and nothing says so.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/>, <paramref name="config"/> or
    /// <paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="config"/> asks for a tick of zero seconds or
    /// less.</exception>
    public TileWorldClient(INetTransport transport, TileWorldClientConfig config, TileCollisionMap map,
        ITileTargets? targets = null, byte[]? connectToken = null, ReplicationRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(map);
        if (config.TickSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(config), config.TickSeconds, "TickSeconds must be > 0.");

        this.config = config;
        queued = TileCommand.Continue(RunMode);
        // TileRemoteTargets reads Prediction and LocalNetId LAZILY, so handing it `this` from inside the constructor
        // is safe: nothing calls the resolver before construction returns.
        Simulator = new TileMoveSimulator(map, config.StepTicks, targets, config.Move, new TileRemoteTargets(this));
        Prediction = new ClientPrediction<TileMoveState, TileCommand>(Simulator,
            config.Prediction ?? new PredictionSettings(config.TickSeconds, MaxPendingCommands: 64,
                HardSnapDistance: 0.5f, CorrectionRate: 8f, CorrectionDeadZone: 0.01f));
        View = new ClientReplicationView(registry ?? TileProtocol.CreateRegistry());
        World = new World();
        // A placeholder until the head has the world file. One metre tiles and the document default plane height
        // are the only honest guess available before a document is loaded, and Presenter is settable for exactly
        // this reason. It carries no tuning, so replacing it cannot change how anything MOVES: the glide is the
        // step's own tick count, which the simulation owns on both heads.
        Presenter = new TilePresenter(1f, TileWorldDocument.DefaultPlaneHeight);
        clock = new FixedTickHost(config.TickSeconds);
        onCommandTick = OnCommandTick;
        net = new NetClient(transport, connectToken);
    }

    /// <summary>The local player's prediction. <see cref="LocalPose"/> is what to DRAW, and
    /// <see cref="ClientPrediction{TState,TCommand}.PredictedState"/> is what the rules see.</summary>
    public ClientPrediction<TileMoveState, TileCommand> Prediction { get; }

    /// <summary>The remote view. Its entities live in <see cref="World"/> and its components are the ones the
    /// registry this client was built with registered, which is <see cref="TileProtocol.CreateRegistry"/> plus
    /// whatever the game added to it.</summary>
    public ClientReplicationView View { get; }

    /// <summary>The client-side ECS world remotes are applied into. A game adds its own components to these
    /// entities, and reads its own replicated ones off them.</summary>
    public World World { get; }

    /// <summary>The stepper, shared with the server BY CONSTRUCTION rather than by convention: the same type over
    /// the same map and the same cadence, which is what makes a misprediction a real disagreement.</summary>
    public TileMoveSimulator Simulator { get; }

    /// <summary>The tile-to-view bridge, a pure map from a tile point to a world position. Replace it once the
    /// world document is loaded (<c>new TilePresenter(document)</c>), so it carries the real tile size and plane
    /// height instead of the placeholder the constructor installed. It carries no tuning of its own, so replacing
    /// it cannot change how anything MOVES.</summary>
    public TilePresenter Presenter { get; set; }

    /// <summary>
    /// Where to DRAW the local player's BODY: <see cref="ClientPrediction{TState,TCommand}.RenderedState"/> through
    /// <see cref="Presenter"/>, which is the linear glide from <see cref="TileMoveState.StepFrom"/> into
    /// <see cref="TileMoveState.Tile"/> eased between command ticks, plus whatever is left of a decaying
    /// reconciliation offset. Call it once a frame, after <see cref="AdvancePresentation"/>.
    /// <para>THE BODY LAGS THE COMMITTED TILE, BY DESIGN, and this is the one thing to understand before drawing
    /// anything else off this client. A step commits its tile when it STARTS, so the rules answer every question
    /// (reach, occupancy, what a click resolves against) about <see cref="ClientPrediction{TState,TCommand}.PredictedState"/>'s
    /// <see cref="TileMoveState.Tile"/> while this pose is still walking into it: up to one whole step behind, half
    /// a tile on average, and zero at the instant the body lands. The lag is the price of the responsiveness the
    /// lead commit buys, and it is NOT smoothed away here. The mitigation is VISIBILITY, and it is the game's to
    /// draw: a true-tile marker on <c>PredictedState.Tile</c> and a highlight over the remaining
    /// <see cref="TileMoveState.Route"/>, both mapped with <see cref="TilePresenter.PoseAt(TileCoord, TileDirection)"/>.
    /// See <c>docs/USING-KHAOZENGINE.md</c> for the overlay reads and
    /// <c>docs/design/TILE-WORLD-NETCODE-DESIGN-2026-08-22.md</c> section 5.2 for why tightening the curve was
    /// tried twice and rejected.</para>
    /// <para>The correction offset IS folded in, because it is the prediction layer's and it is anchored to the
    /// same <see cref="TileMoveState.Position"/> this draws: a rebase keeps the drawn position continuous and the
    /// offset decays off it, which is the whole reason a misprediction does not pop. Both axes are the prediction
    /// layer's own, so a teleport, a hard snap and an ordinary sub-tile correction are all handled there, once,
    /// rather than being re-decided here.</para>
    /// </summary>
    public TilePose LocalPose => Presenter.LocalPose(Prediction);

    /// <summary>
    /// The run toggle this client is holding, which rides on EVERY command rather than on the click that started a
    /// walk. A head sets it from its run button.
    /// <para>It matters that this is not <see cref="TileCommand.None"/>: None is Continue at
    /// <see cref="TileMoveMode.Walk"/>, so a client that sent it while a route played out would hold run for
    /// exactly one tick and then quietly drop to a walk. A change lands at the start of the NEXT step, never
    /// mid-step, which is the simulator's rule on both heads.</para>
    /// </summary>
    public TileMoveMode RunMode { get; set; } = TileMoveMode.Walk;

    /// <summary>The local player's net id, -1 before the first snapshot names it.</summary>
    public long LocalNetId { get; private set; } = -1;

    /// <summary>True once the session handshake completed. False again the moment it drops.</summary>
    public bool IsJoined { get; private set; }

    /// <summary>The newest server tick seen, -1 before the first snapshot.</summary>
    public long ServerTick { get; private set; } = -1;

    /// <summary>Reconciliations that moved the local player at all, snaps included. A healthy session on a clean
    /// map costs ZERO of these, because both heads replay the same commands over the same tiles.</summary>
    public int CorrectionCount { get; private set; }

    /// <summary>Reconciliations that CUT rather than glided: the local player was on a different SQUARE than the
    /// server, which is a disagreement about the world rather than about timing. See
    /// <see cref="TileWorldClientConfig.Prediction"/> for the threshold and why it is half a tile.</summary>
    public int SnapCount { get; private set; }

    /// <summary>Snapshots refused whole by the decoder. A malformed component frame is never partly applied and
    /// never becomes a reconciliation basis, because a basis rebuilt from half a frame is a plausible-looking
    /// answer to a question nobody asked. Counted so a head can log a wire that has started lying.</summary>
    public int DroppedSnapshotCount { get; private set; }

    /// <summary>Clicks dropped before they were ever sent: a goal on a plane the world does not have, a goal on a
    /// plane the player is not standing on, or a goal in a region this client has not loaded.</summary>
    public int DroppedClickCount { get; private set; }

    /// <summary>The reason token the door refused with, or null. A TOKEN rather than prose: the client matches it
    /// and shows its own localized string. See <c>KhaozEngine.Netcode.HandshakeToken</c>.</summary>
    public string? RefusedReason { get; private set; }

    /// <summary>Raised with the reason token when the door refuses the connection. Terminal: no join follows
    /// one.</summary>
    public event Action<string>? RefusedAtDoor;

    /// <summary>Raised when the session drops, for any reason the door did not already refuse.</summary>
    public event Action? Disconnected;

    /// <summary>Raised with a server notice's reason token, every notice included, the ones with their own event
    /// among them. See <see cref="TileServerReason"/> for the tokens this package defines and why a game is
    /// expected to add its own.</summary>
    public event Action<string>? NoticeReceived;

    /// <summary>Raised when the server refuses a pending interaction because the player cannot get to what they
    /// clicked. The typed form of the <see cref="TileServerReason.CannotReach"/> notice, so a head can drop its own
    /// pending click at the same moment the server dropped the authoritative one.</summary>
    public event Action? CannotReach;

    /// <summary>Raised once per swing this client was told about: the ones whose TARGET is in its own area of
    /// interest, misses included. A miss carries <c>Landed = false</c> and <c>Amount = 0</c> and STILL draws a
    /// hitsplat, because a fight with invisible misses reads as a broken fight.
    /// <para>Never derive a fight from replicated health instead. Two hits on one tick collapse into one delta and a
    /// miss moves health by zero, so a fight drawn from deltas shows fewer, larger, later hitsplats than the fight
    /// the server ran.</para></summary>
    public event Action<TileCombatEvent>? CombatEvent;

    /// <summary>Raised with a remote's net id the first frame this client has a drawing state for it: it entered the
    /// area of interest, or it was just spawned inside it. The diff is already computed every frame, so this costs
    /// nothing and saves a head keeping its own copy of it.</summary>
    public event Action<long>? RemoteEntered;

    /// <summary>Raised with a remote's net id the frame it stops being drawn: it left the area of interest, it was
    /// despawned, or it died. What a per-remote overlay stack (a nameplate, a hitsplat stack) prunes on.</summary>
    public event Action<long>? RemoteLeft;

    /// <summary>
    /// Raised when the local player's position changed DISCONTINUOUSLY: the server moved them
    /// (<c>TileWorldServer.SetPlayerState</c> with <c>teleport: true</c>, so a respawn, an admin move or a fast
    /// travel), or prediction was seeded and had no earlier position to be continuous with. See
    /// <see cref="ReconciliationResult.Teleported"/> for the exact set.
    /// <para>It is NOT a mispredicted step. A step the two heads disagreed about cuts too, and is counted in
    /// <see cref="SnapCount"/>, but the avatar is one square from where it was drawn and the world around it is
    /// the same world. This event is the expensive one: a head answers it by snapping its follow camera, running a
    /// screen transition and re-centring anything keyed to the player's position. The FIRST snapshot to reconcile
    /// after a join raises it, which is correct rather than a quirk, since the head has nothing to be continuous
    /// with at that point either.</para>
    /// </summary>
    public event Action? Teleported;

    /// <summary>Raised for an opaque game message.</summary>
    public event TileClientMessageHandler? OnGameMessage;

    /// <summary>Net ids of the remotes currently drawn. The local player is never among them.</summary>
    public IReadOnlyCollection<long> RemoteNetIds => remoteSamples.Keys;

    /// <summary>
    /// Latest-wins intent for the NEXT command tick, called from a click handler. A second click before the tick
    /// replaces the first, which is what makes a rapid double click feel like one decision rather than two.
    /// <para>The command's own <see cref="TileCommand.Mode"/> is adopted as <see cref="RunMode"/>, so the toggle a
    /// click was made under is the one every following <see cref="TileCommand.Continue"/> carries. A head that
    /// builds its clicks from <see cref="RunMode"/> sees no change from this, and one that does not still gets a
    /// walk that keeps running.</para>
    /// <para>Two kinds of click are dropped HERE rather than sent. A goal the SIMULATOR would refuse whole (a walk
    /// or an interaction on another plane, <see cref="TileMoveSimulator.Accepts"/>) would spend a command slot to
    /// achieve nothing on either head. A goal in a region this client has not loaded names ground neither head can
    /// path into, and the collision map is region-sparse, so there is nothing to path over. Both are counted in
    /// <see cref="DroppedClickCount"/> so a head can log them. Everything else is sent verbatim, an out-of-range
    /// goal included, because the server answers that one by REWRITING the command rather than dropping the tick
    /// and this client mirrors the rewrite when it predicts.</para>
    /// <para>Before the first snapshot the predicted state is still the default one, standing at tile (0,0) on
    /// plane 0, so a click made during the join is measured against that and usually dropped. That is deliberate:
    /// nothing is sent before the seed anyway (see <see cref="Tick"/>), and queueing the click instead would
    /// predict it from a tile the player was never on. A head that wants the click to survive its loading screen
    /// re-issues it once <see cref="LocalNetId"/> is set.</para>
    /// </summary>
    /// <param name="command">The click to send on the next command tick.</param>
    public void Queue(in TileCommand command)
    {
        RunMode = command.Mode;
        if (!Simulator.Accepts(Prediction.PredictedState, command)
            || (command.Kind == TileCommandKind.WalkTo && !IsLoaded(command.Goal)))
        {
            DroppedClickCount++;
            return;
        }
        queued = command;
    }

    // A goal this client could not path to whatever the server said: off the planes the world has, or in a region
    // that was never loaded. Region-sparse is the operative word - the map answers "no region" rather than "no
    // floor", so a click past the loaded edge is not a blocked tile, it is a question the pathfinder cannot be
    // asked.
    bool IsLoaded(TileCoord goal) =>
        goal.Plane >= 0 && goal.Plane < config.PlaneCount
        && Simulator.Map.HasRegion(RegionCoord.Of(goal.X, goal.Z));

    /// <summary>
    /// Advances the client's own command clock, and on each whole tick predicts one command and sends it. Returns
    /// the number of whole ticks stepped, which a head can ignore.
    /// <para>The accumulator advances even before the session exists, and that is what carries the client's PHASE
    /// across the join: a client whose first command tick fell exactly on the server's would never exercise the
    /// ordering a real one lives with. What is suppressed until the first snapshot has seeded prediction is the
    /// predict-and-send itself, because a command sent before the seed burns a sequence number that
    /// <see cref="ClientPrediction{TState,TCommand}.Reset"/> then rewinds, and the server refuses the re-used
    /// number as stale.</para>
    /// <para>A frame that covers many ticks (a stall, a breakpoint, a long GC) runs at most eight of them and SHEDS
    /// the rest, which is <see cref="FixedTickHost"/>'s own rule. Catching the whole backlog up would fire a burst
    /// of commands describing intent the player no longer has, and movement is latest-wins on both heads anyway.
    /// </para>
    /// </summary>
    /// <param name="elapsedSeconds">Seconds since the last call. Negative is treated as zero.</param>
    /// <returns>The number of whole command ticks this call produced, at most eight.</returns>
    public int Tick(float elapsedSeconds) => clock.Advance(elapsedSeconds, onCommandTick);

    void OnCommandTick(long tick)
    {
        // Both gates, and IsJoined is the one that matters after a drop: nothing re-handshakes a NetClient, so a
        // client whose link died can never rejoin, and one that kept predicting and sending would walk an avatar
        // with no authority behind it into a socket nobody is reading. A reconnect means a NEW TileWorldClient,
        // which is why ClientPrediction.Reseed is deliberately never called here.
        if (!IsJoined || !seeded) return;
        TileCommand sent = queued;
        queued = TileCommand.Continue(RunMode);
        // PREDICT the admitted form and SEND the raw one. The server rewrites an out-of-range goal itself, off the
        // command it received, so sending the rewrite instead would be telling it a different story than the one
        // the player told, and any disagreement about the bound would then be invisible rather than corrected.
        int seq = Prediction.Predict(Admit(sent));
        net.Send(TileProtocol.EncodeCommand(seq, sent), NetChannelReliability.ReliableOrdered);
    }

    // The client half of TileWorldServer.Admit, and it has to be the same rule to the letter. A goal beyond the
    // reach bound becomes Continue at the mode the COMMAND carried, so the walk is refused while the run toggle
    // that rode in with it still applies, and an Attack naming target 0 becomes Continue for the reason the server
    // gives: zero is CombatTarget's own "not fighting" value, so it is a malformed command rather than a click at
    // something that went away. Everything else passes through untouched, a cross-plane goal included:
    // the simulator drops that one whole on both heads, and rewriting it here would apply a mode the server never
    // did. Queue already refused the cross-plane click, so this is the backstop for a command built elsewhere.
    TileCommand Admit(in TileCommand cmd) =>
        (cmd.Kind == TileCommandKind.WalkTo && !GoalInRange(Prediction.PredictedState, cmd.Goal))
        || (cmd.Kind == TileCommandKind.Attack && cmd.Target == 0)
            ? TileCommand.Continue(cmd.Mode)
            : cmd;

    // Both of the server's refusals, in its order. The plane bound is first and cheapest, and the wire encoder
    // refuses a plane over 255 before either of them is asked.
    bool GoalInRange(in TileMoveState state, TileCoord goal)
    {
        if (goal.Plane >= config.PlaneCount) return false;
        // In LONG, matching the server line for line. Nothing a client builds locally reaches int.MinValue apart,
        // but the two predicates ARE the determinism contract and a difference here is a difference nobody would
        // find until it mispredicted. See TileWorldServer.GoalInRange for why the long is load-bearing there.
        long dx = (long)goal.X - state.Tile.X, dz = (long)goal.Z - state.Tile.Z;
        return Math.Max(Math.Abs(dx), Math.Abs(dz)) <= config.MaxGoalRadius;
    }

    /// <summary>
    /// Advances the render clocks: the prediction's inter-tick easing and correction decay for the local player,
    /// and the delayed remote timeline for everybody else. Call it once per frame, after <see cref="Poll"/> and
    /// before drawing.
    /// <para>The remote timeline is resampled HERE rather than on snapshot arrival, so a remote advances with the
    /// frame rate instead of once per packet. That is the whole difference between a remote that glides and one
    /// that hops at the tick rate.</para>
    /// </summary>
    /// <param name="dt">Seconds since the last frame. Anything that is not a finite positive number of seconds
    /// (negative, zero, infinite, or not a number) is treated as zero and advances nothing.</param>
    public void AdvancePresentation(float dt)
    {
        // The clock here ACCUMULATES, which is why this is a finiteness test rather than a clamp. Math.Max(0f, NaN)
        // is NaN under IEEE, so a single NaN frame would take presentationClock, and with it the remote render
        // timeline, out for the rest of the session rather than for a frame. An infinite dt is refused in the same
        // breath: it would carry the render time past every buffered sample at once, park every remote on its
        // newest one and never come back. Both mean the caller handed over a broken frame clock, and the honest
        // answer to a frame that took no valid amount of time is to draw the previous one again. The sanitized
        // value is what the prediction layer is handed too, so one guard covers both render clocks.
        float step = float.IsFinite(dt) && dt > 0f ? dt : 0f;
        presentationClock += step;
        Prediction.AdvancePresentation(step);
        if (LocalNetId < 0) return;
        double renderTime = RenderTime;
        View.InterpolateAt(World, renderTime, excludeNetId: LocalNetId);
        RefreshRemoteSamples(renderTime);
    }

    // Where the remote timeline is right now: the render clock, less the delay that buys room for a lost snapshot.
    double RenderTime => presentationClock - config.InterpolationDelayTicks * config.TickSeconds;

    /// <summary>Sends an opaque game message to the server.</summary>
    /// <param name="kind">The game-defined message kind.</param>
    /// <param name="payload">The body, at most <see cref="TileProtocol.MaxGameMessageBytes"/> bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is over the cap.</exception>
    public void SendGameMessage(ushort kind, ReadOnlySpan<byte> payload) =>
        net.Send(TileProtocol.EncodeGameMessage(TileProtocol.ClientFrameGameMessage, kind, payload),
            NetChannelReliability.ReliableOrdered);

    /// <summary>
    /// Drops the client's own per-remote bookkeeping. The TRANSPORT is the caller's to dispose, because the caller
    /// built it and may well reconnect over the same one, and a client that closed a transport it did not own would
    /// take a shared connection down with it.
    /// </summary>
    public void Dispose()
    {
        remoteSamples.Clear();
        goneRemotes.Clear();
        liveRemotes.Clear();
        enteredRemotes.Clear();
        latestTiles.Clear();
        goneLatest.Clear();
        liveLatest.Clear();
        decodedCombat.Clear();
    }
}
