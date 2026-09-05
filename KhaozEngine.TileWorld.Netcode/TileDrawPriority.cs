using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// ONE BODY PER TILE AT REST. Which actor standing on each tile is the one to draw, which are behind it, and how
/// far through fading in or out of that answer each of them is, rebuilt from scratch every frame. The local
/// player wins their own tile outright, and both tiles of a step while one is in flight. Every other tile goes to
/// the HIGHEST net id on it.
/// <para>WHY, and it is a presentation rule rather than a rules one. A tile game draws every body on the tile
/// centre, so a stack of them is a smear of overlapping meshes that reads as one wrong-looking creature, and the
/// body a player can least afford to lose in that smear is their own: standing under a bank crowd, an avatar the
/// player cannot see is an avatar they cannot aim from. So the stack collapses to one. OSRS answers the same
/// question with PID, a per-tick priority every player carries, and this is the same shape with a stable key
/// instead of a rotating one.</para>
/// <para>THE ANSWER IS A WEIGHT, NOT A BOOLEAN, and that is what keeps the rule from popping. A tile commits when
/// a step STARTS and the body glides in over the rest of it, so an actor that loses its tile on the frame the
/// tile changes is hidden while its body is still visibly a whole step away from where the rule thinks it is:
/// enemies walking onto the player vanish in the open instead of walking under them. <see cref="Weight"/> is the
/// visibility to draw a body at, 0 through 1, and it moves ACROSS the step rather than at its start. A body
/// losing a tile it is stepping into falls to 0 exactly as it comes to rest there, so it walks visibly under the
/// winner and is gone the moment it lands. A body that regains its tile by stepping OUT rises back to 1 as that
/// step lands, so it walks visibly out from under. A body that loses or regains while STANDING STILL (the winner
/// stepped onto it, or walked off it) has no step to ride, so it crosses over <see cref="FadeSeconds"/> instead.
/// The rule at rest is unchanged and exact: one body per tile.</para>
/// <para>WHAT A HEAD DOES WITH THE NUMBER IS THE HEAD'S CALL. The engine hands over the weight and draws nothing
/// itself. A renderer with per-instance alpha multiplies it in, one without can scale the body, dither it, or
/// pick a stipple pattern from it. <see cref="IsDrawn"/> is <see cref="Weight"/> above zero, so a head that wants
/// the old hard cut simply keeps calling it and reads a body that is still fading as still drawn.</para>
/// <para>THE KEY IS THE NET ID, and its only job is to be STABLE. It is arbitrary rather than meaningful: a net
/// id is handed out by <c>NetIdAllocator</c> in increasing order, so the highest one on a tile is the most
/// recently spawned actor there, which is not a fact anybody should design around. What matters is that it does
/// not move for an actor's life, so a crowd standing still draws the SAME body every frame. An order derived from
/// anything that moves (distance to the camera, distance to the player, arrival time on the tile) re-decides
/// itself mid-step, and the body under the cursor swaps between frames while nothing on screen appears to have
/// changed.</para>
/// <para>WHICH TILE each actor is judged on is the other half of the rule, and the two heads answer it
/// differently ON PURPOSE. The local player is judged on their PREDICTED tile
/// (<c>Prediction.PredictedState.Tile</c>), because that is the tile the local rules have already committed them
/// to. A remote is judged on its COMMITTED replicated tile off the delayed render timeline
/// (<see cref="TileWorldClient.TryGetRemoteTile"/>), which is the tile the body being drawn is walking INTO, so
/// the hide happens on the same timeline as the picture. Judging a remote on
/// <see cref="TileWorldClient.TryGetLatestRemoteTile(long, out TileCoord)"/> instead would hide it
/// <see cref="TileWorldClientConfig.InterpolationDelayTicks"/> before its body arrived, which is a remote
/// vanishing into a tile it is visibly still two ticks away from. Neither read is the drawn POSITION: a body
/// commits its tile when the step starts and glides in afterwards, so a tile read leads the body it names by up
/// to a whole step. The weight is what closes that lead, by spending it rather than by shortening it.</para>
/// <para>THE LOCAL PLAYER CLAIMS BOTH TILES OF A STEP IN FLIGHT, and that is what makes the own-body guarantee
/// hold while walking. <see cref="TileMoveState"/> commits the destination on the tick the step STARTS and the
/// body glides in over the rest of it, so through the whole step the drawn local body is somewhere between
/// <see cref="TileMoveState.StepFrom"/> and <see cref="TileMoveState.Tile"/>. Claiming the destination alone
/// leaves the tile being vacated to the highest net id standing on it, and on the tick the step commits that
/// body draws at the same world position as the local one, which is the exact failure this class exists to
/// remove. So while <see cref="TileMoveState.IsStepping"/> the local player wins the leaving tile as well as the
/// entering one. The drawn body is inside that pair for the whole step, so nothing is ever drawn over it. Both
/// claims name the SAME net id, so the pair is one entry in <see cref="Drawn"/> and two in the tile map. The
/// local player's own weight is pinned at 1 and never fades, because they are drawn unconditionally.</para>
/// <para>A REMOTE KEEPS ONE TILE, deliberately, so the lead the local claim removes is still there for everybody
/// else: a remote mid-step is judged on the tile it is walking into, and a body standing on the tile it is
/// leaving wins that tile and draws over the remote's own body. It lasts exactly one step, it ends when the step
/// lands, and it is the same lead every other tile read in this package carries. A remote is not the body a
/// player aims from, and paying two tiles per remote to shave it would hide a second body in every crowd for the
/// length of every step somebody takes.</para>
/// <para>Per frame at 60 Hz and allocation free after the first rebuild: the buffers here are reused, the rule is
/// one pass over the actors plus one over the winners, and there is no LINQ and no sort. The weights add one
/// dictionary keyed by net id, pruned in the same rebuild an actor stops being listed in. Hold ONE of these on
/// the head and call <see cref="Rebuild(TileWorldClient, float)"/> once a frame, after
/// <see cref="TileWorldClient.AdvancePresentation"/> and before drawing, with the same frame <c>dt</c>. The draw
/// loop walks a collected list rather than <see cref="TileWorldClient.RemoteNetIds"/>, which is interface-typed
/// and boxes an enumerator every frame, the cost <see cref="TileWorldClient.CollectRemoteSteps"/> exists to
/// avoid.</para>
/// <code>
/// priority.Rebuild(client, dt);
/// TilePose me = client.LocalPose;                 // the local player is always drawn, always at weight 1
/// Draw(playerMesh, me.Position, me.Yaw, alpha: 1f);
/// client.CollectRemoteTiles(remotes);             // a List&lt;(long NetId, TileCoord Tile)&gt; the head keeps
/// foreach ((long netId, TileCoord _) in remotes)
/// {
///     float weight = priority.Weight(netId);
///     if (weight &gt; 0f &amp;&amp; client.TryGetRemotePose(netId, out TilePose pose))
///         Draw(remoteMesh, pose.Position, pose.Yaw, alpha: weight);
/// }
/// </code>
/// <para>Nothing here is a rules input. Hiding a body changes what is DRAWN and nothing else: the hidden actor is
/// still on its tile, still replicated, still a legal click target and still swinging. A head that hides
/// nameplates or click targets along with the body is making a second, separate decision and should make it
/// deliberately, and a head that fades a nameplate on the body's weight should say so too.</para>
/// </summary>
public sealed class TileDrawPriority
{
    /// <summary>The one <c>localNetId</c> value that means "no local player", and the value
    /// <see cref="TileWorldClient.LocalNetId"/> carries until the first snapshot names the local actor.
    /// <para>It is a SENTINEL rather than a sign test, and that is load-bearing: <c>NetIdAllocator.Pack</c> is
    /// <c>nodeId &lt;&lt; 48 | counter</c> over a node id up to 65535, so every node from 32768 up hands out
    /// NEGATIVE net ids. A rule that read "negative" as "not logged in" would silently drop the local player's
    /// own claim on a sharded fleet that far out, which is the one thing this class must never do.</para></summary>
    public const long NoLocalPlayer = -1;

    /// <summary>What <see cref="FadeSeconds"/> starts at: a quarter of a second, which is about one and a half
    /// walking steps at the tick rate this package's clients run. Long enough to read as a body leaving rather
    /// than a body deleted, short enough that a crowd shuffling on the spot is not a wall of half-drawn
    /// meshes.</summary>
    public const float DefaultFadeSeconds = 0.25f;

    // Under this a step has no remaining fraction to spread a fade over, so the fixed window takes it instead.
    // Comparing against zero would put a body one float tick from the end of its step on a divide that produces
    // an enormous rate rather than the intended one.
    const float StepEpsilon = 1e-6f;

    // The client's remotes, collected once per rebuild so the rule can read them as a span, with the step
    // progress the fade is paced by. Grows to the biggest crowd this client has seen and stops, which is the
    // whole of the per-frame allocation story.
    readonly List<(long NetId, TileCoord Tile, float StepProgress)> actors = new();
    readonly Dictionary<TileCoord, long> winners = new();
    readonly HashSet<long> drawn = new();
    // One entry per body this rule has an opinion about, which is every actor the last rebuild was handed plus
    // the local player. Swept in the rebuild an actor stops being listed in, so a departed body cannot come back
    // later at the weight it left on.
    readonly Dictionary<long, Fade> fades = new();
    readonly List<long> gone = new();
    int stamp;
    float fadeSeconds = DefaultFadeSeconds;

    /// <summary>How long a body takes to fade in or out when it has no step to ride, in seconds, defaulting to
    /// <see cref="DefaultFadeSeconds"/>. This is the window for a loss or a gain that happens while the body
    /// STANDS STILL: the winner stepped onto it, or stepped off it, and the losing body is not moving at all, so
    /// there is no glide to spend the change across.
    /// <para>Zero makes those cases CUT, which is the pre-weight behaviour for them and is a legitimate choice
    /// for a head that draws bodies with no alpha at all. It does not disable the fade a body rides across its
    /// own step, which is paced by the step and not by this.</para></summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not a number.</exception>
    public float FadeSeconds
    {
        get => fadeSeconds;
        set
        {
            if (!(value >= 0f))
                throw new ArgumentOutOfRangeException(nameof(value), value, "FadeSeconds must be zero or more.");
            fadeSeconds = value;
        }
    }

    /// <summary>The net ids this rebuild is drawing SOMETHING for: the winner of every occupied tile, plus every
    /// body still fading out of one it lost. The LOCAL player's id is among them whenever the client has one,
    /// because the local player is always drawn, so a head walking this collection to draw bodies has to draw
    /// that one through <see cref="TileWorldClient.LocalPose"/> rather than
    /// <see cref="TileWorldClient.TryGetRemotePose"/>. Walking a list filled by
    /// <see cref="TileWorldClient.CollectRemoteTiles"/> and asking <see cref="Weight"/> avoids the question
    /// entirely, and is also the allocation-free shape: this collection is interface-typed, so enumerating it
    /// boxes the set's enumerator once a frame.
    /// <para>THE LIVE SET, not a snapshot. A rebuild CLEARS and refills this same instance, so a reference held
    /// across frames changes under its holder, and enumerating it while
    /// <see cref="Rebuild(TileWorldClient, float)"/> runs on it throws
    /// <see cref="InvalidOperationException"/>. Copy it if you need last frame's answer.</para></summary>
    public IReadOnlyCollection<long> Drawn => drawn;

    /// <summary>How many bodies this rebuild is drawing, fading ones included. NOT the number of occupied tiles:
    /// one body can hold two of them. The local player holds both tiles of a step in flight, and a caller's
    /// roster may list one net id on two tiles. <see cref="Drawn"/> is a set of net ids, so either case counts
    /// once here and twice in <see cref="TryGetDrawn"/>.</summary>
    public int Count => drawn.Count;

    /// <summary>How visible <paramref name="netId"/> is this frame, 0 through 1: 1 for the body that owns its
    /// tile, 0 for one fully hidden behind another, and in between for one crossing between those states. Zero
    /// for an id this priority has never seen, because an actor with no tile is an actor with no pose to draw.
    /// <para>THE UNITS ARE THE HEAD'S. This is a visibility fraction and nothing more: multiply it into an alpha,
    /// into a scale, or threshold it into a dither pattern. The engine never draws with it.</para></summary>
    /// <param name="netId">The actor's net id.</param>
    /// <returns>The weight to draw the actor at.</returns>
    public float Weight(long netId) => fades.TryGetValue(netId, out Fade fade) ? fade.Weight : 0f;

    /// <summary>True when <paramref name="netId"/> should be drawn at all, which is <see cref="Weight"/> above
    /// zero. A body part way through fading out is still drawn, and it is what the pre-weight version of this
    /// rule would have hidden outright, so a head that keeps asking this question keeps working and gets the
    /// body walking under the winner for free.
    /// <para>False for an id this priority has never seen, for the same reason <see cref="Weight"/> is zero for
    /// it.</para></summary>
    /// <param name="netId">The actor's net id.</param>
    /// <returns>True when the actor should be drawn.</returns>
    public bool IsDrawn(long netId) => Weight(netId) > 0f;

    /// <summary>The one actor that OWNS a tile, for a head that asks by place rather than by actor (a click
    /// resolving to the body a player can actually see, a tooltip over a tile). The winner alone: a body fading
    /// out of the tile is not the answer here even while it is still being drawn, because exactly one actor owns
    /// a tile and that is the whole rule.</summary>
    /// <param name="tile">The tile to ask about, plane included.</param>
    /// <param name="netId">The net id drawn there, zero when false. Zero is never a live net id, so it is the
    /// same "nobody" this package's combat target uses.</param>
    /// <returns>True when any actor stands on <paramref name="tile"/>.</returns>
    public bool TryGetDrawn(TileCoord tile, out long netId) => winners.TryGetValue(tile, out netId);

    /// <summary>
    /// Rebuilds from a live client WITHOUT fading anything: every weight lands on 0 or 1 in one frame, which is
    /// this rule as it behaved before weights existed. Kept for a head that draws bodies it cannot fade and does
    /// not want to think about it.
    /// <para>Prefer <see cref="Rebuild(TileWorldClient, float)"/>. This overload hides an actor on the frame its
    /// committed tile changes, which is up to a whole step before its body arrives there, so remotes walking onto
    /// an occupied tile disappear in the open.</para>
    /// </summary>
    /// <param name="client">The client to read. Not retained.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public void Rebuild(TileWorldClient client) => Rebuild(client, dt: 0f, snap: true);

    /// <summary>
    /// Rebuilds from a live client: every remote it is drawing on its committed tile and at its own step
    /// progress, plus the local player on their predicted one AND, while a step is in flight, on the tile that
    /// step is leaving. Call it once a frame, after <see cref="TileWorldClient.AdvancePresentation"/> so the
    /// remote set and the progress are the ones this frame will draw, and with the same <paramref name="dt"/>.
    /// <para>Until the first snapshot names the local actor, <see cref="TileWorldClient.LocalNetId"/> is
    /// <see cref="NoLocalPlayer"/>, so nothing claims a local tile and the remotes (of which there are none yet)
    /// settle every tile between themselves.</para>
    /// <para>A body seen for the FIRST time starts at its answer rather than fading to it, so an actor that
    /// spawns into a crowd is hidden at once and one that spawns alone is drawn at once. A fade is for a body
    /// that was already on screen and CHANGED, which is the only case a player can perceive as a pop.</para>
    /// </summary>
    /// <param name="client">The client to read. Not retained.</param>
    /// <param name="dt">Seconds since the last rebuild, the frame's own. Anything that is not a finite positive
    /// number advances no fixed-window fade, exactly as <see cref="TileWorldClient.AdvancePresentation"/> treats
    /// the same frame. A fade riding a step is paced by the step and is unaffected.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public void Rebuild(TileWorldClient client, float dt) => Rebuild(client, dt, snap: false);

    void Rebuild(TileWorldClient client, float dt, bool snap)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.CollectRemoteSteps(actors);
        // The predicted state is read ONCE, so the tile claimed and the tile being left cannot come from two
        // different frames of prediction.
        TileMoveState local = client.Prediction.PredictedState;
        Rebuild(client.LocalNetId, local.Tile, local.IsStepping ? local.StepFrom : null,
            CollectionsMarshal.AsSpan(actors), dt, snap);
    }

    /// <summary>
    /// Rebuilds from a caller's own roster WITHOUT fading anything, the roster form of
    /// <see cref="Rebuild(TileWorldClient)"/>. Prefer the overload that takes a step progress and a
    /// <c>dt</c>.
    /// </summary>
    /// <param name="localNetId">The local player's net id, or <see cref="NoLocalPlayer"/> when there is no local
    /// player, in which case neither local tile is read and every tile is settled by net id. Any OTHER negative
    /// value is a net id like any other, because a packed one can be negative.</param>
    /// <param name="localTile">The tile the local player is committed to, their PREDICTED tile on a live
    /// client.</param>
    /// <param name="localLeaving">The tile the local player's step in flight is walking OUT of
    /// (<see cref="TileMoveState.StepFrom"/> while <see cref="TileMoveState.IsStepping"/>), or null when they are
    /// standing still. Claimed alongside <paramref name="localTile"/>, because the drawn body is between the two
    /// for the whole step. Passing <paramref name="localTile"/> again is the same as passing null.</param>
    /// <param name="others">Every other actor and the tile it is committed to, in any order. An entry whose net
    /// id is <paramref name="localNetId"/> is skipped, so a roster that includes the local player is fine.</param>
    public void Rebuild(long localNetId, TileCoord localTile, TileCoord? localLeaving,
        ReadOnlySpan<(long NetId, TileCoord Tile)> others)
    {
        Select(localNetId, localTile, localLeaving, others, winners, drawn);
        Advance(localNetId, others, dt: 0f, snap: true);
    }

    /// <summary>
    /// Rebuilds from a caller's own roster, for a head that draws bodies this client does not own (a replay, a
    /// server-side view, a game whose actors live outside the replication view). The rule is the same one
    /// <see cref="Rebuild(TileWorldClient, float)"/> applies, over the actors you hand it.
    /// </summary>
    /// <param name="localNetId">The local player's net id, or <see cref="NoLocalPlayer"/> when there is no local
    /// player, in which case neither local tile is read and every tile is settled by net id. Any OTHER negative
    /// value is a net id like any other, because a packed one can be negative.</param>
    /// <param name="localTile">The tile the local player is committed to, their PREDICTED tile on a live
    /// client.</param>
    /// <param name="localLeaving">The tile the local player's step in flight is walking OUT of, or null when they
    /// are standing still. Claimed alongside <paramref name="localTile"/>.</param>
    /// <param name="others">Every other actor, the tile it is COMMITTED to, and how far through the step into
    /// that tile it is: 0 as the step commits, 1 once the body has come to rest there, which is also the value a
    /// body that is not stepping at all carries. <see cref="TilePresenter.StepFraction"/> is that number for a
    /// state you hold, and <see cref="TileWorldClient.CollectRemoteSteps"/> is it for a live client's whole
    /// crowd. A value outside 0 through 1, or one that is not a number, is read as 1.</param>
    /// <param name="dt">Seconds since the last rebuild, for the fades that have no step to ride.</param>
    public void Rebuild(long localNetId, TileCoord localTile, TileCoord? localLeaving,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress)> others, float dt)
        => Rebuild(localNetId, localTile, localLeaving, others, dt, snap: false);

    void Rebuild(long localNetId, TileCoord localTile, TileCoord? localLeaving,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress)> others, float dt, bool snap)
    {
        Begin(winners, drawn);
        for (int i = 0; i < others.Length; i++) Offer(localNetId, others[i].NetId, others[i].Tile, winners);
        Settle(localNetId, localTile, localLeaving, winners, drawn);
        Advance(localNetId, others, dt, snap);
    }

    /// <summary>
    /// THE RULE ITSELF, pure and allocation free: no state of its own, both outputs owned by the caller, and the
    /// same inputs always give the same answer. The instance methods above are this with the buffers held for
    /// you, plus the per-body weights, which need memory across frames and so cannot live here.
    /// <para>Both outputs are CLEARED first. <paramref name="winners"/> ends up as tile to the net id drawn on
    /// it, and <paramref name="drawn"/> as those net ids on their own, which is the membership test a draw loop
    /// wants. The second is derivable from the first and is built anyway, because deriving it per query means
    /// scanning every tile for every body.</para>
    /// </summary>
    /// <param name="localNetId">The local player's net id, or <see cref="NoLocalPlayer"/> for no local player.
    /// Every other value is a net id, negative ones included: a packed net id from a high node is negative, so
    /// this is a sentinel test and not a sign test.</param>
    /// <param name="localTile">The tile the local player is committed to. Not read when
    /// <paramref name="localNetId"/> is <see cref="NoLocalPlayer"/>.</param>
    /// <param name="localLeaving">The tile the local player's step in flight is walking OUT of, claimed alongside
    /// <paramref name="localTile"/>, or null when they stand still. Not read when
    /// <paramref name="localNetId"/> is <see cref="NoLocalPlayer"/>.</param>
    /// <param name="others">Every other actor and its committed tile, in any order.</param>
    /// <param name="winners">Filled with the one net id drawn on each occupied tile.</param>
    /// <param name="drawn">Filled with those net ids, so a local player mid-step is ONE entry here against two
    /// in <paramref name="winners"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="winners"/> or <paramref name="drawn"/> is
    /// null.</exception>
    public static void Select(long localNetId, TileCoord localTile, TileCoord? localLeaving,
        ReadOnlySpan<(long NetId, TileCoord Tile)> others, Dictionary<TileCoord, long> winners,
        HashSet<long> drawn)
    {
        ArgumentNullException.ThrowIfNull(winners);
        ArgumentNullException.ThrowIfNull(drawn);
        Begin(winners, drawn);
        for (int i = 0; i < others.Length; i++) Offer(localNetId, others[i].NetId, others[i].Tile, winners);
        Settle(localNetId, localTile, localLeaving, winners, drawn);
    }

    static void Begin(Dictionary<TileCoord, long> winners, HashSet<long> drawn)
    {
        winners.Clear();
        drawn.Clear();
    }

    // One actor's claim on one tile, which is the whole of the per-entry rule and lives here once so the two
    // roster shapes cannot drift apart on it.
    static void Offer(long localNetId, long netId, TileCoord tile, Dictionary<TileCoord, long> winners)
    {
        // The local player is settled below, whatever a caller's roster says about them, so an entry for them
        // here would only give them a second chance at somebody else's tile.
        if (netId == localNetId) return;
        if (winners.TryGetValue(tile, out long best))
        {
            if (netId > best) winners[tile] = netId;
        }
        else winners[tile] = netId;
    }

    // LAST, and that ordering IS the local player's win: whoever took their tiles in the loop is overwritten
    // here, rather than the loop carrying a per-entry test for a case that is true at most twice. The leaving
    // tile is the same write, and when it equals the tile being entered (a caller passing it rather than null)
    // the second write lands on the entry the first one just made.
    static void Settle(long localNetId, TileCoord localTile, TileCoord? localLeaving,
        Dictionary<TileCoord, long> winners, HashSet<long> drawn)
    {
        if (localNetId != NoLocalPlayer)
        {
            winners[localTile] = localNetId;
            if (localLeaving is TileCoord leaving) winners[leaving] = localNetId;
        }

        foreach (long netId in winners.Values) drawn.Add(netId);
    }

    // The weights, over the winners the rule just chose. Every listed actor is moved toward 1 if it won a tile
    // and toward 0 if it did not, the local player is pinned at 1, anything no longer listed is swept, and every
    // body left with a weight above zero joins the drawn set so a head walking that set draws the faders too.
    void Advance(long localNetId, ReadOnlySpan<(long NetId, TileCoord Tile)> others, float dt, bool snap)
    {
        stamp++;
        int touched = 0;
        for (int i = 0; i < others.Length; i++)
            touched += Touch(localNetId, others[i].NetId, progress: 1f, dt, snap);
        Finish(localNetId, dt, touched);
    }

    void Advance(long localNetId, ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress)> others, float dt,
        bool snap)
    {
        stamp++;
        int touched = 0;
        for (int i = 0; i < others.Length; i++)
            touched += Touch(localNetId, others[i].NetId, others[i].StepProgress, dt, snap);
        Finish(localNetId, dt, touched);
    }

    void Finish(long localNetId, float dt, int touched)
    {
        // The local player is pinned rather than advanced: they are drawn unconditionally, so there is no state
        // for them to cross and a fade on their own body would be a fade on the one body that must never have
        // one. Snapped for that reason whatever the caller asked for.
        if (localNetId != NoLocalPlayer) touched += Touch(NoLocalPlayer, localNetId, 1f, dt, snap: true);

        // Every live body was just stamped, so equal counts means nothing went stale and the sweep is skipped,
        // which is the ordinary frame. Removal cannot happen inside the walk, so the departed are collected
        // first.
        if (fades.Count != touched)
        {
            gone.Clear();
            foreach (KeyValuePair<long, Fade> pair in fades)
                if (pair.Value.Stamp != stamp) gone.Add(pair.Key);
            for (int i = 0; i < gone.Count; i++) fades.Remove(gone[i]);
        }

        foreach (KeyValuePair<long, Fade> pair in fades)
            if (pair.Value.Weight > 0f) drawn.Add(pair.Key);
    }

    // One body, one frame. Returns 1 when this is the first time this rebuild has seen the id, so the caller's
    // count of live bodies is a count of DISTINCT ones and a roster listing an actor on two tiles cannot inflate
    // it past the map's own count and skip the sweep.
    int Touch(long localNetId, long netId, float progress, float dt, bool snap)
    {
        if (netId == localNetId) return 0;
        float target = drawn.Contains(netId) ? 1f : 0f;
        float p = float.IsFinite(progress) ? Math.Clamp(progress, 0f, 1f) : 1f;

        ref Fade fade = ref CollectionsMarshal.GetValueRefOrAddDefault(fades, netId, out bool existed);
        int counted = !existed || fade.Stamp != stamp ? 1 : 0;
        if (!existed || snap)
        {
            // A body seen for the first time starts ON its answer, so a spawn is not a fade in and an actor
            // spawning into a crowd is hidden from its first frame.
            fade.Weight = target;
        }
        else if (p < fade.LastProgress)
        {
            // A NEW STEP, which is the only way progress can fall: the previous one landed and this one has just
            // committed. The fade is re-paced against the whole of the new step from here, which is what makes a
            // body that lost its tile while standing still, then walked out of it, rise across the walk out.
            fade.Weight = Cross(fade.Weight, target, p, remaining: 1f);
        }
        else
        {
            float remaining = 1f - fade.LastProgress;
            fade.Weight = remaining > StepEpsilon
                ? Cross(fade.Weight, target, p - fade.LastProgress, remaining)
                // NO STEP LEFT TO SPEND IT ON, so the fixed window takes over: the body is standing still, and
                // the winner is the one that moved. A dt that is not a finite positive number advances nothing,
                // and a zero window cuts.
                : Window(fade.Weight, target, dt);
        }

        fade.Weight = Math.Clamp(fade.Weight, 0f, 1f);
        fade.LastProgress = p;
        fade.Stamp = stamp;
        return counted;
    }

    // A slice of the crossing, paced by the STEP: covering the whole of what is left of the step covers the
    // whole of what is left of the crossing, so the weight arrives at its target exactly as the body comes to
    // rest, and every frame in between moves monotonically toward it.
    static float Cross(float weight, float target, float covered, float remaining)
        => weight + (target - weight) * Math.Clamp(covered / remaining, 0f, 1f);

    float Window(float weight, float target, float dt)
    {
        if (fadeSeconds <= 0f) return target;
        float step = (float.IsFinite(dt) && dt > 0f ? dt : 0f) / fadeSeconds;
        return target > weight ? Math.Min(target, weight + step) : Math.Max(target, weight - step);
    }

    // One body's crossing. LastProgress is how far into its step the last rebuild saw it, which is what turns a
    // per-frame dt into a per-step pace without the fade having to know how many seconds a step takes: the step
    // itself is the clock. Stamp is the rebuild that last listed the body, which is the sweep's whole mechanism.
    struct Fade
    {
        public float Weight;
        public float LastProgress;
        public int Stamp;
    }
}
