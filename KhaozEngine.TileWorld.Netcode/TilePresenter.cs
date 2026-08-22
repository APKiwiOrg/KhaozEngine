using System;
using System.Numerics;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Where to draw something, and which way it faces.</summary>
/// <param name="Position">World position in metres.</param>
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
    /// Where a state draws. <paramref name="extraTicks"/> is the fraction of a tick elapsed since the state was
    /// SAMPLED, which is what glides a remote between snapshots: the sample carries the step progress at its own
    /// instant, and the presenter carries it forward from there. Clamped at the end of the step, so a sample that
    /// went overdue (a lost snapshot, a stalled server) parks on the destination tile instead of walking off it.
    /// <para>A state with no route draws on its tile centre, whatever <paramref name="extraTicks"/> says. That is
    /// the standing case AND the observer case: a remote's route is owner-only, so a raw replicated state has none
    /// and would stand still. <see cref="TileWorldClient"/> is what rebuilds a one-step route for a remote before
    /// it gets here, and this method deliberately does not guess one, because a guessed direction draws a player
    /// walking somewhere they are not.</para>
    /// </summary>
    /// <param name="state">The state to draw.</param>
    /// <param name="extraTicks">Ticks elapsed since the state was sampled. Negative is treated as zero.</param>
    /// <returns>The world position and yaw to draw at.</returns>
    public TilePose Pose(in TileMoveState state, float extraTicks = 0f)
    {
        float tileX = state.Tile.X, tileZ = state.Tile.Z;
        if (!state.Route.IsIdle && state.StepTotal > 0)
        {
            float f = Math.Clamp((state.StepTicks + Math.Max(0f, extraTicks)) / state.StepTotal, 0f, 1f);
            TileCoord next = state.Route.Next;
            tileX += (next.X - state.Tile.X) * f;
            tileZ += (next.Z - state.Tile.Z) * f;
        }
        return new TilePose(
            TileWorldSpace.ToWorld(tileX, state.Tile.Plane * PlaneHeight, tileZ, TileSize),
            Yaw(state.Facing));
    }

    /// <summary>
    /// Where the LOCAL player draws: <see cref="ClientPrediction{TState,TCommand}.RenderedState"/>, which already
    /// carries the inter-tick easing and whatever is left of a decaying correction offset. Read from the render
    /// override rather than from the tile, because that override is the whole point of the prediction layer: it is
    /// a continuous position over a discrete lattice, and rounding it back to a tile here would throw away every
    /// frame of smoothing the layer just computed.
    /// </summary>
    /// <param name="prediction">The client's prediction for the local player.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prediction"/> is null.</exception>
    public TilePose LocalPose(ClientPrediction<TileMoveState, TileCommand> prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        TileMoveState r = prediction.RenderedState;
        Vector2 planar = r.HasRenderOverride ? r.RenderPosition : r.Position;
        float plane = r.HasRenderOverride ? r.RenderVertical : r.Vertical;
        return new TilePose(
            TileWorldSpace.ToWorld(planar.X, plane * PlaneHeight, planar.Y, TileSize),
            Yaw(r.Facing));
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
}
