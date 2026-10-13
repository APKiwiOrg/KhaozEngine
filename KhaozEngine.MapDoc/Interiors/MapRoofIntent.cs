using System.Collections.Generic;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Interiors;

/// <summary>Explicit roof presentation policy. It never changes physical geometry.</summary>
public enum MapRoofMode { Auto, AlwaysVisible, AlwaysHidden }

/// <summary>Independent intent for colour, shadow, camera boom and sky picking consumers.</summary>
public sealed record MapRoofIntent(string RoofPlacementId, bool ColourVisible, bool CastShadows,
    bool CameraBoomBlocks, bool SkyPickingBlocks);

/// <summary>Roof intent over the complete inventory, with the observer's acquired membership witness.</summary>
public sealed record MapRoofIntentResult(MapMembershipStatus Status, string? SpaceId,
    IReadOnlyList<MapRoofIntent> Roofs, MapReadWitness Witness);
