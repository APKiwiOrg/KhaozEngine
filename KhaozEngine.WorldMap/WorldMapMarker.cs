using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.WorldMap;

/// <summary>A named surface marker for map icons or labels.</summary>
/// <param name="Name">The authored name from which a game can derive a localized label key.</param>
/// <param name="Position">The marker position in map metres.</param>
/// <param name="Tags">The authored tags used by icon and label rules.</param>
public sealed record WorldMapMarker(string Name, Vector2 Position, IReadOnlyList<string> Tags);
