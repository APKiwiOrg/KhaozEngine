using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// How much damage an entity has left in it. The engine owns this MECHANICALLY and owns none of its meaning: it
/// subtracts a game-rolled amount, raises the death event when the number reaches zero, and replicates it so a
/// health bar has something to read. What <see cref="Max"/> should BE is the game's, written from whatever skill
/// core it keeps, and a heal is a game-side write.
/// <para>Replicated on the default channels, four payload bytes, so it survives a cell handoff and a cell capture
/// and reaches every viewer holding the entity in interest.</para>
/// </summary>
public struct TileHealth : IComponent
{
    /// <summary>Damage left before death, never above <see cref="Max"/>. Zero is dead, and death is evaluated ONCE
    /// per tick after every application, which is what lets two lethal blows on one tick kill both parties.</summary>
    public ushort Current;

    /// <summary>The ceiling <see cref="Current"/> is restored to and drawn against. Written by the game.</summary>
    public ushort Max;
}
