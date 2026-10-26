using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace KhaozEngine.Server.Admin;

// Private archive instrumentation. The public constructor never attaches observers or logging providers.
internal sealed record AdminHttpServerDiagnosticHooks(
    Action<ILoggingBuilder> Logging,
    Action<ListenOptions> Listener,
    Action<HttpsConnectionAdapterOptions> Https,
    Action<WebApplication> Application);
