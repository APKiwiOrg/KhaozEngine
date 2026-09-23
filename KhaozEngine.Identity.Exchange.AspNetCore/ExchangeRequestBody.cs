using System;
using System.Buffers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>How reading a request body ended.</summary>
internal enum BodyReadStatus
{
    Read,
    TooLarge,
    Unreadable,
}

/// <summary>
/// One request body, read whole into a pooled buffer no longer than the cap, and parsed separately so the caller can
/// decide what runs between the two. The buffer holds a bearer credential, so it is zeroed when it goes back to the
/// pool.
/// </summary>
internal sealed class ExchangeRequestBody : IDisposable
{
    private byte[]? buffer;
    private int length;

    private ExchangeRequestBody(BodyReadStatus status, byte[]? buffer = null)
    {
        Status = status;
        this.buffer = buffer;
    }

    public BodyReadStatus Status { get; }

    /// <summary>
    /// Reads the body, refusing more than <paramref name="cap"/> bytes. A declared length over the cap is refused before
    /// a byte is read. The server's own per-request limit is lowered to the cap too, so it stops draining an oversized
    /// body after the answer rather than reading it to its own, larger, limit.
    /// </summary>
    public static async Task<ExchangeRequestBody> ReadAsync(HttpRequest request, long cap, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit
            && (limit.MaxRequestBodySize is null || limit.MaxRequestBodySize > cap))
            limit.MaxRequestBodySize = cap;
        if (request.ContentLength > cap) return new ExchangeRequestBody(BodyReadStatus.TooLarge);

        byte[] rented = ArrayPool<byte>.Shared.Rent(checked((int)cap + 1));
        var body = new ExchangeRequestBody(BodyReadStatus.Read, rented);
        try
        {
            while (true)
            {
                int read = await request.Body.ReadAsync(rented.AsMemory(body.length), ct).ConfigureAwait(false);
                if (read == 0) return body;
                body.length += read;
                if (body.length > cap)
                {
                    body.Dispose();
                    return new ExchangeRequestBody(BodyReadStatus.TooLarge);
                }
            }
        }
        catch (BadHttpRequestException ex)
        {
            // The server refused the body itself: over its limit (413), or a framing error such as a bad chunk.
            body.Dispose();
            return new ExchangeRequestBody(ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? BodyReadStatus.TooLarge
                : BodyReadStatus.Unreadable);
        }
        catch
        {
            body.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The request, or <c>null</c> when the body is not a JSON object of the request's shape. Missing members arrive as
    /// <c>null</c> and the exchange answers them as malformed. Parsing uses the fixed wire options, never the host's.
    /// </summary>
    public AuthExchangeRequest? Parse()
    {
        if (Status != BodyReadStatus.Read || buffer is null) return null;
        try
        {
            return JsonSerializer.Deserialize<AuthExchangeRequest>(buffer.AsSpan(0, length), ExchangeHttpResults.Json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // A string that is not valid UTF-8 surfaces from the reader as this rather than as a JsonException.
            return null;
        }
    }

    public void Dispose()
    {
        byte[]? rented = buffer;
        buffer = null;
        length = 0;
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
    }
}
