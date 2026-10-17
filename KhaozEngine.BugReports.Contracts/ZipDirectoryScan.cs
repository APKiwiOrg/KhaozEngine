using System;
using System.Buffers.Binary;
using System.IO;

namespace KhaozEngine.BugReports.Contracts;

// Reads the end of central directory record and walks the central directory headers before
// ZipArchive parses them, so a crafted record count never costs one ZipArchiveEntry per record.
// Bundles never need Zip64, multiple disks or a directory that stops short of the end record.
internal static class ZipDirectoryScan
{
    const int EndRecordSize = 22;
    const int MaxCommentLength = ushort.MaxValue;
    const int Zip64LocatorSize = 20;
    const int CentralHeaderSize = 46;
    const uint EndSignature = 0x06054b50;
    const uint Zip64LocatorSignature = 0x07064b50;
    const uint CentralSignature = 0x02014b50;

    // Null when the directory is plausible, otherwise not_a_zip or too_many_entries. Reads at most
    // the tail window plus one fixed header per record up to maxEntries + 1 records.
    public static string? Check(Stream stream, long length, int maxEntries)
    {
        if (length < EndRecordSize) return BundleErrors.NotAZip;

        int window = (int)Math.Min(length, Zip64LocatorSize + EndRecordSize + MaxCommentLength);
        long windowStart = length - window;
        byte[] tail = new byte[window];
        stream.Position = windowStart;
        stream.ReadExactly(tail);

        int lowest = Math.Max(0, window - EndRecordSize - MaxCommentLength);
        int end = -1;
        for (int i = window - EndRecordSize; i >= lowest; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndSignature)
            {
                end = i;
                break;
            }
        }
        if (end < 0) return BundleErrors.NotAZip;

        ReadOnlySpan<byte> record = tail.AsSpan(end, EndRecordSize);
        ushort disk = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        ushort directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        ushort entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
        ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);

        if (disk == ushort.MaxValue || directoryDisk == ushort.MaxValue || entriesOnDisk == ushort.MaxValue
            || totalEntries == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
            return BundleErrors.NotAZip;
        if (end >= Zip64LocatorSize
            && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end - Zip64LocatorSize)) == Zip64LocatorSignature)
            return BundleErrors.NotAZip;
        if (disk != 0 || directoryDisk != 0 || entriesOnDisk != totalEntries) return BundleErrors.NotAZip;
        if (totalEntries > maxEntries) return BundleErrors.TooManyEntries;

        // The directory must end exactly at the end record. ZipArchive reads headers until the
        // signature stops matching, so this keeps it inside the walked bytes.
        long endPosition = windowStart + end;
        if ((long)directoryOffset + directorySize != endPosition) return BundleErrors.NotAZip;

        Span<byte> header = stackalloc byte[CentralHeaderSize];
        long position = directoryOffset;
        int count = 0;
        while (position < endPosition)
        {
            if (++count > maxEntries) return BundleErrors.TooManyEntries;
            if (endPosition - position < CentralHeaderSize) return BundleErrors.NotAZip;
            stream.Position = position;
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralSignature) return BundleErrors.NotAZip;
            position += CentralHeaderSize + BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[30..]) + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
        }
        return position == endPosition && count == totalEntries ? null : BundleErrors.NotAZip;
    }
}
