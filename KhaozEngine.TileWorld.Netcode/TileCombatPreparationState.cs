using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Transient attempt state owned by the server. The default is idle with no allocated identity.</summary>
internal struct TileCombatPreparationState : IComponent
{
    public bool HasActive;
    public TileCombatPreparation Active;
    public ulong LastAttackId;
    public long ReadyNotBeforeTick;
    public uint AttackerTeleportEpoch;
    public uint TargetTeleportEpoch;
    /// <summary>The tick of the active attempt's most recent deferral for legal reach, or zero when never deferred.</summary>
    public long DeferredTick;
}
