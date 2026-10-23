using System;
using System.Linq;
using KhaozEngine.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Diagnostics;

[Collection("AllocSensitive")]
public sealed class ReportHomeRedactionAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void One_home_occurrence_allocates_only_the_result_string() =>
        AssertAllocation("open /Users/ann/x.log", "open ~/x.log", 48);

    [Fact]
    public void Adjacent_home_occurrences_allocate_only_the_result_string() =>
        AssertAllocation(string.Concat(Enumerable.Repeat("/Users/ann", 1000)), new string('~', 1000), 2024);

    void AssertAllocation(string text, string expected, long maximumBytes)
    {
        var redactor = new ReportRedactor("/Users/ann");
        string? result = null;
        Action redact = () => result = redactor.Redact(text);
        redact();
        Assert.Equal(expected, result);

        long allocated = AllocationMeasurement.Measure("Home redaction", redact);

        output.WriteLine($"Input length {text.Length}, output length {expected.Length}, allocated {allocated} bytes.");
        Assert.Equal(expected, result);
        Assert.InRange(allocated, 0, maximumBytes);
    }
}
