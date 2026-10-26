using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

// No formatter, original format, arbitrary scope ToString, header or body is ever read.
internal sealed class AdminTlsProbeLogging : ILoggerProvider, ISupportExternalScope
{
    private readonly AdminTlsProbe probe;

    public AdminTlsProbeLogging(AdminTlsProbe probe) => this.probe = probe;

    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

    internal static bool Accepts(string? category) => category is not null &&
        (category.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal) ||
         category.StartsWith("Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets", StringComparison.Ordinal) ||
         category == "Microsoft.Hosting.Lifetime" || category == "Microsoft.Extensions.Hosting.Internal.Host");

    public ILogger CreateLogger(string categoryName) => new SafeLogger(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
    public void Dispose() => probe.Buffer.Record("logging.provider.dispose");

    private sealed class SafeLogger(AdminTlsProbeLogging owner, string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug && logLevel < LogLevel.None && Accepts(category);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            owner.scopes.ForEachScope((scope, target) => CopySafeFields(scope, target), fields);
            CopySafeFields(state, fields);
            if (fields.TryGetValue("ConnectionId", out string? connection)) owner.probe.AddEndpoints(connection, fields);
            owner.probe.Buffer.Record("log." + (eventId.Name ?? "unnamed"), category, eventId.Id, fields, exception);
        }

        private static void CopySafeFields(object? state, Dictionary<string, string> target)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> pairs) return;
            int visited = 0;
            foreach (var pair in pairs)
            {
                if (++visited > 16) break;
                if ((pair.Key is "ConnectionId" or "TraceIdentifier" or "RequestId" or "Protocol") && pair.Value is string value)
                    target[pair.Key] = value;
            }
        }
    }
}
