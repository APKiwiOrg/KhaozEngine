using System;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>
/// The bounds and the one disclosure switch of a mapped exchange endpoint. Every bound is carried by the endpoint
/// itself, so it holds whatever middleware the host runs. <see cref="AuthExchangeEndpoints.MapAuthExchange"/> validates
/// them once, when the endpoint is mapped.
/// </summary>
public sealed class AuthExchangeEndpointOptions
{
    /// <summary>The largest <see cref="MaxRequestBodyBytes"/> accepted. A request is one short credential.</summary>
    public const long MaxBodyCapBytes = 1024 * 1024;

    /// <summary>The route. Both games serve <c>/auth/exchange</c>.</summary>
    public string Pattern { get; init; } = "/auth/exchange";

    /// <summary>
    /// The largest request body the endpoint reads, in bytes. A longer body answers 413 with no body, before any JSON
    /// parsing. Applied as endpoint metadata (routing sets the server's per-request limit from it) and enforced again
    /// by the handler's own bounded read. Positive, at most <see cref="MaxBodyCapBytes"/>.
    /// </summary>
    public long MaxRequestBodyBytes { get; init; } = 8 * 1024;

    /// <summary>
    /// How many exchanges one client may start in a fixed one minute window, with no queue: past it the answer is 429
    /// at once, with <c>Retry-After</c>. A real client exchanges rarely (it reuses its session token until it expires),
    /// so five covers a retry, a re-exchange after a stale token, and a couple of people behind one address.
    /// </summary>
    public int PermitsPerClientPerMinute { get; init; } = 5;

    /// <summary>
    /// How many leading bits of an IPv6 caller's address name one client for the per-client window, 1 to 128. See
    /// <see cref="AuthExchangeClientKey"/>. IPv4 callers are always keyed on the whole address.
    /// </summary>
    public int Ipv6PartitionPrefixLength { get; init; } = AuthExchangeClientKey.DefaultIpv6PrefixLength;

    /// <summary>
    /// How many exchanges run at once across every client. The per-client window does not stop a distributed flood,
    /// where every source stays under its own limit and the pile of simultaneous provider calls and store round trips
    /// is the damage. Scoped to this endpoint only, so a health probe beside it is never starved. Positive.
    /// </summary>
    public int MaxConcurrentExchanges { get; init; } = 20;

    /// <summary>
    /// How many exchanges wait, oldest first, for a place under <see cref="MaxConcurrentExchanges"/>. Past it the answer
    /// is 429 at once. Zero or more.
    /// </summary>
    public int MaxQueuedExchanges { get; init; } = 20;

    /// <summary>
    /// Whether a banned caller is told the ban's reason and expiry. Off by default, so a reason written for operators
    /// does not reach a player unless the game decides it should.
    /// </summary>
    public bool IncludeBanDetails { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Pattern) || !Pattern.StartsWith('/'))
            throw new ArgumentException("The exchange route must be a path starting with '/'.", nameof(Pattern));
        if (MaxRequestBodyBytes <= 0 || MaxRequestBodyBytes > MaxBodyCapBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxRequestBodyBytes),
                $"The body cap must be between 1 and {MaxBodyCapBytes} bytes.");
        if (PermitsPerClientPerMinute <= 0)
            throw new ArgumentOutOfRangeException(nameof(PermitsPerClientPerMinute), "The per-client window must admit at least one exchange.");
        if (Ipv6PartitionPrefixLength is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(Ipv6PartitionPrefixLength), "The IPv6 prefix length must be between 1 and 128.");
        if (MaxConcurrentExchanges <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentExchanges), "The global bound must admit at least one exchange.");
        if (MaxQueuedExchanges < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedExchanges), "The queue length cannot be negative.");
    }
}
