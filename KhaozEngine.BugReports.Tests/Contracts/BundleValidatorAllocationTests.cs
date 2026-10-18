using System.IO;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Contracts;

/// <summary>The validator's allocation bound, measured in a checked region. It runs in the serial allocation
/// collection so the rest of the validator tests stay parallel.</summary>
[Collection("AllocSensitive")]
public sealed class BundleValidatorAllocationTests
{
    [Fact]
    public void Too_many_central_records_are_rejected_before_parsing()
    {
        Assert.Equal("too_many_entries", BundleValidator.Validate(TestBundles.CentralRecords(records: 3, declared: 5000)).Error);
        Assert.Equal("too_many_entries", BundleValidator.Validate(TestBundles.CentralRecords(records: 5000, declared: 5)).Error);

        MemoryStream crafted = TestBundles.CentralRecords(records: 5000, declared: 5000);
        BundleValidator.Validate(TestBundles.CentralRecords(records: 5000, declared: 5000));
        BundleValidationResult? result = null;
        long allocated = AllocationMeasurement.Measure(nameof(Too_many_central_records_are_rejected_before_parsing),
            () => result = BundleValidator.Validate(crafted));
        Assert.Equal("too_many_entries", result!.Error);
        // ZipArchive would build one entry object per record, about 500 bytes each.
        Assert.True(allocated < crafted.Length, $"allocated {allocated} bytes for a {crafted.Length} byte archive");
    }
}
