using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.Sfx;

/// <summary>
/// Real ElevenLabs client. Hits the text-to-sound-effects REST endpoint directly (not the auditioning MCP) so
/// the bake can run unattended and control output fidelity. POST https://api.elevenlabs.io/v1/sound-generation
/// with the <c>output_format</c> query param and an <c>xi-api-key</c> header.
/// </summary>
public sealed class ElevenLabsSfxClient : IElevenLabsSfxClient
{
    const string Endpoint = "https://api.elevenlabs.io/v1/sound-generation";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    readonly string? _apiKey;

    /// <summary>Creates a client. <paramref name="apiKey"/> comes from the ELEVENLABS_API_KEY env var.</summary>
    public ElevenLabsSfxClient(string? apiKey) => _apiKey = apiKey;

    /// <inheritdoc/>
    public byte[] Generate(SfxGenRequest request)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("ELEVENLABS_API_KEY is not set.");

        var body = new Dictionary<string, object?>
        {
            ["text"] = request.Prompt,
            ["model_id"] = request.Model,
        };
        if (request.DurationSeconds is { } d) body["duration_seconds"] = d;
        if (request.PromptInfluence is { } p) body["prompt_influence"] = p;

        string url = $"{Endpoint}?output_format={Uri.EscapeDataString(request.OutputFormat)}";
        using var msg = new HttpRequestMessage(HttpMethod.Post, url);
        msg.Headers.Add("xi-api-key", _apiKey);
        msg.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using HttpResponseMessage resp = Http.Send(msg);
        if (!resp.IsSuccessStatusCode)
        {
            string err = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
            throw new InvalidOperationException($"ElevenLabs API {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        using var ms = new MemoryStream();
        resp.Content.ReadAsStream().CopyTo(ms);
        return ms.ToArray();
    }
}
