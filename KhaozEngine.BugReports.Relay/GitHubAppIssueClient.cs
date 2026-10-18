using System;
using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Http;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Creates GitHub issues and comments using a cached GitHub App installation token.</summary>
/// <remarks>The caller owns the HTTP client. Only token exchanges are retried.</remarks>
public sealed class GitHubAppIssueClient : IGitHubIssueClient, IDisposable
{
    readonly HttpClient http;
    readonly GitHubAppCredentials credentials;
    readonly string issuesPath;
    readonly TimeProvider time;
    readonly RSA key;
    readonly InstallationTokenCache tokens;
    bool disposed;

    /// <summary>Creates a client for the configured GitHub App and repository.</summary>
    /// <param name="http">The caller-owned HTTP client.</param>
    /// <param name="credentials">The app id, installation id and RSA private key.</param>
    /// <param name="owner">The repository owner's login.</param>
    /// <param name="repository">The repository name.</param>
    /// <param name="time">The clock used for JWTs, token expiry and rate limits.</param>
    /// <exception cref="ArgumentException">The private key cannot be imported as RSA.</exception>
    public GitHubAppIssueClient(HttpClient http, GitHubAppCredentials credentials, string owner, string repository, TimeProvider time)
        : this(http, credentials, owner, repository, time, HttpRetryPolicy.Default) { }

    internal GitHubAppIssueClient(HttpClient http, GitHubAppCredentials credentials, string owner, string repository,
        TimeProvider time, HttpRetryPolicy tokenPolicy)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(tokenPolicy);
        this.http = http;
        this.credentials = credentials;
        this.time = time;
        issuesPath = "repos/" + Uri.EscapeDataString(owner) + "/" + Uri.EscapeDataString(repository) + "/issues";
        key = RSA.Create();
        try
        {
            key.ImportFromPem(credentials.PrivateKeyPem);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new ArgumentException("The GitHub App private key must be an RSA PEM.", nameof(credentials));
        }
        tokens = new InstallationTokenCache(http, credentials, key, time, tokenPolicy);
    }

    /// <summary>Creates an issue and returns its number. The POST is sent once.</summary>
    public async Task<int> CreateIssueAsync(GitHubIssueDraft issue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        using var content = JsonContent(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("title", issue.Title);
            writer.WriteString("body", issue.Body);
            writer.WriteStartArray("labels");
            foreach (string label in issue.Labels)
                writer.WriteStringValue(label);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        using HttpResponseMessage response = await PostAsync(issuesPath, content, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("number", out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int number) && number > 0)
                return number;
        }
        catch (JsonException)
        {
            // The response body can echo credentials, so never retain a parser exception.
        }
        throw GitHubResponseClassifier.InvalidResponse(response);
    }

    /// <summary>Adds one comment to an issue. The POST is sent once.</summary>
    public async Task AddCommentAsync(int issueNumber, string body, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);
        ArgumentNullException.ThrowIfNull(body);
        using var content = JsonContent(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("body", body);
            writer.WriteEndObject();
        });
        using HttpResponseMessage response = await PostAsync(issuesPath + "/" + issueNumber.ToString(CultureInfo.InvariantCulture)
            + "/comments", content, cancellationToken).ConfigureAwait(false);
    }

    async Task<HttpResponseMessage> PostAsync(string path, HttpContent content, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            string token = await tokens.GetAsync(ct).ConfigureAwait(false);
            using HttpRequestMessage request = CreateRequest(path, token);
            request.Content = content;
            HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                tokens.Invalidate();
            try
            {
                var error = GitHubResponseClassifier.Classify(response, time.GetUtcNow(), token, credentials.PrivateKeyPem);
                if (error is not null)
                    throw error;
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (HttpRequestException error)
        {
            // A transport can echo headers or credentials in both its message and its inner exception.
            throw new HttpRequestException("GitHub request failed.", null, error.StatusCode);
        }
        catch (TaskCanceledException)
        {
            throw new TaskCanceledException("GitHub request canceled.", null, ct);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("GitHub request canceled.", ct);
        }
    }

    internal static HttpRequestMessage CreateRequest(string path, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.github.com/" + path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("KhaozEngine-BugReports");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    static ByteArrayContent JsonContent(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>Releases the RSA key and token cache, leaving the caller's HTTP client open.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        tokens.Dispose();
        key.Dispose();
    }
}
