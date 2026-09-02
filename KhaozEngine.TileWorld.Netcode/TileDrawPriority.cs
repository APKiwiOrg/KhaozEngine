using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// ONE BODY PER TILE. Which actor standing on each tile is the one to draw, and which are hidden behind it,
/// rebuilt from scratch every frame. The local player wins their own tile outright, and both tiles of a step
/// while one is in flight. Every other tile goes to the HIGHEST net id on it.
/// <para>WHY, and it is a presentation rule rather than a rules one. A tile game draws every body on the tile
/// centre, so a stack of them is a smear of overlapping meshes that reads as one wrong-looking creature, and the
/// body a player can least afford to lose in that smear is their own: standing under a bank crowd, an avatar the
/// player cannot see is an avatar they cannot aim from. So the stack collapses to one. OSRS answers the same
/// question with PID, a per-tick priority every player carries, and this is the same shape with a stable key
/// instead of a rotating one.</para>
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
/// to a whole step.</para>
/// <para>THE LOCAL PLAYER CLAIMS BOTH TILES OF A STEP IN FLIGHT, and that is what makes the own-body guarantee
/// hold while walking. <see cref="TileMoveState"/> commits the destination on the tick the step STARTS and the
/// body glides in over the rest of it, so through the whole step the drawn local body is somewhere between
/// <see cref="TileMoveState.StepFrom"/> and <see cref="TileMoveState.Tile"/>. Claiming the destination alone
/// leaves the tile being vacated to the highest net id standing on it, and on the tick the step commits that
/// body draws at the same world position as the local one, which is the exact failure this class exists to
/// remove. So while <see cref="TileMoveState.IsStepping"/> the local player wins the leaving tile as well as the
/// entering one. The drawn body is inside that pair for the whole step, so nothing is ever drawn over it. Both
/// claims name the SAME net id, so the pair is one entry in <see cref="Drawn"/> and two in the tile map.</para>
/// <para>A REMOTE KEEPS ONE TILE, deliberately, so the lead the local claim removes is still there for everybody
/// else: a remote mid-step is judged on the tile it is walking into, and a body standing on the tile it is
/// leaving wins that tile and draws over the remote's own body. It lasts exactly one step, it ends when the step
/// lands, and it is the same lead every other tile read in this package carries. A remote is not the body a
/// player aims from, and paying two tiles per remote to shave it would hide a second body in every crowd for the
/// length of every step somebody takes.</para>
/// <para>Per frame at 60 Hz and allocation free after the first rebuild: the buffers here are reused, the rule is
/// one pass over the actors plus one over the winners, and there is no LINQ and no sort. Hold ONE of these on the
/// head and call <see cref="Rebuild(TileWorldClient)"/> once a frame, after
/// <see cref="TileWorldClient.AdvancePresentation"/> and before drawing. The draw loop walks a collected list
/// rather than <see cref="TileWorldClient.RemoteNetIds"/>, which is interface-typed and boxes an enumerator every
/// frame, the cost <see cref="TileWorldClient.CollectRemoteTiles"/> exists to avoid.</para>
/// <code>
/// priority.Rebuild(client);
/// TilePose me = client.LocalPose;                 // the local player is always drawn
/// Draw(playerMesh, me.Position, me.Yaw);
/// client.CollectRemoteTiles(remotes);             // a List&lt;(long NetId, TileCoord Tile)&gt; the head keeps
/// foreach ((long netId, TileCoord _) in remotes)
///     if (priority.IsDrawn(netId) &amp;&amp; client.TryGetRemotePose(netId, out TilePose pose))
///         Draw(remoteMesh, pose.Position, pose.Yaw);
/// </code>
/// <para>Nothing here is a rules input. Hiding a body changes what is DRAWN and nothing else: the hidden actor is
/// still on its tile, still replicated, still a legal click target and still swinging. A head that hides
/// nameplates or click targets along with the body is making a second, separate decision and should make it
/// deliberately.</para>
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

    // The client's remotes, collected once per rebuild so the rule can read them as a span. Grows to the biggest
    // crowd this client has seen and stops, which is the whole of the per-frame allocation story.
    readonly List<(long NetId, TileCoord Tile)> actors = new();
    readonly Dictionary<TileCoord, long> winners = new();
    readonly HashSet<long> drawn = new();

    /// <summary>The net ids this rebuild chose to draw. The LOCAL player's id is among them whenever the client
    /// has one, because the local player is always drawn, so a head walking this collection to draw bodies has to
    /// draw that one through <see cref="TileWorldClient.LocalPose"/> rather than
    /// <see cref="TileWorldClient.TryGetRemotePose"/>. Walking a list filled by
    /// <see cref="TileWorldClient.CollectRemoteTiles"/> and asking <see cref="IsDrawn"/> avoids the question
    /// entirely, and is also the allocation-free shape: this collection is interface-typed, so enumerating it
    /// boxes the set's enumerator once a frame.
    /// <para>THE LIVE SET, not a snapshot. A rebuild CLEARS and refills this same instance, so a reference held
    /// across frames changes under its holder, and enumerating it while <see cref="Rebuild(TileWorldClient)"/>
    /// runs on it throws <see cref="InvalidOperationException"/>. Copy it if you need last frame's
    /// answer.</para></summary>
    public IReadOnlyCollection<long> Drawn => drawn;

    /// <summary>How many bodies this rebuild chose. NOT the number of occupied tiles: one body can hold two of
    /// them. The local player holds both tiles of a step in flight, and a caller's roster may list one net id on
    /// two tiles. <see cref="Drawn"/> is a set of net ids, so either case counts once here and twice in
    /// <see cref="TryGetDrawn"/>.</summary>
    public int Count => drawn.Count;

    /// <summary>True when <paramref name="netId"/> is the one body drawn on its tile. False when it is standing
    /// behind somebody, AND false for an id this priority has never seen: an actor with no tile is an actor with
    /// no pose to draw, so the two cases want the same answer at a draw call.</summary>
    /// <param name="netId">The actor's net id.</param>
    /// <returns>True when the actor should be drawn.</returns>
    public bool IsDrawn(long netId) => drawn.Contains(netId);

    /// <summary>The one actor drawn on a tile, for a head that asks by place rather than by actor (a click
    /// resolving to the body a player can actually see, a tooltip over a tile).</summary>
    /// <param name="tile">The tile to ask about, plane included.</param>
    /// <param name="netId">The net id drawn there, zero when false. Zero is never a live net id, so it is the
    /// same "nobody" this package's combat target uses.</param>
    /// <returns>True when any actor stands on <paramref name="tile"/>.</returns>
    public bool TryGetDrawn(TileCoord tile, out long netId) => winners.TryGetValue(tile, out netId);

    /// <summary>
    /// Rebuilds from a live client: every remote it is drawing on its committed tile, plus the local player on
    /// their predicted one AND, while a step is in flight, on the tile that step is leaving. Call it once a
    /// frame, after <see cref="TileWorldClient.AdvancePresentation"/> so the remote set is the one this frame
    /// will draw.
    /// <para>Until the first snapshot names the local actor, <see cref="TileWorldClient.LocalNetId"/> is
    /// <see cref="NoLocalPlayer"/>, so nothing claims a local tile and the remotes (of which there are none yet)
    /// settle every tile between themselves.</para>
    /// </summary>
    /// <param name="client">The client to read. Not retained.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public void Rebuild(TileWorldClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.CollectRemoteTiles(actors);
        // The predicted state is read ONCE, so the tile claimed and the tile being left cannot come from two
        // different frames of prediction.
        TileMoveState local = client.Prediction.PredictedState;
        Select(client.LocalNetId, local.Tile, local.IsStepping ? local.StepFrom : null,
            CollectionsMarshal.AsSpan(actors), winners, drawn);
    }

    /// <summary>
    /// Rebuilds from a caller's own roster, for a head that draws bodies this client does not own (a replay, a
    /// server-side view, a game whose actors live outside the replication view). The rule is the same one
    /// <see cref="Rebuild(TileWorldClient)"/> applies, over the actors you hand it.
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
        => Select(localNetId, localTile, localLeaving, others, winners, drawn);

    /// <summary>
    /// THE RULE ITSELF, pure and allocation free: no state of its own, both outputs owned by the caller, and the
    /// same inputs always give the same answer. The instance methods above are this with the buffers held for
    /// you, and this is here for a head that already owns its own.
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
        winners.Clear();
        drawn.Clear();

        for (int i = 0; i < others.Length; i++)
        {
            (long netId, TileCoord tile) = others[i];
            // The local player is settled below, whatever a caller's roster says about them, so an entry for them
            // here would only give them a second chance at somebody else's tile.
            if (netId == localNetId) continue;
            if (winners.TryGetValue(tile, out long best))
            {
                if (netId > best) winners[tile] = netId;
            }
            else winners[tile] = netId;
        }

        // LAST, and that ordering IS the local player's win: whoever took their tiles above is overwritten here,
        // rather than the loop carrying a per-entry test for a case that is true at most twice. The leaving tile
        // is the same write, and when it equals the tile being entered (a caller passing it rather than null) the
        // second write lands on the entry the first one just made.
        if (localNetId != NoLocalPlayer)
        {
            winners[localTile] = localNetId;
            if (localLeaving is TileCoord leaving) winners[leaving] = localNetId;
        }

        foreach (long netId in winners.Values) drawn.Add(netId);
    }
}
