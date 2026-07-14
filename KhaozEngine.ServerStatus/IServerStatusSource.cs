using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// Transport seam for fetching one <see cref="ServerStatusReport"/> from the out-of-band status endpoint.
/// The default <see cref="HttpServerStatusSource"/> speaks HTTPS against a configured URL; tests inject a
/// fake so the poller runs headless with no sockets. Returning null means "no answer this time" (transport
/// error, timeout, oversized or malformed body) - the implementation MUST never throw, so the poller can
/// degrade to a stale/unknown snapshot uniformly.
/// </summary>
public interface IServerStatusSource
{
    /// <summary>Fetches the current report, or null when unreachable/unparseable. Never throws.</summary>
    Task<ServerStatusReport?> FetchAsync(CancellationToken cancellationToken = default);
}
