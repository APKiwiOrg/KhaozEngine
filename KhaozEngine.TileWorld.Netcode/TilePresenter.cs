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
/// <para>Nothing here holds state or touches a GPU. It is a function of a <see cref="TileMoveState"/> plus a
/// fraction of a tick, so a head can call it from a render thread, a test can call it with no device, and two
/// callers asking about the same state get the same answer.</para>
/// </summary>
public sealed class TilePresenter
{
    /// <summary>Builds a presenter for a world's tile size and plane height.</summary>
    /// <param name="tileSize">Metres per tile. Must be positive.</param>
    /// <param name="planeHeight">Metres between two planes. Zero is legal, and draws every plane flat.</param>
    /// <param name="glide">How long the body may lag its committed tile. Omitted is
    /// <see cref="TileGlideWindow.WholeStep"/>, the full-step glide this package always drew. A client hands its own
    /// <see cref="TileWorldClient.Glide"/> here, see the type doc.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileSize"/> is zero or negative, which would
    /// collapse the whole world onto the origin.</exception>
    public TilePresenter(float tileSize, float planeHeight, TileGlideWindow glide = default)
    {
        if (tileSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "A tile is at least some metres wide.");
        TileSize = tileSize;
        PlaneHeight = planeHeight;
        Glide = glide;
    }

    /// <summary>Builds a presenter from a loaded document, which is where the real numbers live. A head builds one
    /// of these the moment it has the world file, and replaces the placeholder the client started with.</summary>
    /// <param name="document">The loaded world.</param>
    /// <param name="glide">How long the body may lag its committed tile, <see cref="TileWorldClient.Glide"/> on a
    /// head that has a client. Omitted is <see cref="TileGlideWindow.WholeStep"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public TilePresenter(TileWorldDocument document, TileGlideWindow glide = default)
        : this((document ?? throw new ArgumentNullException(nameof(document))).TileSize, document.PlaneHeight,
            glide) { }

    /// <summary>Metres per tile.</summary>
    public float TileSize { get; }

    /// <summary>Metres between two planes. A plane INDEX times this is the height the pose draws at, which is why
    /// <see cref="TileMoveState.Vertical"/> can stay document-free.</summary>
    public float PlaneHeight { get; }

    /// <summary>
    /// How long the drawn body may lag the tile it is committed to. <see cref="TileGlideWindow.WholeStep"/> unless a
    /// caller passed one, which draws the full-step glide this package has always drawn.
    /// <para>It lives on the PRESENTER rather than being handed to each call because that is what makes the local
    /// player and every remote agree by construction: one presenter serves <see cref="Pose"/> and
    /// <see cref="LocalPose"/> both, so there is no second copy of the number to set differently and no way for the
    /// local body to snap while the remotes slide.</para>
    /// </summary>
    public TileGlideWindow Glide { get; }

    /// <summary>
    /// Where a state draws: the glide from <see cref="TileMoveState.StepFrom"/> INTO
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
    /// <para><see cref="Glide"/> remaps the fraction on the way through, so a windowed presenter draws the body onto
    /// its committed tile within the window's seconds and holds it there for the rest of the step. On the default
    /// <see cref="TileGlideWindow.WholeStep"/> the fraction is not touched at all.</para>
    /// </summary>
    /// <param name="state">The state to draw.</param>
    /// <param name="extraTicks">Ticks elapsed since the state was sampled. Negative is treated as zero.</param>
    /// <returns>The world position and yaw to draw at.</returns>
    public TilePose Pose(in TileMoveState state, float extraTicks = 0f)
    {
        float tileX = state.Tile.X, tileZ = state.Tile.Z;
        if (state.IsStepping && state.StepTotal > 0)
        {
            float f = Math.Clamp((state.StepTicks + Math.Max(0f, extraTicks)) / state.StepTotal, 0f, 1f);
            f = TileGlideWindow.Remap(f, Glide.FractionOf(state.StepTotal));
            // In FLOAT, for the reason TileMoveState.Position differences in float: the fields are public, and two
            // hand-written coordinates a world apart would overflow an int subtraction.
            tileX = state.StepFrom.X + ((float)state.Tile.X - state.StepFrom.X) * f;
            tileZ = state.StepFrom.Z + ((float)state.Tile.Z - state.StepFrom.Z) * f;
        }
        return new TilePose(Centre(tileX, state.Tile.Plane * PlaneHeight, tileZ), Yaw(state.Facing));
    }

    /// <summary>
    /// Where the LOCAL player draws: <see cref="ClientPrediction{TState,TCommand}.RenderedState"/>, which already
    /// carries the inter-tick easing and whatever is left of a decaying correction offset. Read from the render
    /// override rather than from the tile, because that override is the whole point of the prediction layer: it is
    /// a continuous position over a discrete lattice, and rounding it back to a tile here would throw away every
    /// frame of smoothing the layer just computed.
    /// <para>On the default <see cref="TileGlideWindow.WholeStep"/> that override is drawn verbatim, which is what
    /// this method has always done. A WINDOWED presenter has to take the rendered position apart first, because the
    /// window remaps the STEP interpolation and nothing else: the decaying correction offset is lifted off, the step
    /// is re-placed at the remapped fraction, and the offset goes straight back on. Remapping the rendered position
    /// whole would put the correction through the same multiplier and swallow it, so a misprediction would cut
    /// instead of decaying and the one thing the prediction layer smooths would stop being smoothed.</para>
    /// <para>The fraction is rebuilt from the state and the layer's inter-tick phase rather than measured off the
    /// drawn point, and that is what keeps a corner continuous. Measured off the point, the fraction would be taken
    /// against whichever step is in flight NOW while the eased point is still on the one before it, so every turn
    /// would jump. Rebuilt, it runs from zero to one across the step, so the tick a step commits reads as zero on the
    /// new step, which is <see cref="TileMoveState.StepFrom"/>: exactly the tile the previous step's fraction of one
    /// had already parked the body on.</para>
    /// </summary>
    /// <param name="prediction">The client's prediction for the local player.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prediction"/> is null.</exception>
    public TilePose LocalPose(ClientPrediction<TileMoveState, TileCommand> prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        TileMoveState r = prediction.RenderedState;
        bool eased = r.HasRenderOverride;
        Vector2 planar = eased ? r.RenderPosition : r.Position;
        float plane = eased ? r.RenderVertical : r.Vertical;
        if (!Glide.CoversWholeStep(r.StepTotal)) planar = Windowed(prediction, r);
        return new TilePose(Centre(planar.X, plane * PlaneHeight, planar.Y), Yaw(r.Facing));
    }

    // The local player's step, re-placed at the window's fraction, with the correction offset put back unchanged.
    // Split out so LocalPose reads as the one-line pass-through it still is on the default window.
    //
    // The fraction is the step's OWN progress carried through the tick by the layer's inter-tick phase, which is the
    // same expression Pose builds for a remote out of StepTicks and the ticks elapsed since its sample. Read here as a
    // sub-tick clock rather than as the origin of the layer's easing, and that is deliberate: measured a tick further
    // back, as the layer's own easing is, the fraction tops out at (StepTotal - 1) / StepTotal and never reaches 1, so
    // a window inside one tick of the step's duration leaves the body short of its tile and then jumps it onto the
    // next step's StepFrom at the commit. Measured on a real predicted walk that is half a tile every step at run
    // cadence and a whole tile at a one-tick one. Measured from the commit instead, the catch-up the head draws is the
    // window itself rather than the window plus a tick, and it is the same catch-up a remote is drawn with.
    //
    // The prediction layer is not bypassed by any of this. Its correction offset rides through unchanged below, and
    // its phase is what keeps the motion smooth above the tick rate. What the window replaces is only where along
    // the step the body sits.
    Vector2 Windowed(ClientPrediction<TileMoveState, TileCommand> prediction, in TileMoveState r)
    {
        // RenderedState always carries the render override (it ends in WithRenderState unconditionally), so there is
        // no un-eased local pose to fall back to and no second phase convention to pick between.
        Vector2 offset = prediction.RenderOffset;
        if (!r.IsStepping || r.StepTotal == 0) return new Vector2(r.Tile.X, r.Tile.Z) + offset;
        float f = TileGlideWindow.Remap((r.StepTicks + prediction.InterTickFraction) / r.StepTotal,
            Glide.FractionOf(r.StepTotal));
        return new Vector2(
            r.StepFrom.X + ((float)r.Tile.X - r.StepFrom.X) * f,
            r.StepFrom.Z + ((float)r.Tile.Z - r.StepFrom.Z) * f) + offset;
    }

    // A tile point as a world position on the tile CENTRE, which is the one place the half tile is added. In TILE
    // units, before TileWorldSpace, so the z half tile is negated with the coordinate it belongs to rather than
    // being added to a world metre and landing on the wrong side of the tile. A smoothed position goes through the
    // same offset as a lattice one, so a correction decays toward the centre it was drawn from.
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
