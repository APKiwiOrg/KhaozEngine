using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Interactive;
using Xunit;

namespace KhaozEngine.Tests.Identity;

/// <summary>
/// Exercises the real <see cref="HttpLoopbackListener"/> over an actual loopback socket. The sign-in providers'
/// own tests substitute a fake <c>ILoopbackListener</c>, so nothing else drives the real socket-backed request
/// parsing, the caller-supplied completion page, or the mid-wait cancellation path. Every wait here is bounded by
/// a timeout so a mis-behaving listener surfaces as a test failure, never a hang.
///
/// The two budgets below are deliberately separate. Sharing one deadline made the hang guard race the request it
/// was waiting on: the guard cancels WaitForRedirectAsync, which tears down the request in flight with it, so on
/// a loaded host it could fire while a slow but perfectly healthy request was still being served (#720). The
/// guard now sits far enough out that only a genuinely stuck listener reaches it, and the request budget is the
/// one that decides red, quickly.
/// </summary>
public sealed class HttpLoopbackListenerTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    // RedirectUri is assigned by the constructor, after the socket is bound and accepting, so a URL only exists
    // once connections are being queued: the client here cannot connect ahead of the bind.
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
    public async Task A_connection_that_sends_nothing_does_not_block_the_redirect()
    {
        using var listener = new HttpLoopbackListener(0);
        using var cts = new CancellationTokenSource(HangGuard);

        Task<LoopbackResult> wait = listener.WaitForRedirectAsync(cts.Token);

        // A browser pre-connect opens a socket and may never send on it. Connections are served off the accept
        // loop, so this one must not hold the real redirect behind its own read budget.
        using TcpClient silent = new();
        await silent.ConnectAsync(IPAddress.Loopback, listener.RedirectUri.Port).WaitAsync(RequestTimeout);

        using HttpClient http = new() { Timeout = RequestTimeout };
        using HttpResponseMessage response = await http.GetAsync(Redirect(listener, "?code=after-the-silent-one"));

        LoopbackResult result = await wait.WaitAsync(HangGuard);

        Assert.Equal("after-the-silent-one", result.Query["code"]);
    }

    [Fact]
    public void A_fixed_port_binds_that_port_and_refuses_a_second_listener_on_it()
    {
        // The fixed-port path is what a provider with a pre-registered redirect URI needs, so it has to bind the
        // port it was handed. A port already taken must be refused rather than silently shared: two listeners
        // splitting one port's connections is the failure #720 was.
        using var holder = new HttpLoopbackListener(0);
        int taken = holder.RedirectUri.Port;

        Assert.ThrowsAny<SocketException>(() =>
        {
            HttpLoopbackListener second = new(taken);
            second.Dispose();
        });
    }

    [Fact]
    public async Task Cancellation_mid_wait_faults_rather_than_hanging()
    {
        using var listener = new HttpLoopbackListener(0);
        using var cts = new CancellationTokenSource();

        Task<LoopbackResult> wait = listener.WaitForRedirectAsync(cts.Token);
        cts.Cancel();

        // Bound the observation so a cancel that fails to unblock the accept surfaces as an assertion failure
        // (the WhenAny loses to Task.Delay), never a hung test.
        Task finished = await Task.WhenAny(wait, Task.Delay(RequestTimeout));
        Assert.Same(wait, finished);

        // The contract is that the wait faults rather than returning a spurious LoopbackResult. Cancelling a
        // pending accept surfaces as OperationCanceledException (TaskCanceledException derives from it).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
    }

    [Fact]
    public async Task Oidc_namespace_listener_forwards_to_the_base_implementation()
    {
        using var listener = new KhaozEngine.Identity.Oidc.HttpLoopbackListener(0);
        using var cts = new CancellationTokenSource(HangGuard);
        Task<LoopbackResult> wait = listener.WaitForRedirectAsync(cts.Token);

        using HttpClient http = new() { Timeout = RequestTimeout };
        using HttpResponseMessage response = await http.GetAsync(
            new Uri(listener.RedirectUri.ToString() + "?code=compat"));

        LoopbackResult result = await wait.WaitAsync(HangGuard);
        Assert.Equal("compat", result.Query["code"]);
    }

    [Fact]
    public void Browser_launchers_exist_in_base_and_compatibility_namespaces()
    {
        IBrowserLauncher baseLauncher = new SystemBrowserLauncher();
        IBrowserLauncher compatibilityLauncher = new KhaozEngine.Identity.Oidc.SystemBrowserLauncher();

        Assert.Equal("KhaozEngine.Identity", baseLauncher.GetType().Assembly.GetName().Name);
        Assert.Equal("KhaozEngine.Identity.Oidc", compatibilityLauncher.GetType().Assembly.GetName().Name);
    }
}
