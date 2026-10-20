using System;
using System.Collections.Generic;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Reads HTTP headers independent of the host dictionary's comparer.</summary>
internal sealed class RequestHeaderReader
{
    readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

    public RequestHeaderReader(IReadOnlyDictionary<string, string> headers)
    {
        foreach ((string name, string value) in headers)
            this.headers[name] = value;
    }

    public string? Get(string name) => headers.GetValueOrDefault(name);

    public string? BearerToken()
    {
        string? authorization = Get("Authorization");
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        string token = authorization["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
