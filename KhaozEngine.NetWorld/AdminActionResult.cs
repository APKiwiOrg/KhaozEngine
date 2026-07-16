using System;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The outcome kind of a game-registered admin action, mapped to an HTTP status by the admin endpoint: a query that
/// returns data (<see cref="Ok"/>), a mutation that was enqueued for the host thread (<see cref="Accepted"/>), or a
/// rejected request whose <see cref="AdminActionResult.Error"/> explains why (<see cref="BadRequest"/>).
/// </summary>
public enum AdminActionStatus
{
    /// <summary>The action completed and may carry a JSON payload (HTTP 200).</summary>
    Ok,

    /// <summary>The action enqueued work for the host thread and returns no body (HTTP 202).</summary>
    Accepted,

    /// <summary>The request was malformed or invalid, see <see cref="AdminActionResult.Error"/> (HTTP 400).</summary>
    BadRequest,
}

/// <summary>
/// The result of a game-registered admin action. Build one through the static factories rather than a constructor:
/// <see cref="Ok"/> for a query (optionally carrying a payload the endpoint serializes as JSON), <see cref="Accepted"/>
/// for a mutation the handler enqueued to the host thread, or <see cref="BadRequest"/> to reject the request with a
/// message.
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

    /// <summary>The value serialized as the JSON response body when <see cref="Status"/> is <see cref="AdminActionStatus.Ok"/>, otherwise null.</summary>
    public object? Payload { get; }

    /// <summary>The rejection message when <see cref="Status"/> is <see cref="AdminActionStatus.BadRequest"/>, otherwise null.</summary>
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
}
