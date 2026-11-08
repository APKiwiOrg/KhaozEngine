using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

public sealed partial class PlayerMoveReadScope
{
    MovementColdPlacement coldPlacement;
    uint coldEpoch;
    int coldAnchorX, coldAnchorZ;
    // Raw bits, because a different raw position can round to the same physics-frame position.
    int coldX, coldY, coldZ;

    internal void RememberColdPlacement(in MovementColdPlacement certificate, in PlayerMoveState basis)
    {
        coldPlacement = certificate;
        coldEpoch = basis.TeleportEpoch;
        coldAnchorX = BitConverter.SingleToInt32Bits(basis.FrameAnchor.X);
        coldAnchorZ = BitConverter.SingleToInt32Bits(basis.FrameAnchor.Y);
        coldX = BitConverter.SingleToInt32Bits(basis.Move.Position.X);
        coldY = BitConverter.SingleToInt32Bits(basis.Move.Position.Y);
        coldZ = BitConverter.SingleToInt32Bits(basis.Move.Position.Z);
    }

    internal MovementColdPlacement TakeColdPlacement(in PlayerMoveState basis)
    {
        var certificate = basis.TeleportEpoch == coldEpoch &&
            BitConverter.SingleToInt32Bits(basis.FrameAnchor.X) == coldAnchorX &&
            BitConverter.SingleToInt32Bits(basis.FrameAnchor.Y) == coldAnchorZ &&
            BitConverter.SingleToInt32Bits(basis.Move.Position.X) == coldX &&
            BitConverter.SingleToInt32Bits(basis.Move.Position.Y) == coldY &&
            BitConverter.SingleToInt32Bits(basis.Move.Position.Z) == coldZ ? coldPlacement : default;
        // The first attempted Step consumes the certificate even when its basis or command differs.
        coldPlacement = default;
        return certificate;
    }
}
