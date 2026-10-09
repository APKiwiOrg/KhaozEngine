using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.Tests.MapDoc;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed class FormatFourRecorder(ITestOutputHelper output)
{
    [Fact]
    public void Record_FormatFourFixtures()
    {
        string directory = Environment.GetEnvironmentVariable("KHAOZ_R2_RECORD_DIR")
            ?? throw new InvalidOperationException("set KHAOZ_R2_RECORD_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("set KHAOZ_R2_RECORD_DIR");
        Assert.Equal(4, MapDocumentFile.CurrentFormatVersion);
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("refusing to overwrite recorded format-4 fixtures");

        var fixture = FormatFourFixtures.Build();
        MapResolvedDocument resolved = FormatFourFixtures.ResolveRecording(fixture.Document, out var calls);
        var expectations = new ResolverExpectations(resolved.AuthoredHash,
            resolved.Placements.Select(p => new ExpectedPlacement(p.PlacementId, p.Kind, p.AssetId, p.NumericId,
                BitConverter.SingleToUInt32Bits(p.Transform.Position.X),
                BitConverter.SingleToUInt32Bits(p.Transform.Position.Y),
                BitConverter.SingleToUInt32Bits(p.Transform.Position.Z),
                BitConverter.SingleToUInt32Bits(p.Transform.YawRadians),
                BitConverter.SingleToUInt32Bits(p.Transform.Scale), p.Tags.ToArray())).ToArray(),
            calls.Select(p => new ExpectedCall(BitConverter.SingleToUInt32Bits(p.X),
                BitConverter.SingleToUInt32Bits(p.Z))).ToArray());

        Directory.CreateDirectory(directory);
        MapDocumentFile.Save(fixture.Document, Path.Combine(directory, "native-monolithic.mapdoc.json"));
        MapDocumentFile.SaveTiled(fixture.Document, Path.Combine(directory, "native-tiled"));
        File.WriteAllText(Path.Combine(directory, "resolver-v1-expectations.json"),
            JsonSerializer.Serialize(expectations, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        output.WriteLine($"fixture digest {FormatFourFixtures.ComputeFixtureDigest(directory)}");
    }
}
