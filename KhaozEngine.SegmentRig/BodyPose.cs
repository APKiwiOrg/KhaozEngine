using System.Numerics;

namespace KhaozEngine.SegmentRig;

/// <summary>Where one body is drawn this frame and which way it faces: the two numbers every rig in this
/// package composes a transform from, and the only thing it ever asks a game for.</summary>
/// <param name="Position">Where the body's FEET are, world metres. The rigs are authored about a sole on
/// y 0, so this is the point the whole skeleton hangs off rather than a centre of mass.</param>
/// <param name="Yaw">Which way the body faces, RADIANS about the world up axis. Zero faces engine +z, and
/// the frame is right handed with +x to that body's own LEFT. See <see cref="BodyRig"/> for why the sides
/// fall out that way, because it is the half that inverts silently.</param>
/// <remarks>
/// Deliberately not a snapshot of anything: a game hands over the position it is DRAWING the body at, which
/// is the presented or interpolated pose rather than the committed authoritative one, so a body glides
/// between server answers instead of stepping once per update. Whether that position came from a fixed-tick
/// world, a continuous prediction or a replay is not this package's business.
/// <para>A record struct with two fields and no behaviour, so a caller that already holds a position and a
/// heading builds one per frame for free and nothing here allocates.</para>
/// </remarks>
public readonly record struct BodyPose(Vector3 Position, float Yaw)
{
    /// <summary>A body at the world origin facing engine +z. What a test builds when the placement is not
    /// the thing under test.</summary>
    public static BodyPose Origin => default;
}
