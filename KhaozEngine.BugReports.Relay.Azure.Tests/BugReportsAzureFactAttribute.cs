using System;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Runs only against an explicitly named account and pre-created test resources.</summary>
public sealed class BugReportsAzureFactAttribute : FactAttribute
{
    /// <summary>The account name. An unset value leaves the live leg skipped.</summary>
    public const string EnvironmentVariable = "KE_BUGREPORTS_STORAGE";

    /// <summary>All live resource names must carry this marker.</summary>
    public const string ResourceMarker = "kebugtest";

    /// <summary>Skips unless the variable names a valid Azure Storage account.</summary>
    public BugReportsAzureFactAttribute()
    {
        string? account = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(account))
        {
            Skip = "set " + EnvironmentVariable + " to a test storage account name to run against pre-created kebugtest resources";
            return;
        }

        if (account.Length is < 3 or > 24 || Array.Exists(account.ToCharArray(), c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9')))
            Skip = EnvironmentVariable + " must be a lowercase alphanumeric storage account name";
    }
}
