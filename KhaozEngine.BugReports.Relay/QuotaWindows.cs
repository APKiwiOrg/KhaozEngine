using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.BugReports.Relay;

/// <summary>A counter key, its report limit and its exclusive UTC end.</summary>
internal readonly record struct QuotaWindow(string Key, int Limit, DateTimeOffset End);

/// <summary>The account hour, account day and global day windows shared by every relay instance.</summary>
internal static class QuotaWindows
{
    /// <summary>The first 32 lowercase hex characters of SHA-256 over the UTF-8 subject.</summary>
    public static string SubjectHash(string subject) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject)).AsSpan(0, 16));

    /// <summary>Returns the account hour, account day and global day windows, in increment order.</summary>
    public static IReadOnlyList<QuotaWindow> For(string subject, DateTimeOffset now, BugReportRelayOptions options)
    {
        now = now.ToUniversalTime();
        string hash = SubjectHash(subject);
        string hourKey = now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
        string dayKey = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        DateTimeOffset hourStart = new(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        DateTimeOffset dayStart = new(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset dayEnd = dayStart.AddDays(1);
        return new[]
        {
            new QuotaWindow($"ah-{hash}-{hourKey}", options.PerAccountPerHour, hourStart.AddHours(1)),
            new QuotaWindow($"ad-{hash}-{dayKey}", options.PerAccountPerDay, dayEnd),
            new QuotaWindow($"gd-{dayKey}", options.GlobalPerDay, dayEnd),
        };
    }
}
