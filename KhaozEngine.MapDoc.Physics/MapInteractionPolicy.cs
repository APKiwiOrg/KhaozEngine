using System;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>The versioned rules that turn a placement's shapes into its interaction envelope. The envelope source is
/// the selection volume when present, otherwise the collider of a solid asset. An object whose source is shorter
/// than <see cref="MinimumVerticalReachHeightMetres"/> is swept straight up in world space until it is that tall.
/// Interaction bands are absolute world Y and clip the envelope after the raise. <see cref="Hash"/> enters every
/// placement geometry digest, so a policy change changes every identity built on it.</summary>
public static class MapInteractionPolicy
{
    /// <summary>The policy identifier.</summary>
    public const string PolicyId = "kemap/interaction-envelope/1";

    /// <summary>The minimum height of an interaction envelope, in metres.</summary>
    public const float MinimumVerticalReachHeightMetres = 1f;

    /// <summary>The canonical text of the policy, hashed into <see cref="Hash"/>.</summary>
    public static string CanonicalText { get; } =
        PolicyId + "\n" +
        "minimumVerticalReachHeightMetres=1\n" +
        "source=selection-else-solid-collider\n" +
        "raise=world-vertical-sweep-of-members\n" +
        "band=absolute-world-y-after-raise\n";

    /// <summary>The lowercase hex SHA-256 of the UTF-8 <see cref="CanonicalText"/>.</summary>
    public static string Hash { get; } =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalText)));
}
