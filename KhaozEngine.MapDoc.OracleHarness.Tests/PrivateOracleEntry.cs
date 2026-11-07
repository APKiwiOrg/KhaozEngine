using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal static class PrivateOracleEntry
{
    internal static void Run(Action<PrivateOracleInputs, PrivateOracleReport> body)
        => Execute(PrivateOracleInputs.FromProcess(), body);

    internal static void Run(IReadOnlyDictionary<string, string?> environment,
        Action<PrivateOracleInputs, PrivateOracleReport> body)
        => Execute(PrivateOracleInputs.Require(environment), body);

    static void Execute(PrivateOracleInputs inputs, Action<PrivateOracleInputs, PrivateOracleReport> body)
    {
        using PrivateOracleReport report = PrivateOracleReport.Open(inputs.ReportPath);
        try { body(inputs, report); }
        catch (Exception error)
        {
            string digest;
            try
            {
                report.Record("exception", JsonSerializer.Serialize(new
                {
                    type = error.GetType().FullName,
                    message = error.Message,
                    stack = error.StackTrace,
                }));
                report.Dispose();
                digest = report.Sha256;
            }
            catch (Exception) { throw new PrivateOracleFailure(PrivateOracleFailureRefusal.SecureReportUnavailable, PrivateOracleReport.SecureFailure); }
            throw new PrivateOracleFailure(PrivateOracleFailureRefusal.ExecutionFailed, $"private oracle: exception failure, report sha256 {digest}")
            { ReportSha256 = digest };
        }
        report.ThrowIfAnyFailed();
    }
}
