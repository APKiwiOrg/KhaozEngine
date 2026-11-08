using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

public sealed partial class PlayerMoveReadScope
{
    MovementColdPlacement coldPlacement;
    uint coldEpoch;
    int coldAnchorX, coldAnchorZ;

    internal void RememberColdPlacement(in MovementColdPlacement certificate, in PlayerMoveState basis)
    {
        coldPlacement = certificate;
        coldEpoch = basis.TeleportEpoch;
        coldAnchorX = BitConverter.SingleToInt32Bits(basis.FrameAnchor.X);
        coldAnchorZ = BitConverter.SingleToInt32Bits(basis.FrameAnchor.Y);
    }

    internal MovementColdPlacement TakeColdPlacement(in PlayerMoveState basis)
    {
        var certificate = basis.TeleportEpoch == coldEpoch &&
            BitConverter.SingleToInt32Bits(basis.FrameAnchor.X) == coldAnchorX &&
            BitConverter.SingleToInt32Bits(basis.FrameAnchor.Y) == coldAnchorZ ? coldPlacement : default;
        // The first attempted Step consumes the certificate even when its basis or command differs.
        coldPlacement = default;
        return certificate;
    }
}
