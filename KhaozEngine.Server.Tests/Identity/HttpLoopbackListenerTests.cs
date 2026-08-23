using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Oidc;
using Xunit;

namespace KhaozEngine.Tests.Identity;

/// <summary>
/// Exercises the real <see cref="HttpLoopbackListener"/> over an actual loopback socket. The sign-in providers'
/// own tests substitute a fake <c>ILoopbackListener</c>, so nothing else drives the real HttpListener-backed
/// query parsing, the caller-supplied completion page, or the mid-wait cancellation path. Every wait here is
/// bounded by a timeout so a mis-behaving listener surfaces as a test failure, never a hang.
///
/// The two budgets below are deliberately separate. Sharing one deadline made the hang guard race the request it
/// was waiting on: the guard cancels WaitForRedirectAsync, which STOPS the listener, so on a loaded host it could
/// fire while a slow but perfectly healthy request was still in flight and tear the listener down under it (#720).
/// The guard now sits far enough out that only a genuinely stuck listener reaches it, and the request budget is
/// the one that decides red, quickly.
/// </summary>
public sealed class HttpLoopbackListenerTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    // RedirectUri is assigned by the constructor, after HttpListener.Start() has returned, so a URL only exists
    // once the listener is bound and queueing requests: the client here cannot connect ahead of the bind.
    private static Uri Redirect(HttpLoopbackListener listener, string query)
        => new(listener.RedirectUri.ToString() + query);

    [Fact]
    public async Task Parses_query_from_a_real_loopback_redirect()
    {
        using var listener = new HttpLoopbackListener(0);
        using var cts = new CancellationTokenSource(HangGuard);

        Task<LoopbackResult> wait = listener.WaitForRedirectAsync(cts.Token);

        using HttpClient http = new() { Timeout = RequestTimeout };
        using HttpResponseMessage response = await http.GetAsync(Redirect(listener, "?code=the-code&state=the-state"));

        LoopbackResult result = await wait.WaitAsync(HangGuard);

        Assert.Equal("the-code", result.Query["code"]);
        Assert.Equal("the-state", result.Query["state"]);
        Assert.Equal(listener.RedirectUri.ToString(), result.RedirectUri);
        Assert.Contains("Sign-in complete", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Serves_a_caller_supplied_completion_page()
    {
        const string custom = "<html><body>Fertig. Sie koennen dieses Fenster schliessen.</body></html>";
        using var listener = new HttpLoopbackListener(0, custom);
        using var cts = new CancellationTokenSource(HangGuard);

        Task<LoopbackResult> wait = listener.WaitForRedirectAsync(cts.Token);

        using HttpClient http = new() { Timeout = RequestTimeout };
        using HttpResponseMessage response = await http.GetAsync(Redirect(listener, "?code=c&state=s"));

        await wait.WaitAsync(HangGuard);

        Assert.Equal(custom, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cancellation_mid_wait_faults_rather_than_hanging()
    {
        using var listener = new HttpLoopbackListener(0);
        using var cts = new CancellationTokenSource();

        Task<LoopbackResult> wait = listener.WaitForRedirectAsync(cts.Token);
        cts.Cancel();

        // Bound the observation so a cancel that fails to unblock GetContextAsync surfaces as an assertion
        // failure (the WhenAny loses to Task.Delay), never a hung test.
        Task finished = await Task.WhenAny(wait, Task.Delay(RequestTimeout));
        Assert.Same(wait, finished);

        // Cancelling a pending HttpListener.GetContextAsync surfaces as a framework exception (HttpListenerException
        // or ObjectDisposedException, platform-dependent). The contract pinned here is that it faults rather than
        // returning a spurious LoopbackResult, so the exact type is left open on purpose.
        await Assert.ThrowsAnyAsync<Exception>(async () => await wait);
    }
}
