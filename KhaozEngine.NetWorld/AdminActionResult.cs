using System;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The outcome kind of a game-registered admin action, mapped to an HTTP status by the admin endpoint: a query that
/// returns data (<see cref="Ok"/>), a mutation that was enqueued for the host thread (<see cref="Accepted"/>), a
/// rejected request whose <see cref="AdminActionResult.Error"/> or <see cref="AdminActionResult.Payload"/> explains
/// why (<see cref="BadRequest"/>), or a request refused because the state it named has moved
/// (<see cref="Conflict"/>).
/// </summary>
public enum AdminActionStatus
{
    /// <summary>The action completed and may carry a JSON payload (HTTP 200).</summary>
    Ok,

    /// <summary>The action enqueued work for the host thread and returns no body (HTTP 202).</summary>
    Accepted,

    /// <summary>The request was malformed or invalid, see <see cref="AdminActionResult.Error"/> (HTTP 400).</summary>
    BadRequest,

    /// <summary>
    /// The request named a state the store has moved past, and the payload says what it found instead (HTTP 409).
    /// Optimistic concurrency is what needs it: a caller that publishes against a base version another caller has
    /// already taken has to be told BOTH numbers, and telling it so is neither a malformed request nor a server
    /// fault.
    /// </summary>
    Conflict,
}

/// <summary>
/// The result of a game-registered admin action. Build one through the static factories rather than a constructor:
/// <see cref="Ok"/> for a query (optionally carrying a payload the endpoint serializes as JSON), <see cref="Accepted"/>
/// for a mutation the handler enqueued to the host thread, <see cref="BadRequest(string)"/> to reject the request with
/// a message, <see cref="BadRequest(object)"/> to reject it with a whole DOCUMENT, or <see cref="Conflict"/> when the
/// state the request named has moved.
/// <para>
/// <b>A rejection carries EITHER a message or a document, never both.</b> The string overload is the one every
/// existing caller uses and its body shape is fixed at <c>{ "error": "..." }</c>, so the document overload is an
/// addition beside it rather than a reshaping of it. A handler with more than one thing to say (a finding list, the
/// two version numbers of a stale publish) hands over a document and the endpoint returns it as the body.
/// </para>
/// </summary>
public readonly struct AdminActionResult
{
    private AdminActionResult(AdminActionStatus status, object? payload, string? error)
    {
        Status = status;
        Payload = payload;
        Error = error;
    }

    /// <summary>The outcome kind, mapped to an HTTP status by the admin endpoint.</summary>
    public AdminActionStatus Status { get; }

    /// <summary>
    /// The value serialized as the JSON response body: the query result under <see cref="AdminActionStatus.Ok"/>, the
    /// rejection document under a <see cref="BadRequest(object)"/>, the conflict document under
    /// <see cref="AdminActionStatus.Conflict"/>, and null otherwise.
    /// </summary>
    public object? Payload { get; }

    /// <summary>
    /// The rejection message when the result came from <see cref="BadRequest(string)"/>, otherwise null. A rejection
    /// built from <see cref="BadRequest(object)"/> carries its reason on <see cref="Payload"/> instead.
    /// </summary>
    public string? Error { get; }

    /// <summary>A successful query. Pass <paramref name="payload"/> to return a JSON body, or omit it for an empty 200.</summary>
    public static AdminActionResult Ok(object? payload = null) => new(AdminActionStatus.Ok, payload, null);

    /// <summary>A mutation the handler enqueued to the host thread. The endpoint answers 202 with no body.</summary>
    public static AdminActionResult Accepted() => new(AdminActionStatus.Accepted, null, null);

    /// <summary>A rejected request. <paramref name="error"/> is returned to the caller as the reason (400).</summary>
    public static AdminActionResult BadRequest(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(AdminActionStatus.BadRequest, null, error);
    }

    /// <summary>
    /// A rejected request whose reason is a DOCUMENT. <paramref name="payload"/> is returned as the whole JSON body
    /// (400), which is what a handler with a finding LIST needs: a validator accumulates every finding rather than
    /// stopping at the first, and flattening those into one sentence is what makes an operator re-run the call to
    /// see the next one.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public static AdminActionResult BadRequest(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new(AdminActionStatus.BadRequest, payload, null);
    }

    /// <summary>
    /// A request refused because the state it named has moved. <paramref name="payload"/> is returned as the whole
    /// JSON body (409) and says what the store found instead, so an optimistic caller is told both numbers rather
    /// than told no.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public static AdminActionResult Conflict(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new(AdminActionStatus.Conflict, payload, null);
    }
}
