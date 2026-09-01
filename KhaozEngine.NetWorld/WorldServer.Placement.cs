using System.Numerics;

namespace KhaozEngine.NetWorld;

// The continuous position lever. Teleport is the other one, and the pair is documented on IAdminControllable: the
// two queue the identical placement and differ only in whether the teleport epoch advances, which is the client's
// signal to cut. Held in its own partial so both heads carry the identical seam and the frame-loop files stay
// inside the file-size ratchet. See ShardedWorldServer.Placement.cs for the multi-cell twin.
public sealed partial class WorldServer
{
    /// <inheritdoc/>
    public void SetPosition(PlayerRef target, Vector3 position) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.SetPosition, Target = target, Position = position });
}
