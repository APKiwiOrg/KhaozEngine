using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// One inbound relay call as a host hands it over: the request headers, the body stream and the declared content length.
/// </summary>
/// <param name="Headers">The request headers by name.</param>
/// <param name="Body">The request body, read once and never seeked.</param>
/// <param name="ContentLength">The declared body length, or null when the request did not declare one.</param>
public sealed record RelayRequest(IReadOnlyDictionary<string, string> Headers, Stream Body, long? ContentLength);

/// <summary>The relay's answer, which a host writes back unchanged.</summary>
/// <param name="StatusCode">The HTTP status code.</param>
/// <param name="JsonBody">The JSON response body, or null for an empty body.</param>
/// <param name="Headers">Extra response headers, such as <c>Retry-After</c> on a 429.</param>
public sealed record RelayResponse(int StatusCode, string? JsonBody, IReadOnlyDictionary<string, string> Headers);
