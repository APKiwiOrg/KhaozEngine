using System;
using System.Numerics;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Where to draw something, and which way it faces.</summary>
/// <param name="Position">World position in metres, on the footprint CENTRE, which is the tile centre for a one-tile
/// body. See <see cref="TilePresenter"/> for the convention and why the half tile is the presenter's to add.</param>
/// <param name="Yaw">Facing as a rotation about +Y in radians, ready for a <c>Matrix4x4.CreateRotationY(yaw)</c>
/// model transform on a +z-forward mesh: tile SOUTH is 0, east +pi/2, north pi and west -pi/2. That is the engine's
/// one model-yaw convention, the same <c>CharacterFacing.YawOf</c> produces and the same hand
/// <c>TileObjectProps.YawRadians</c> places tile objects with, so an avatar and the object it stands next to face
/// the same way. See <see cref="TilePresenter.Yaw(TileDirection)"/>.</param>
public readonly record struct TilePose(Vector3 Position, float Yaw);

/// <summary>
/// The pure bridge from a tile state to a view, and THE ONLY PLACE in this package that consults
/// <see cref="TileWorldSpace"/>. Everything else here, the server especially, runs entirely in tile coordinates:
/// tile z counts NORTH while render z counts south, so a render-space sign that leaked into a shard boundary, a
/// reach test or a route would be an off-by-one nobody could see until a player walked into it. Keeping the
/// negation in one file is what makes that impossible rather than merely unlikely.
/// <para>A POSE NAMES THE FOOTPRINT CENTRE, which is the tile centre for a one-tile body. Tile (x, z) spans x..x+1
/// and z..z+1, which is the span its ground quad covers and the span <c>TileObjectProps.AnchorPosition</c> centres a
/// 1x1 prop in, so half a tile is added on each axis before the world conversion. An NxN body is anchored on its
/// south-west tile and covers x..x+N and z..z+N, so its centre is N halves in from the anchor corner rather than one,
/// the same centre an NxN prop is anchored at. Added in TILE units, ahead of <see cref="TileWorldSpace"/>, so the half
/// tile goes through the same z negation the tile coordinate does and lands on the same side of the tile the
/// ground quad and the props do. Drawn on the CORNER instead, an avatar stands half a tile diagonally off every
/// prop it walks up to and off the middle of the ground it occupies, which is what a consumer then re-centres in
/// a shim of its own.</para>
/// <para>A POSE STANDS ON THE TERRAIN, not on the plane floor. Once the planar centre is known, the height comes
/// from <see cref="Ground"/>, sampled at that same centred point, so a body on an authored slope has its feet on
/// the ground quad it is standing on and so does a marker or a dropped item laid down through
/// <see cref="PoseAt(TileCoord, TileDirection)"/>. A gliding body resamples every frame at its interpolated planar
/// position, which is what makes it FOLLOW a slope between two tile centres instead of stepping at the tile edge.
/// <see cref="TilePresenter(TileWorldDocument)"/> wires the document's own bilinear lattice, the same one the
/// terrain mesh and the props are built from, and <see cref="TilePresenter(TileWorldDocument, TileWorldCatalogs)"/>
/// raises that onto a walkable object top such as a bridge deck. A presenter with no ground source draws at the plane index times
/// <see cref="PlaneHeight"/>, which is the flat placeholder and the only honest answer before a document is
/// loaded.</para>
/// <para>THE BODY GLIDES THE WHOLE STEP, LINEARLY. <see cref="Pose(in TileMoveState, float)"/> runs from <see cref="TileMoveState.StepFrom"/>
/// into <see cref="TileMoveState.Tile"/> by <see cref="TileMoveState.StepTicks"/> over
/// <see cref="TileMoveState.StepTotal"/>, at a constant speed, arriving exactly as the next step commits. That is
/// the OSRS model, and it is the ruled answer rather than a first draft of one: see
/// <c>docs/design/TILE-WORLD-NETCODE-DESIGN-2026-08-22.md</c> section 5.2 for the two shapes that were tried
/// against it and rejected. Its known cost is that the drawn body lags the tile the rules have committed it to,
/// by half a tile on average, and the answer to that is VISIBILITY rather than tightness: a head draws a
/// true-tile marker and a route highlight off <see cref="PoseAt(TileCoord, TileDirection)"/>, so the lead is
/// legible instead of being something a player has to learn.</para>
/// <para>Nothing here holds state or touches a GPU. It is a function of a <see cref="TileMoveState"/> plus a
/// fraction of a tick, so a head can call it from a render thread, a test can call it with no device, and two
/// callers asking about the same state get the same answer.</para>
/// </summary>
public sealed class TilePresenter
{
    /// <summary>Builds a FLAT presenter for a world's tile size and plane height, with no terrain under it. The
    /// placeholder shape: every pose draws at its plane index times <paramref name="planeHeight"/>, which is the
    /// only honest answer before a document is loaded. Use <see cref="TilePresenter(TileWorldDocument)"/> the
    /// moment there is a world file.</summary>
    /// <param name="tileSize">Metres per tile. Must be positive.</param>
    /// <param name="planeHeight">Metres between two planes. Zero is legal, and draws every plane flat.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileSize"/> is zero or negative, which would
    /// collapse the whole world onto the origin.</exception>
    public TilePresenter(float tileSize, float planeHeight)
        : this(tileSize, planeHeight, null) { }

    /// <summary>Builds a presenter over an explicit ground-height source, for a test with a synthetic slope and for
    /// a head whose terrain is not a <see cref="TileWorldDocument"/> (a streamed source, a generated one).</summary>
    /// <param name="tileSize">Metres per tile. Must be positive.</param>
    /// <param name="planeHeight">Metres between two planes, which is still what a pose falls back to when
    /// <paramref name="ground"/> is null and what <see cref="TileMoveState.Vertical"/> is read against.</param>
    /// <param name="ground">The terrain under a pose, or null to draw every plane flat.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileSize"/> is zero or negative, which would
    /// collapse the whole world onto the origin.</exception>
    public TilePresenter(float tileSize, float planeHeight, ITileGroundHeight? ground)
    {
        if (tileSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "A tile is at least some metres wide.");
        TileSize = tileSize;
        PlaneHeight = planeHeight;
        Ground = ground;
    }

    /// <summary>Builds a presenter from a loaded document, which is where the real numbers live. A head builds one
    /// of these the moment it has the world file, and replaces the placeholder the client started with. It wires
    /// the terrain-only <see cref="TileDocumentGroundHeight(TileWorldDocument)"/>, so the bodies this draws stand on
    /// the SAME lattice the terrain mesh and the props are built from, with nothing for the head to call. No object
    /// is consulted, so a body on a bridge deck stands on the ground under it: a world whose archetypes carry walk
    /// surfaces wants <see cref="TilePresenter(TileWorldDocument, TileWorldCatalogs)"/>.</summary>
    /// <param name="document">The loaded world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public TilePresenter(TileWorldDocument document)
        : this((document ?? throw new ArgumentNullException(nameof(document))).TileSize, document.PlaneHeight,
            new TileDocumentGroundHeight(document)) { }

    /// <summary>Builds a presenter from a loaded document AND the catalogs its objects reference, so a pose stands
    /// on the higher of the terrain and any walkable object top covering it (a bridge deck, a dock, a pier). It
    /// wires <see cref="TileDocumentGroundHeight(TileWorldDocument, TileWorldCatalogs)"/>, which reads the
    /// <see cref="TileObjectArchetype.WalkSurfaces"/> through the same placement the props are drawn with.</summary>
    /// <param name="document">The loaded world.</param>
    /// <param name="catalogs">The archetypes the world's objects reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="catalogs"/> is
    /// null.</exception>
    public TilePresenter(TileWorldDocument document, TileWorldCatalogs catalogs)
        : this((document ?? throw new ArgumentNullException(nameof(document))).TileSize, document.PlaneHeight,
            new TileDocumentGroundHeight(document, catalogs)) { }

    /// <summary>Metres per tile.</summary>
    public float TileSize { get; }

    /// <summary>Metres between two planes. What a pose falls back to when there is no <see cref="Ground"/>, and the
    /// lift a plane with no authored heights carries over the one below it, which is why
    /// <see cref="TileMoveState.Vertical"/> can stay document-free.</summary>
    public float PlaneHeight { get; }

    /// <summary>The terrain under a pose, or null on a FLAT presenter, which is what the
    /// <see cref="TilePresenter(float, float)"/> placeholder builds and what a head holds until it swaps in one
    /// built from the document. Read it to tell the two apart.</summary>
    public ITileGroundHeight? Ground { get; }

    /// <summary>
    /// Where a state's BODY draws: the linear glide from <see cref="TileMoveState.StepFrom"/> INTO
    /// <see cref="TileMoveState.Tile"/>, which the simulation already owns. <paramref name="extraTicks"/> is the
    /// fraction of a tick elapsed since the state was SAMPLED, which is what glides a remote between snapshots: the
    /// sample carries the step progress at its own instant, and the presenter carries it forward from there.
    /// Clamped at the end of the step, so a sample that went overdue (a lost snapshot, a stalled server) parks on
    /// the tile instead of walking past it.
    /// <para>The ROUTE is not consulted, and that is what makes an observer's pose honest. A remote's route is
    /// owner-only, but the pair of tiles this glides between rides the everyone channel, so a raw replicated state
    /// draws exactly where its owner draws it with nothing guessed and nothing reconstructed from the tile it was
    /// last seen on. A state with no step in flight draws on its footprint centre, whatever
    /// <paramref name="extraTicks"/> says.</para>
    /// <para>This is the BODY's answer, not the RULES'. A step commits its tile when it STARTS, so the tile the
    /// simulation has committed this player to is <see cref="TileMoveState.Tile"/> and the body drawn here is up to
    /// one Chebyshev grid step behind it. A diagonal grid step is <c>sqrt(2) * TileSize</c> in Euclidean world
    /// distance. An overlay that has to show where the player IS (a true-tile marker, a minimap dot, a server-side
    /// tool) reads <c>state.Tile</c> and maps it with
    /// <see cref="PoseAt(TileCoord, TileDirection)"/>.</para>
    /// </summary>
    /// <param name="state">The state to draw.</param>
    /// <param name="extraTicks">Ticks elapsed since the state was sampled. Negative is treated as zero.</param>
    /// <returns>The world position and yaw to draw at.</returns>
    public TilePose Pose(in TileMoveState state, float extraTicks = 0f) =>
        PoseAt(BodyCentre(state, extraTicks), state.Tile.Plane, state.Facing);

    /// <summary>
    /// The same body, drawn in the same place, LOOKING at <paramref name="aimTilePlanar"/> instead of along
    /// <see cref="TileMoveState.Facing"/>. The continuous aim in its hand-placed form, for a game drawing a body of
    /// its own rather than one of the client's: <see cref="TileWorldClient.TryGetRemotePose"/> and
    /// <see cref="TileWorldClient.LocalPose"/> already apply it to the bodies they draw.
    /// <para>The yaw is taken from where the body IS DRAWN, so an aimed body turns with its own glide, and for a
    /// body at rest that point is its footprint centre. An aim point ON the body's own centre has no direction to
    /// report, so the tile facing is kept rather than snapping the body south.</para>
    /// <para><see cref="TileMoveState.Facing"/> is untouched. It is what the reach rules, the follow and the wire
    /// all read, and this is a drawn yaw over it.</para>
    /// </summary>
    /// <param name="state">The state to draw.</param>
    /// <param name="aimTilePlanar">Where to look, in tile units on the lattice (x, z), which is what
    /// <see cref="ITileTargets.TryGetAimPoint"/> answers.</param>
    /// <param name="extraTicks">Ticks elapsed since the state was sampled. Negative is treated as zero.</param>
    /// <returns>The world position and the aimed yaw to draw at.</returns>
    public TilePose Pose(in TileMoveState state, Vector2 aimTilePlanar, float extraTicks = 0f)
    {
        Vector2 centre = BodyCentre(state, extraTicks);
        return new TilePose(Centre(centre.X, state.Tile.Plane, centre.Y),
            centre == aimTilePlanar ? Yaw(state.Facing) : Yaw(centre, aimTilePlanar));
    }

    // Where the body's CENTRE is, in tile units: the glide from StepFrom into Tile, plus the offset that centres a
    // large body on its footprint. Shared by both Pose overloads so an aimed body and a facing one are drawn at the
    // same point by construction rather than by two copies of the same arithmetic.
    Vector2 BodyCentre(in TileMoveState state, float extraTicks)
    {
        float tileX = state.Tile.X, tileZ = state.Tile.Z;
        if (state.IsStepping && state.StepTotal > 0)
        {
            float f = StepFraction(state, extraTicks);
            // In FLOAT, for the reason TileMoveState.Position differences in float: the fields are public, and two
            // hand-written coordinates a world apart would overflow an int subtraction.
            tileX = state.StepFrom.X + ((float)state.Tile.X - state.StepFrom.X) * f;
            tileZ = state.StepFrom.Z + ((float)state.Tile.Z - state.StepFrom.Z) * f;
        }
        // A footprint's centre is half its edge in from the anchor corner. PoseAt already adds the half tile a one-tile
        // body wants, so a large body adds the rest, and the offset is constant through a glide because the size is.
        float extra = (state.FootprintSize - 1) * 0.5f;
        return new Vector2(tileX + extra, tileZ + extra);
    }

    /// <summary>
    /// How far through its current step a state is, 0 at the moment the step commits and 1 as the body lands,
    /// carried forward by <paramref name="extraTicks"/> exactly as <see cref="Pose(in TileMoveState, float)"/> carries the glide. This IS
    /// the fraction <see cref="Pose(in TileMoveState, float)"/> interpolates on, exposed so a presentation rule that has to run in lockstep
    /// with the body (a fade, a squash, a footfall) measures the same number the body is drawn at rather than a
    /// second estimate of it.
    /// <para>ONE when there is no step in flight, because a body at rest is all the way into the tile it is
    /// committed to. That is the same answer a body that has just landed gives, so a reader cannot see a
    /// discontinuity at the landing, and it is why the value is a fraction of the step INTO
    /// <see cref="TileMoveState.Tile"/> rather than a distance from anywhere.</para>
    /// </summary>
    /// <param name="state">The state to measure.</param>
    /// <param name="extraTicks">Ticks elapsed since the state was sampled. Negative is treated as zero.</param>
    /// <returns>The fraction of the step already spent, clamped to 0 through 1.</returns>
    public static float StepFraction(in TileMoveState state, float extraTicks = 0f)
        => state.IsStepping && state.StepTotal > 0
            ? Math.Clamp((state.StepTicks + Math.Max(0f, extraTicks)) / state.StepTotal, 0f, 1f)
            : 1f;

    /// <summary>
    /// Where the LOCAL player's BODY draws: <see cref="ClientPrediction{TState,TCommand}.RenderedState"/>, which
    /// already carries the inter-tick easing of the same <see cref="TileMoveState.Position"/> glide plus whatever
    /// is left of a decaying correction offset. Read from the render override rather than from the tile, because
    /// that override is the whole point of the prediction layer: it is a continuous position over a discrete
    /// lattice, and rounding it back to a tile here would throw away every frame of smoothing the layer just
    /// computed.
    /// <para>The zero-correction local motion bound has one term beyond <see cref="Pose(in TileMoveState, float)"/>. At the instant a new step commits,
    /// <c>RenderedState</c> still starts from the previous predicted position, so the body may trail
    /// <c>PredictedState.Tile</c> by one grid step plus one local command tick of travel. With a step cadence of N
    /// ticks the bound is <c>1 + 1/N</c> grid steps. The default walk and run cadences therefore bound at 1.25 and
    /// 1.5. Multiply by <c>sqrt(2) * TileSize</c> for the Euclidean world-space bound of diagonal travel. An active
    /// reconciliation offset is an additional presentation term. The conservative instantaneous bound adds its
    /// magnitude to this base motion bound. Ordinary corrections can re-anchor it, while a hard snap or teleport
    /// clears it.</para>
    /// <para><see cref="TileWorldClient.LocalPose"/> is this call with the client's own prediction and presenter
    /// already in hand, and is what a head normally uses. This overload is for a head holding a
    /// <see cref="ClientPrediction{TState,TCommand}"/> of its own.</para>
    /// <para>No footprint offset, unlike <see cref="Pose(in TileMoveState, float)"/>: this draws the LOCAL PLAYER, and a player is always one
    /// tile (<see cref="TileWorldServer.SetPlayerState"/> refuses a larger footprint), so the tile centre is the
    /// footprint centre.</para>
    /// </summary>
    /// <param name="prediction">The client's prediction for the local player.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prediction"/> is null.</exception>
    public TilePose LocalPose(ClientPrediction<TileMoveState, TileCommand> prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        TileMoveState r = prediction.RenderedState;
        return PoseAt(r.HasRenderOverride ? r.RenderPosition : r.Position,
            r.HasRenderOverride ? r.RenderVertical : r.Vertical, r.Facing);
    }

    /// <summary>
    /// THE mapping, and the one every other entry point here goes through: a point in TILE units plus a plane index
    /// becomes a world position on the tile centre, and a facing becomes a model yaw.
    /// <para>Public because it is what an OVERLAY needs. A true-tile marker maps
    /// <c>client.Prediction.PredictedState.Tile</c>, a route highlight maps each remaining
    /// <see cref="TileRoute.Tiles"/> entry from <see cref="TileRoute.Index"/> on, and both want a whole
    /// <see cref="TileCoord"/> rather than a state: use the <see cref="PoseAt(TileCoord, TileDirection)"/>
    /// overload for those. This one takes a CONTINUOUS point, because a body between two tiles is not on the
    /// lattice.</para>
    /// </summary>
    /// <param name="tilePlanar">Where the point is, in tile units on the lattice (x, z). The half tile onto the
    /// tile centre is added here, and the ground is sampled at that centred point.</param>
    /// <param name="planeIndex">Which plane, as an INDEX rather than a height. Fractional is legal and is what a
    /// prediction layer's eased vertical hands in, and it reads between the two planes' own ground samples.</param>
    /// <param name="facing">The direction to face.</param>
    /// <returns>The world position and yaw to draw at.</returns>
    public TilePose PoseAt(Vector2 tilePlanar, float planeIndex, TileDirection facing) =>
        new(Centre(tilePlanar.X, planeIndex, tilePlanar.Y), Yaw(facing));

    /// <summary>
    /// A whole TILE's centre, which is the RULES' answer about where a player is and the one an overlay draws on.
    /// The tile carries its own plane, so this is the call a true-tile marker and a route highlight make, once per
    /// tile, with no state and no glide involved.
    /// <para>Never draw a BODY through this: a step commits its tile when it STARTS, so a body drawn straight on
    /// its committed tile cuts a whole tile at every commit. Bodies go through
    /// <see cref="TileWorldClient.LocalPose"/> and <see cref="TileWorldClient.TryGetRemotePose"/>.</para>
    /// </summary>
    /// <param name="tile">The tile to place.</param>
    /// <param name="facing">The direction to face, <see cref="TileDirection.S"/> for a marker that has no facing
    /// of its own (yaw 0, so a head that ignores the yaw pays nothing for it).</param>
    /// <returns>The tile centre's world position, and the yaw of <paramref name="facing"/>.</returns>
    public TilePose PoseAt(TileCoord tile, TileDirection facing = TileDirection.S) =>
        PoseAt(new Vector2(tile.X, tile.Z), tile.Plane, facing);

    /// <summary>A footprint's CENTRE, which is where an overlay that belongs to a large body (a footprint marker, a
    /// nameplate anchor, a hitsplat) draws. The rules' answer, like <see cref="PoseAt(TileCoord, TileDirection)"/>, so
    /// never a body: a body glides and goes through <see cref="Pose(in TileMoveState, float)"/>.</summary>
    /// <param name="footprint">The tiles covered, anchored on the south-west tile. A one-tile rect draws exactly
    /// where <see cref="PoseAt(TileCoord, TileDirection)"/> draws its tile.</param>
    /// <param name="plane">The plane index the footprint stands on.</param>
    /// <param name="facing">The direction to face, <see cref="TileDirection.S"/> for a marker with no facing.</param>
    /// <returns>The footprint centre's world position, and the yaw of <paramref name="facing"/>.</returns>
    public TilePose PoseAt(TileRect footprint, int plane, TileDirection facing = TileDirection.S) =>
        PoseAt(new Vector2(footprint.X + (footprint.Width - 1) * 0.5f, footprint.Z + (footprint.Height - 1) * 0.5f),
            plane, facing);

    // A tile point as a world position on the tile CENTRE, which is the one place the half tile is added AND the
    // one place the ground is sampled. In TILE units, before TileWorldSpace, so the z half tile is negated with the
    // coordinate it belongs to rather than being added to a world metre and landing on the wrong side of the tile.
    // A glided position goes through the same offset as a lattice one, so a body converges onto the centre it is
    // drawn toward.
    //
    // The height is read at the CENTRED point, in one expression with the position built from it, so a pose samples
    // the ground under exactly where it draws by construction rather than by two call sites agreeing. Sampling the
    // anchor corner instead would put a body on a slope half a tile diagonally off its own feet.
    Vector3 Centre(float tileX, float planeIndex, float tileZ)
    {
        float centreX = tileX + 0.5f, centreZ = tileZ + 0.5f;
        return TileWorldSpace.ToWorld(centreX, Height(centreX, centreZ, planeIndex), centreZ, TileSize);
    }

    // The ground at an already-centred planar point, in metres. Flat when there is no source, which is the
    // placeholder presenter and the only honest answer before a document is loaded.
    //
    // A FRACTIONAL plane index is a body easing between two planes (LocalPose reads TileMoveState.RenderVertical,
    // which the prediction layer eases), and it reads BETWEEN the two planes' own samples. The flat answer is
    // linear in the plane index, so the terrain answer is too, and a body climbing a stair draws continuously
    // instead of popping at the plane boundary. A whole plane index costs one sample, which is every pose an
    // overlay and a remote body ever ask for.
    float Height(float centreX, float centreZ, float planeIndex)
    {
        if (Ground is null) return planeIndex * PlaneHeight;
        int below = (int)MathF.Floor(planeIndex);
        float fraction = planeIndex - below;
        float lower = Ground.HeightAt(centreX, centreZ, below);
        return fraction <= 0f ? lower : lower + (Ground.HeightAt(centreX, centreZ, below + 1) - lower) * fraction;
    }

    /// <summary>
    /// The yaw a facing draws at, in the ENGINE's model-yaw convention: the value a head hands straight to
    /// <c>Matrix4x4.CreateRotationY</c> to point a +z-forward mesh along <paramref name="facing"/>. Tile south is
    /// 0, east +pi/2, north pi, west -pi/2.
    /// <para>The delta is taken in WORLD space, so the yaw goes through the same z negation the position does (tile
    /// north is world -z, see <see cref="TileWorldSpace"/>) and a facing and a position can never disagree about
    /// which way north is. It is the formula <c>CharacterFacing.YawOf</c> applies to a world direction, in the hand
    /// <c>TileObjectProps.YawRadians</c> rotates tile objects by: a clockwise quarter turn seen from above is a
    /// NEGATIVE yaw, because a row-vector <c>CreateRotationY(t)</c> carries the west point of a tile onto its north
    /// point only at t of -90 degrees. A compass bearing here instead (north 0, increasing clockwise) is the same
    /// numbers reflected, which agrees on east and west and draws every avatar facing south while it walks
    /// north.</para>
    /// </summary>
    /// <param name="facing">The direction the state faces.</param>
    /// <returns>Rotation about +Y in radians, in the range (-pi, pi].</returns>
    public static float Yaw(TileDirection facing)
    {
        (int dx, int dz) = TileDirections.Delta(facing);
        return MathF.Atan2(dx, -dz);
    }

    /// <summary>
    /// The CONTINUOUS yaw, the same convention and the same north as <see cref="Yaw(TileDirection)"/>: the way a
    /// body standing at <paramref name="fromTilePlanar"/> looks to see <paramref name="toTilePlanar"/>. Both points
    /// are in tile units on the lattice (x, z), which is what <see cref="PoseAt(Vector2, float, TileDirection)"/>
    /// takes and what <see cref="ITileTargets.TryGetAimPoint"/> answers.
    /// <para>It agrees with the direction overload on all eight steps, exactly:
    /// <c>Yaw(d) == Yaw(origin, origin + Delta(d))</c>, whole-tile deltas being the same pair of floats both calls
    /// hand atan2. So a one-tile body beside a one-tile target draws precisely the cardinal the reach rules face it
    /// along, and only a footprint bigger than one tile moves the drawn yaw off it. The two can never disagree
    /// about which way north is, because the delta is taken in the same world space through the same z negation.</para>
    /// <para>Two coincident points have no direction to report and answer 0, which is tile south.
    /// <see cref="Pose(in TileMoveState, Vector2, float)"/> keeps the tile facing in that case rather than passing
    /// the zero on.</para>
    /// </summary>
    /// <param name="fromTilePlanar">Where the looker stands, in tile units.</param>
    /// <param name="toTilePlanar">What it looks at, in tile units.</param>
    /// <returns>Rotation about +Y in radians, in the range (-pi, pi].</returns>
    public static float Yaw(Vector2 fromTilePlanar, Vector2 toTilePlanar) =>
        MathF.Atan2(toTilePlanar.X - fromTilePlanar.X, fromTilePlanar.Y - toTilePlanar.Y);
}
