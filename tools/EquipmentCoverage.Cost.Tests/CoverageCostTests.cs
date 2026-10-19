using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.CoverageCost;

/// <summary>Historical authorization discovery remains available. Every selected CPU unit rejects before any work.</summary>
public sealed class CoverageCostTests
{
    public static IEnumerable<object[]> Authorized() => CoverageCostTheoryAttribute.Cases().Select(name => new object[] { name });

    [CoverageCostTheory]
    [MemberData(nameof(Authorized))]
    public void Measure_authorized_case(string name) => CoverageCostHarness.RejectRetiredAuthorization(name);
}

/// <summary>Skips the theory when no authorized manifest is present in the build output.</summary>
public sealed class CoverageCostTheoryAttribute : TheoryAttribute
{
    internal static string Folder => Path.Combine(AppContext.BaseDirectory, "authorized");

    internal static IEnumerable<string> Cases() => Directory.Exists(Folder)
        ? Directory.EnumerateFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)
        : [];

    public CoverageCostTheoryAttribute()
    {
        if (!Cases().Any()) Skip = "No owner-authorized coverage cost manifest is committed under authorized/.";
    }
}
