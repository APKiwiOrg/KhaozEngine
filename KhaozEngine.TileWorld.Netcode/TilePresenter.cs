using System;
using System.Numerics;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Where to draw something, and which way it faces.</summary>
/// <param name="Position">World position in metres, on the tile CENTRE. See <see cref="TilePresenter"/> for the
/// convention and why the half tile is the presenter's to add.</param>
/// <param name="Yaw">Facing as a rotation about +Y in radians, ready for a <c>Matrix4x4.CreateRotationY(yaw)</c>
/// model transform on a +z-forward mesh: tile SOUTH is 0, east +pi/2, north pi and west -pi/2. That is the engine's
/// one model-yaw convention, the same <c>CharacterFacing.YawOf</c> produces and the same hand
/// <c>TileObjectProps.YawRadians</c> places tile objects with, so an avatar and the object it stands next to face
/// the same way. See <see cref="TilePresenter.Yaw"/>.</param>
public readonly record struct TilePose(Vector3 Position, float Yaw);

/// <summary>
/// The pure bridge from a tile state to a view, and THE ONLY PLACE in this package that consults
/// <see cref="TileWorldSpace"/>. Everything else here, the server especially, runs entirely in tile coordinates:
/// tile z counts NORTH while render z counts south, so a render-space sign that leaked into a shard boundary, a
/// reach test or a route would be an off-by-one nobody could see until a player walked into it. Keeping the
/// negation in one file is what makes that impossible rather than merely unlikely.
/// <para>A POSE NAMES THE TILE CENTRE. Tile (x, z) spans x..x+1 and z..z+1, which is the span its ground quad
/// covers and the span <c>TileObjectProps.AnchorPosition</c> centres a 1x1 prop in, so half a tile is added on
/// each axis before the world conversion. Added in TILE units, ahead of <see cref="TileWorldSpace"/>, so the half
/// tile goes through the same z negation the tile coordinate does and lands on the same side of the tile the
/// ground quad and the props do. Drawn on the CORNER instead, an avatar stands half a tile diagonally off every
/// prop it walks up to and off the middle of the ground it occupies, which is what a consumer then re-centres in
/// a shim of its own.</para>
/// <para>THE BODY GLIDES THE WHOLE STEP, LINEARLY. <see cref="Pose"/> runs from <see cref="TileMoveState.StepFrom"/>
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
    /// <summary>Builds a presenter for a world's tile size and plane height.</summary>
    /// <param name="tileSize">Metres per tile. Must be positive.</param>
    /// <param name="planeHeight">Metres between two planes. Zero is legal, and draws every plane flat.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileSize"/> is zero or negative, which would
    /// collapse the whole world onto the origin.</exception>
    public TilePresenter(float tileSize, float planeHeight)
    {
        if (tileSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "A tile is at least some metres wide.");
        TileSize = tileSize;
        PlaneHeight = planeHeight;
    }

    /// <summary>Builds a presenter from a loaded document, which is where the real numbers live. A head builds one
    /// of these the moment it has the world file, and replaces the placeholder the client started with.</summary>
    /// <param name="document">The loaded world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public TilePresenter(TileWorldDocument document)
        : this((document ?? throw new ArgumentNullException(nameof(document))).TileSize, document.PlaneHeight) { }

    /// <summary>Metres per tile.</summary>
    public float TileSize { get; }

    /// <summary>Metres between two planes. A plane INDEX times this is the height the pose draws at, which is why
    /// <see cref="TileMoveState.Vertical"/> can stay document-free.</summary>
    public float PlaneHeight { get; }

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
    /// last seen on. A state with no step in flight draws on its tile centre, whatever
    /// <paramref name="extraTicks"/> says.</para>
    /// <para>This is the BODY's answer, not the RULES'. A step commits its tile when it STARTS, so the tile the
    /// simulation has committed this player to is <see cref="TileMoveState.Tile"/> and the body drawn here is up to
    /// one step behind it. An overlay that has to show where the player IS (a true-tile marker, a minimap dot, a
    /// server-side tool) reads <c>state.Tile</c> and maps it with
    /// <see cref="PoseAt(TileCoord, TileDirection)"/>.</para>
    /// </summary>
    /// <param name="state">The state to draw.</param>
    /// <param name="extraTicks">Ticks elapsed since the state was sampled. Negative is treated as zero.</param>
    /// <returns>The world position and yaw to draw at.</returns>
    public TilePose Pose(in TileMoveState state, float extraTicks = 0f)
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
        return PoseAt(new Vector2(tileX, tileZ), state.Tile.Plane, state.Facing);
    }

    /// <summary>
    /// How far through its current step a state is, 0 at the moment the step commits and 1 as the body lands,
    /// carried forward by <paramref name="extraTicks"/> exactly as <see cref="Pose"/> carries the glide. This IS
    /// the fraction <see cref="Pose"/> interpolates on, exposed so a presentation rule that has to run in lockstep
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
    /// <para><see cref="TileWorldClient.LocalPose"/> is this call with the client's own prediction and presenter
    /// already in hand, and is what a head normally uses. This overload is for a head holding a
    /// <see cref="ClientPrediction{TState,TCommand}"/> of its own.</para>
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
    /// <param name="tilePlanar">Where the point is, in tile units on the lattice (x, z).</param>
    /// <param name="planeIndex">Which plane, as an INDEX rather than a height. Fractional is legal and is what a
    /// prediction layer's eased vertical hands in.</param>
    /// <param name="facing">The direction to face.</param>
    /// <returns>The world position and yaw to draw at.</returns>
    public TilePose PoseAt(Vector2 tilePlanar, float planeIndex, TileDirection facing) =>
        new(Centre(tilePlanar.X, planeIndex * PlaneHeight, tilePlanar.Y), Yaw(facing));

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

    // A tile point as a world position on the tile CENTRE, which is the one place the half tile is added. In TILE
    // units, before TileWorldSpace, so the z half tile is negated with the coordinate it belongs to rather than
    // being added to a world metre and landing on the wrong side of the tile. A glided position goes through the
    // same offset as a lattice one, so a body converges onto the centre it is drawn toward.
    Vector3 Centre(float tileX, float heightMetres, float tileZ) =>
        TileWorldSpace.ToWorld(tileX + 0.5f, heightMetres, tileZ + 0.5f, TileSize);

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
}
