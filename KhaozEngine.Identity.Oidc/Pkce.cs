using System;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Identity.Oidc;

/// <summary>RFC 7636 PKCE verifier/challenge pair for the auth-code flow: a 32-byte random verifier (base64url),
/// and its S256 challenge (base64url(SHA256(ASCII(verifier)))).</summary>
internal static class Pkce
{
    public static (string verifier, string challenge) Create()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        string verifier = B64(bytes);
        using SHA256 sha = SHA256.Create();
        string challenge = B64(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
