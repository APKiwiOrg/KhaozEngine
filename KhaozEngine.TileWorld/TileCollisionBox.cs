namespace KhaozEngine.TileWorld;

/// <summary>
/// One authored collision box of an object archetype, listed in <see cref="TileObjectArchetype.CollisionBoxes"/>: a
/// bench's seat, its back, one of the boxes a barrel or a well is built from. When an archetype lists boxes, tile
/// world physics builds one collider per box in place of the box over the whole footprint, so a body bumps into the
/// seat and the back, stands on the seat, and stands on nothing the prop does not have.
/// <para>The box is authored in the MESH's own local metres, the frame <see cref="TileWalkSurface"/> uses: x east, z
/// south (world +z) and y up, before the instance rotation and the archetype's yaw offset turn it. Y is measured from
/// the mesh base, which is the placed standing height <see cref="TileObjectPlacement.AnchorPosition"/> answers, so a
/// seat box with <see cref="MaxY"/> 0.45 is a seat top 0.45 m above where the bench stands. A box may reach past the
/// footprint, the way an awning or a cart's shafts do.</para>
/// <para><see cref="YawDegrees"/> turns the box about its own centre, clockwise seen from above, the same sense as
/// <see cref="TileObjectArchetype.YawOffsetDegrees"/> and the quarter turns, so two boxes at 0 and 45 degrees make an
/// octagon for a round prop and one box turned 45 degrees is a diagonal member. The box's centre turns with the object
/// (the instance rotation and the archetype's yaw offset) and its axes by the whole turn, this yaw included. A whole
/// turn that is a whole number of quarter turns places the box axis-aligned, any other places it oriented, and each
/// of the two turns that is a whole number of quarter turns is placed with no trigonometry.</para>
/// <para>Every bound is explicit and the catalog loader judges them all: a non-finite value, or a minimum that is
/// not below its maximum on any axis (an inverted or empty box), refuses the load, and so does a list on an archetype
/// whose collision kind builds no object collider. The placement and slope rules are in the
/// <c>KhaozEngine.TileWorld.Physics</c> README.</para>
/// </summary>
public sealed class TileCollisionBox
{
    /// <summary>West face in mesh-local metres.</summary>
    public float MinX { get; set; }
    /// <summary>East face in mesh-local metres.</summary>
    public float MaxX { get; set; }
    /// <summary>Bottom in metres above the mesh base. Defaults to 0, a box resting on the base. A box raised off the
    /// base keeps the space under it open.</summary>
    public float MinY { get; set; }
    /// <summary>Top in metres above the mesh base, the height a body standing on the box stands at.</summary>
    public float MaxY { get; set; }
    /// <summary>Low z face in mesh-local metres, the north side at rotation 0.</summary>
    public float MinZ { get; set; }
    /// <summary>High z face in mesh-local metres, the south side at rotation 0.</summary>
    public float MaxZ { get; set; }
    /// <summary>Degrees the box turns about its own centre, clockwise seen from above. Defaults to 0.</summary>
    public float YawDegrees { get; set; }
}
