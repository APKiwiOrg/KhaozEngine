using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.Identity.Exchange.AspNetCore;

/// <summary>A small HTTP/1.1 client for facts that must observe a response while an upload is being refused.</summary>
internal static class ExchangeWireClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public static async Task<ExchangeReply> PostAsync(Uri address, byte[] body, bool chunked,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(body);

        byte[] request = Request(address, body, chunked);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, address.Port, timeout.Token).ConfigureAwait(false);
        await using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);

        using var response = new MemoryStream();
        await stream.CopyToAsync(response, timeout.Token).ConfigureAwait(false);
        return Parse(response.ToArray());
    }

    private static byte[] Request(Uri address, byte[] body, bool chunked)
    {
        string framing = chunked
            ? "Transfer-Encoding: chunked\r\n"
            : $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n";
        byte[] head = Encoding.ASCII.GetBytes(
            $"POST /auth/exchange HTTP/1.1\r\nHost: {address.Authority}\r\nContent-Type: application/json\r\n{framing}Connection: close\r\n\r\n");
        byte[] framedBody = chunked ? Chunk(body) : body;
        byte[] request = new byte[head.Length + framedBody.Length];
        head.CopyTo(request, 0);
        framedBody.CopyTo(request, head.Length);
        return request;
    }

    private static byte[] Chunk(byte[] body)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"{body.Length:X}\r\n");
        byte[] suffix = "\r\n0\r\n\r\n"u8.ToArray();
        byte[] chunked = new byte[prefix.Length + body.Length + suffix.Length];
        prefix.CopyTo(chunked, 0);
        body.CopyTo(chunked, prefix.Length);
        suffix.CopyTo(chunked, prefix.Length + body.Length);
        return chunked;
    }

    private static ExchangeReply Parse(byte[] response)
    {
        ReadOnlySpan<byte> separator = "\r\n\r\n"u8;
        int headerEnd = response.AsSpan().IndexOf(separator);
        if (headerEnd < 0) throw new InvalidDataException("The HTTP response has no header terminator.");

        string[] lines = Encoding.ASCII.GetString(response, 0, headerEnd).Split("\r\n", StringSplitOptions.None);
        string[] status = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (status.Length < 2 || !int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            throw new InvalidDataException("The HTTP response has no numeric status.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) throw new InvalidDataException("The HTTP response has a malformed header.");
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        byte[] body = response.AsSpan(headerEnd + separator.Length).ToArray();
        if (headers.TryGetValue("Transfer-Encoding", out string? transfer)
            && transfer.Split(',').Any(value => value.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase)))
            body = DecodeChunks(body);
        else if (headers.TryGetValue("Content-Length", out string? lengthText))
        {
            if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out int length)
                || length < 0 || body.Length != length)
                throw new InvalidDataException("The HTTP response body does not match Content-Length.");
        }

        bool noStore = headers.TryGetValue("Cache-Control", out string? cacheControl)
            && cacheControl.Split(',').Any(value => value.Trim().Equals("no-store", StringComparison.OrdinalIgnoreCase));
        return new ExchangeReply((HttpStatusCode)code, body, noStore, null, headers.Keys.ToArray());
    }

    private static byte[] DecodeChunks(byte[] encoded)
    {
        var decoded = new ArrayBufferWriter<byte>();
        int offset = 0;
        while (true)
        {
            int lineEnd = encoded.AsSpan(offset).IndexOf("\r\n"u8);
            if (lineEnd < 0) throw new InvalidDataException("The chunked response has no size terminator.");
            string sizeText = Encoding.ASCII.GetString(encoded, offset, lineEnd).Split(';', 2)[0];
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0)
                throw new InvalidDataException("The chunked response has an invalid size.");
            offset += lineEnd + 2;
            if (size == 0) return decoded.WrittenSpan.ToArray();
            if (encoded.Length - offset < size + 2 || !encoded.AsSpan(offset + size, 2).SequenceEqual("\r\n"u8))
                throw new InvalidDataException("The chunked response ended inside a chunk.");
            decoded.Write(encoded.AsSpan(offset, size));
            offset += size + 2;
        }
    }
}
