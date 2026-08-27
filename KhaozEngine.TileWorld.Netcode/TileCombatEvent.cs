namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// One resolved swing, as presentation needs it. EXPLICIT rather than derived, and the temptation to derive it is
/// worth closing off: the serve is a full snapshot every tick, so a client CAN diff health between two samples, and
/// the diff is wrong twice. Two hits on one tick collapse into one number, and a MISS moves health by zero and is
/// therefore invisible. A fight rendered from health deltas shows fewer, larger, later hitsplats than the fight the
/// server ran.
/// </summary>
/// <param name="AttackerNetId">Who swung.</param>
/// <param name="TargetNetId">Who was swung at.</param>
/// <param name="Amount">Health subtracted, 0 on a miss.</param>
/// <param name="Kind">The game's own hit classification, which the engine never inspects.</param>
/// <param name="Landed">Whether the swing connected.</param>
/// <param name="Killed">Whether this blow is the one that took the target to zero. A death rides the blow that
/// caused it, so a client never has to notice an entity's absence to know it died.</param>
public readonly record struct TileCombatEvent(
    long AttackerNetId,
    long TargetNetId,
    ushort Amount,
    byte Kind,
    bool Landed,
    bool Killed);
