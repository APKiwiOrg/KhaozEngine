using System;
using System.Numerics;

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
/// The pure bridge from a tile point to a view, and THE ONLY PLACE in this package that consults
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
/// <para>Nothing here holds state or touches a GPU, and the SMOOTHING is deliberately not here either: a body's
/// drawn point is chased toward its committed tile by a <see cref="TileChase"/>, which is stateful and lives per
/// body on <see cref="TileWorldClient"/>, and <see cref="PoseAt"/> is handed the answer. So this type is a
/// function of its arguments, a head can call it from a render thread, a test can call it with no device, and two
/// callers asking about the same point get the same answer.</para>
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
    /// THE mapping, and the one both drawn paths go through: a point in TILE units plus a plane index becomes a
    /// world position on the tile centre, and a facing becomes a model yaw.
    /// <para><paramref name="tilePlanar"/> is a <see cref="TileChase.Drawn"/> for a body being drawn, which is why
    /// it is a continuous point rather than a <see cref="TileCoord"/>: the chase sits between tiles for most of a
    /// walk. <see cref="TileWorldClient.LocalPose"/> and <see cref="TileWorldClient.TryGetRemotePose"/> are the two
    /// callers the engine ships, and a game drawing a body of its own calls this with its own chase.</para>
    /// </summary>
    /// <param name="tilePlanar">Where the body is, in tile units on the lattice (x, z).</param>
    /// <param name="planeIndex">Which plane, as an INDEX rather than a height. Fractional is legal and is what a
    /// prediction layer's eased vertical hands in.</param>
    /// <param name="facing">The direction the body faces.</param>
    /// <returns>The world position and yaw to draw at.</returns>
    public TilePose PoseAt(Vector2 tilePlanar, float planeIndex, TileDirection facing) =>
        new(Centre(tilePlanar.X, planeIndex * PlaneHeight, tilePlanar.Y), Yaw(facing));

    /// <summary>
    /// Where a state says the body IS: the centre of <see cref="TileMoveState.Tile"/>, the tile the simulation has
    /// committed it to, with nothing smoothed. This is the RULES' answer, so it is what a minimap, an editor, a
    /// server-side tool or a debug overlay wants.
    /// <para>It is NOT what an avatar draws at. A body drawn straight off this cuts a whole tile at every commit,
    /// because a step commits its tile when it STARTS. Draw a body through <see cref="TileWorldClient.LocalPose"/>
    /// or <see cref="TileWorldClient.TryGetRemotePose"/>, both of which run a <see cref="TileChase"/> onto exactly
    /// this point. Note the pair is not consulted either: <see cref="TileMoveState.StepFrom"/> is still on the
    /// state and still on the wire (the simulator and the reconcile both need it), it is simply not what the body
    /// is drawn between any more.</para>
    /// </summary>
    /// <param name="state">The state to place.</param>
    /// <returns>The committed tile's centre and the state's facing.</returns>
    public TilePose Pose(in TileMoveState state) =>
        PoseAt(new Vector2(state.Tile.X, state.Tile.Z), state.Tile.Plane, state.Facing);

    // A tile point as a world position on the tile CENTRE, which is the one place the half tile is added. In TILE
    // units, before TileWorldSpace, so the z half tile is negated with the coordinate it belongs to rather than
    // being added to a world metre and landing on the wrong side of the tile. A chased position goes through the
    // same offset as a lattice one, so a body converges onto the centre it is drawn from.
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
