using System;
using System.Buffers.Binary;
using System.IO;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Contracts;

/// <summary>Each <c>not_a_zip</c> branch of the directory scan, on a one record stored zip with no comment.</summary>
public sealed class ZipDirectoryScanTests
{
    const int EndRecordSize = 22;

    static byte[] Zip() => TestBundles.CentralRecords(records: 1, declared: 1).ToArray();

    static string? Scan(byte[] zip) => ZipDirectoryScan.Check(new MemoryStream(zip), zip.Length, BugReportLimits.MaxEntries);

    static int End(byte[] zip) => zip.Length - EndRecordSize;

    [Fact]
    public void Plain_directory_passes() => Assert.Null(Scan(Zip()));

    [Theory]
    [InlineData(4, 2)]
    [InlineData(6, 2)]
    [InlineData(8, 2)]
    [InlineData(10, 2)]
    [InlineData(12, 4)]
    [InlineData(16, 4)]
    public void Zip64_sentinels_are_not_a_zip(int field, int width)
    {
        byte[] zip = Zip();
        zip.AsSpan(End(zip) + field, width).Fill(0xFF);

        Assert.Equal(BundleErrors.NotAZip, Scan(zip));
    }

    [Fact]
    public void Zip64_locator_before_the_end_record_is_not_a_zip()
    {
        byte[] zip = Zip();
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(End(zip) - 20), 0x07064b50);

        Assert.Equal(BundleErrors.NotAZip, Scan(zip));
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(6, 1)]
    [InlineData(8, 2)]
    public void Disk_or_count_mismatch_is_not_a_zip(int field, int value)
    {
        byte[] zip = Zip();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(End(zip) + field), (ushort)value);

        Assert.Equal(BundleErrors.NotAZip, Scan(zip));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Directory_that_does_not_end_at_the_end_record_is_not_a_zip(int sizeChange)
    {
        byte[] zip = Zip();
        Span<byte> size = zip.AsSpan(End(zip) + 12, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(BinaryPrimitives.ReadUInt32LittleEndian(size) + sizeChange));

        Assert.Equal(BundleErrors.NotAZip, Scan(zip));
    }

    [Fact]
    public void Bad_central_signature_is_not_a_zip()
    {
        byte[] zip = Zip();
        int directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(End(zip) + 16));
        zip[directory] ^= 0xFF;

        Assert.Equal(BundleErrors.NotAZip, Scan(zip));
    }
}
