namespace KhaozEngine.Identity;

/// <summary>The launch/sign-in status an <see cref="IdentitySession"/> can be in.</summary>
public enum IdentityStatus
{
    /// <summary>No usable session; the player must sign in interactively.</summary>
    RequiresSignIn,

    /// <summary>The cached session token has expired, but the last successful authentication is still within the
    /// configured offline-grace window, so play continues offline.</summary>
    OfflineGrace,

    /// <summary>A valid session token is held (or was just attached), so the player is fully signed in.</summary>
    SignedIn,
}

/// <summary><see cref="Subject"/> is the server-verified subject from the <c>/auth/exchange</c> result. It is null
/// until a session token has been attached (via <see cref="IdentitySession.AttachSessionTokenAsync"/>); the
/// provider credential alone is not a verified identity.</summary>
public readonly record struct IdentityState(
    IdentityStatus Status, string? Subject, string? DisplayName, ProviderCredential? Credential, string? SessionToken);
