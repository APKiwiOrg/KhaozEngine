using Microsoft.Xna.Framework;

namespace KhaozEngine.Input;

/// <summary>
/// Two-finger pinch state in virtual coordinates, from <see cref="InputManager.TryGetPinch"/>.
/// </summary>
/// <param name="Active">True while two or more touches are down.</param>
/// <param name="Midpoint">Midpoint between the first two touches (virtual coords).</param>
/// <param name="Distance">Current distance between the first two touches (virtual).</param>
/// <param name="Delta">Change in <see cref="Distance"/> since last frame (virtual).</param>
/// <param name="Scale">currentDistance / previousDistance (1.0 on the first pinch frame).</param>
public readonly record struct Pinch(bool Active, Vector2 Midpoint, float Distance, float Delta, float Scale);
