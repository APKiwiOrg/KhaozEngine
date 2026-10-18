using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Compares service keys through equal-length SHA-256 hashes.</summary>
internal static class ServiceKeyCheck
{
    public static bool Matches(string expected, string? provided)
        => provided is not null && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(provided)));
}
