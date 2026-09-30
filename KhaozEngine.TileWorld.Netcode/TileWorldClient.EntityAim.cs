using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Nominates a presentation aim point for an entity on the target timeline selected by the client.</summary>
/// <param name="target">The target entity net id.</param>
/// <param name="footprint">The target footprint from the selected newest or delayed capture.</param>
/// <param name="plane">The plane of that footprint.</param>
/// <param name="tilePlanar">The nominated point in tile units, in the convention of
/// <see cref="ITileTargets.TryGetAimPoint"/>.</param>
/// <returns>True to nominate a finite point, false to use the footprint centre.</returns>
public delegate bool TileEntityAimPointResolver(long target, TileRect footprint, int plane, out Vector2 tilePlanar);

public sealed partial class TileWorldClient
{
    /// <summary>
    /// Optional entity aim nomination for presented poses. Null, a declined nomination or a nonfinite point uses
    /// the footprint centre. Unknown targets keep their existing facing without calling this resolver.
    /// <para>The client selects the target footprint first. Local attackers use the newest remote capture, remote
    /// attackers use the delayed capture, and a target matching <see cref="LocalNetId"/> always uses newest local
    /// prediction. The callback needs no timeline reconstruction.</para>
    /// <para>Presentation only. Movement, reach and encoded state are unchanged. Authored objects continue to use
    /// the constructor's <see cref="ITileTargets.TryGetAimPoint"/> override. A gliding body keeps its physical
    /// step direction until displayed landing.</para>
    /// </summary>
    public TileEntityAimPointResolver? EntityAimPointResolver { get; set; }

    bool TryResolveEntityAim(long target, bool delayed, out Vector2 aim)
    {
        aim = default;
        ITileTargets targets = delayed && target != LocalNetId ? delayedTargets : entityTargets;
        if (!targets.TryGetFootprint(target, out TileRect footprint, out int plane)) return false;
        if (EntityAimPointResolver is { } nominate && nominate(target, footprint, plane, out Vector2 point)
            && float.IsFinite(point.X) && float.IsFinite(point.Y))
        {
            aim = point;
            return true;
        }

        aim = new Vector2(footprint.X + (footprint.Width - 1) * .5f,
            footprint.Z + (footprint.Height - 1) * .5f);
        return true;
    }
}
