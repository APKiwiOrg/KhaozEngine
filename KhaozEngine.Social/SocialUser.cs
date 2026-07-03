namespace KhaozEngine.Social;

/// <summary>
/// A platform user identity. <see cref="Username"/> is the login/handle (e.g. the Discord username);
/// <see cref="GlobalName"/> is the display name where the platform distinguishes the two.
/// </summary>
public readonly record struct SocialUser(string Id, string Username, string? GlobalName);
