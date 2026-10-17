using System.Collections.Generic;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The platform facts that cannot change while the process runs, read once at startup by
/// <see cref="ClientFacts.CaptureInvariants"/> and handed to <see cref="ClientFacts.Platform"/> in the trigger frame.
/// </summary>
/// <param name="Os">The operating system description.</param>
/// <param name="OsArchitecture">The operating system architecture.</param>
/// <param name="ProcessArchitecture">The architecture this process runs as.</param>
/// <param name="Runtime">The .NET runtime description.</param>
/// <param name="CpuCount">Logical processors available to the process.</param>
/// <param name="TotalMemoryBytes">Physical memory the runtime may use.</param>
/// <param name="Locale">The current culture name.</param>
/// <param name="Environment">The <c>KE_</c> variables, selected and masked by <see cref="ReportRedactor.SelectEnvironment"/>.</param>
internal sealed record PlatformInvariants(string Os, string OsArchitecture, string ProcessArchitecture, string Runtime,
    int CpuCount, long TotalMemoryBytes, string Locale, IReadOnlyList<TelemetryHeaderValue> Environment);
