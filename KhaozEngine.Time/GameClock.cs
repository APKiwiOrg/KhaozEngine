using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Time;

/// <summary>
/// A game-agnostic clock that separates real delta time from a scaled simulation delta.
/// Set <see cref="TimeScale"/> for slow-mo/fast-forward and <see cref="Pause"/>/<see cref="Resume"/>
/// to freeze the sim while real time keeps running (UI, transitions, notifications).
/// </summary>
public sealed class GameClock
{
    /// <summary>Advance once per frame before consumers read the deltas.</summary>
    public void Update(GameTime gameTime) { }
}
