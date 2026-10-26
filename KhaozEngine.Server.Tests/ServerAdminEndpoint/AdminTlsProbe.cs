using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Security;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Server.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

internal sealed class AdminTlsProbe
{
    private readonly object connectionGate = new();
    private readonly Dictionary<string, (string? Local, string? Remote)> connections = new(StringComparer.Ordinal);
    private readonly List<CancellationTokenRegistration> lifetimes = new();
    private readonly Action<string> output;
    private readonly AdminTlsProbeLogging logging;
    private readonly AdminTlsProbeEvents? listener;
    private Exception? cleanupFailure;

    public AdminTlsProbe(string scenario, Action<string> output, bool listenToEvents = true)
    {
        Buffer = new AdminTlsDiagnosticBuffer(scenario);
        this.output = output;
        logging = new AdminTlsProbeLogging(this);
        listener = listenToEvents ? new AdminTlsProbeEvents(this) : null;
        Buffer.Record("runtime.identity", fields: new Dictionary<string, string>
        {
            ["Framework"] = RuntimeInformation.FrameworkDescription,
            ["CoreLib"] = Identity(typeof(object)),
            ["AspNetCore"] = Identity(typeof(WebApplication)),
            ["Kestrel"] = Identity(typeof(KestrelServerOptions)),
            ["NetSecurity"] = Identity(typeof(SslStream)),
        });
    }

    private static string Identity(Type type) => type.Assembly.GetName().Name + " " +
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public AdminTlsDiagnosticBuffer Buffer { get; }
    public Exception? FlushFailure { get; private set; }
    public void SetScopeProvider(IExternalScopeProvider scopes) => logging.SetScopeProvider(scopes);
    public ILogger CreateLogger(string category) => logging.CreateLogger(category);

    public AdminHttpServerDiagnosticHooks Hooks => new(
        builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFilter<AdminTlsProbeLogging>((category, level) => AdminTlsProbeLogging.Accepts(category) && level >= LogLevel.Debug);
            builder.AddProvider(logging);
            Buffer.Record("logging.attached");
        },
        listen => listen.Use(next => async connection =>
        {
            ConnectionObserved(connection.ConnectionId, connection.LocalEndPoint?.ToString(), connection.RemoteEndPoint?.ToString());
            try { await next(connection); }
            catch (Exception error)
            {
                Buffer.Record("connection.pipeline.error", fields: ConnectionFields(connection.ConnectionId), error: error);
                throw;
            }
            finally { Buffer.Record("connection.pipeline.complete", fields: ConnectionFields(connection.ConnectionId)); }
        }),
        options => Buffer.Record("https.effective-options", fields: new Dictionary<string, string>
        {
            ["HandshakeTimeoutSeconds"] = options.HandshakeTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture),
        }),
        app =>
        {
            lifetimes.Add(app.Lifetime.ApplicationStarted.Register(() => Buffer.Record("application.started", fields: BoundAddresses(app))));
            lifetimes.Add(app.Lifetime.ApplicationStopping.Register(() => Buffer.Record("application.stopping")));
            lifetimes.Add(app.Lifetime.ApplicationStopped.Register(() => Buffer.Record("application.stopped")));
            app.Use(async (context, next) =>
            {
                Dictionary<string, string> fields = ConnectionFields(context.Connection.Id);
                fields["RequestId"] = context.TraceIdentifier;
                Buffer.Record("request.server.entry", fields: fields);
                try
                {
                    await next(context);
                    fields["StatusCode"] = context.Response.StatusCode.ToString(CultureInfo.InvariantCulture);
                    Buffer.Record("request.server.complete", fields: fields);
                }
                catch (Exception error)
                {
                    Buffer.Record("request.server.error", fields: fields, error: error);
                    throw;
                }
            });
        });

    private static Dictionary<string, string> BoundAddresses(WebApplication app)
    {
        var fields = new Dictionary<string, string>();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        if (addresses is not null)
            foreach (string address in addresses.Addresses)
            {
                if (fields.Count == 4) break;
                fields["BoundEndpoint" + fields.Count.ToString(CultureInfo.InvariantCulture)] = address;
            }
        return fields;
    }

    public void ConnectionObserved(string id, string? local, string? remote)
    {
        lock (connectionGate)
        {
            if (!connections.ContainsKey(id) && connections.Count == 128) { Buffer.CapacityLoss(); return; }
            connections[id] = (local, remote);
        }
        Buffer.Record("connection.observed", fields: ConnectionFields(id));
    }

    internal void AddEndpoints(string connection, Dictionary<string, string> fields)
    {
        lock (connectionGate)
            if (connections.TryGetValue(connection, out var endpoints))
            {
                if (endpoints.Local is not null) fields["LocalEndpoint"] = endpoints.Local;
                if (endpoints.Remote is not null) fields["RemoteEndpoint"] = endpoints.Remote;
            }
    }

    private Dictionary<string, string> ConnectionFields(string connection)
    {
        var fields = new Dictionary<string, string> { ["ConnectionId"] = connection };
        AddEndpoints(connection, fields);
        return fields;
    }

    public void TlsEvent(int id, string connection, string? protocol)
    {
        lock (connectionGate) { if (!connections.ContainsKey(connection)) return; }
        string? phase = id switch { 2 => "connection.stop.eventsource", 8 => "tls.start", 9 => "tls.stop", 10 => "tls.failed", _ => null };
        if (phase is null) return;
        Dictionary<string, string> fields = ConnectionFields(connection);
        if (protocol is not null) fields["Protocol"] = protocol;
        Buffer.Record(phase, "Microsoft-AspNetCore-Server-Kestrel", id, fields);
    }

    public T Step<T>(string phase, Func<T> action)
    {
        Buffer.Record(phase + ".entry");
        try
        {
            T result = action();
            Buffer.Record(phase + ".complete");
            return result;
        }
        catch (Exception error) { Buffer.Record(phase + ".error", error: error); throw; }
    }

    public async Task StepAsync(string phase, Func<Task> action)
    {
        Buffer.Record(phase + ".entry");
        try { await action(); Buffer.Record(phase + ".complete"); }
        catch (Exception error) { Buffer.Record(phase + ".error", error: error); throw; }
    }

    public async Task<T> RequestAsync<T>(string label, Func<Task<T>> action)
    {
        Buffer.Record("request.client." + label + ".entry");
        try
        {
            T result = await action();
            Buffer.Record("request.client." + label + ".complete");
            return result;
        }
        catch (Exception error) { Buffer.Record("request.client." + label + ".error", error: error); throw; }
    }

    public async Task StartAsync(AdminHttpServer server)
    {
        await StepAsync("server.start", () => server.StartAsync());
        Buffer.Record("server.bound", fields: new Dictionary<string, string>
        {
            ["BoundEndpoint"] = "https://127.0.0.1:" + server.BoundPort.ToString(CultureInfo.InvariantCulture),
        });
    }

    public async Task CleanupAsync(string phase, Func<Task> cleanup)
    {
        try { await StepAsync(phase, cleanup); }
        catch (Exception error) { cleanupFailure ??= error; Buffer.Record("cleanup.secondary", error: error); }
    }

    public async Task RunAsync(Func<Task> body, Func<Task> cleanup)
    {
        Exception? original = null;
        try { await body(); }
        catch (Exception error) { original = error; Buffer.Record("test.error", error: error); throw; }
        finally
        {
            await CleanupAsync("fixture.cleanup", cleanup);
            foreach (CancellationTokenRegistration lifetime in lifetimes)
                await CleanupAsync("lifetime.detach", () => { lifetime.Dispose(); return Task.CompletedTask; });
            await CleanupAsync("eventsource.detach", () => { listener?.Dispose(); return Task.CompletedTask; });
            Buffer.Record("collector.detached");
            try { output("ADMIN_TLS_DIAGNOSTIC " + Buffer.Freeze().ToJson()); }
            catch (Exception error) { FlushFailure = error; }
            if (original is null && (cleanupFailure ?? FlushFailure) is { } failure)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
