using System.Numerics;

namespace KhaozEngine.NetWorld;

// The continuous position lever, the multi-cell twin of WorldServer.Placement.cs. Teleport is the other one, and
// the pair is documented on IAdminControllable: the two queue the identical placement and differ only in whether
// the teleport epoch advances, which is the client's signal to cut. Held in its own partial so both heads carry the
// identical seam and the frame-loop files stay inside the file-size ratchet.
public sealed partial class ShardedWorldServer
{
    /// <inheritdoc/>
    public void SetPosition(PlayerRef target, Vector3 position) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.SetPosition, Target = target, Position = position });
}
