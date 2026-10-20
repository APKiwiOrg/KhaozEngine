using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.BugReports.Relay;

internal static class GitHubAppJwt
{
    public static string Create(long appId, RSA key, DateTimeOffset now)
    {
        var header = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(header))
        {
            writer.WriteStartObject();
            writer.WriteString("alg", "RS256");
            writer.WriteString("typ", "JWT");
            writer.WriteEndObject();
        }

        var claims = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(claims))
        {
            long seconds = now.ToUnixTimeSeconds();
            writer.WriteStartObject();
            writer.WriteNumber("iat", seconds - 60);
            writer.WriteNumber("exp", seconds + 540);
            writer.WriteString("iss", appId.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        string unsigned = Base64Url.EncodeToString(header.WrittenSpan) + "." + Base64Url.EncodeToString(claims.WrittenSpan);
        byte[] signature = key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url.EncodeToString(signature);
    }
}
