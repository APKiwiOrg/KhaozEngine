using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

internal static class FormatFourFixtures
{
    // Frozen from the one-time released-resolver recording. Do not regenerate to hide a compatibility change.
    internal const string PinnedFixtureDigest = "d71a03bee5142f1f4e8d4b9bca34a147d145d4fd9e74ea0c46de0b6d9567c93a";
    internal static readonly MapResolveOptions Options = new("headless", 1, "options");

    static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "FormatFour");
    internal static string MonolithicPath => Path.Combine(DirectoryPath, "native-monolithic.mapdoc.json");
    internal static string TiledDirectory => Path.Combine(DirectoryPath, "native-tiled");

    internal static string CopyTiledToTemp()
    {
        string root = Path.Combine(Path.GetTempPath(), "mapdoc-format-four-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            foreach (string path in Directory.EnumerateFiles(TiledDirectory, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(root, Path.GetRelativePath(TiledDirectory, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }
            return root;
        }
        catch
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
        }
    }

    internal static IReadOnlyList<string> TileFileDigests(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "tiles"), "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(entry => entry.Relative, StringComparer.Ordinal)
            .Select(entry => entry.Relative + "\t" + AssertFixtures.Sha256(entry.Path)).ToArray();

    internal static (MapDocument Document, MapAssetClosure Assets, MapResolveOptions Options) Build()
    {
        var assets = NativeAssetFixtures.Valid();
        MapDocument document = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
        document.Id = "r2-format-four";
        document.ResolverIdentity = new(1, 1);
        document.NativeAssets = assets.Roots.ToList();
        document.NumericIdHighWaterMark = 13;
        MapPlacement first = document.Placements.Single(p => p.Id == "old-inn");
        first.Id = "a";
        first.AssetId = "tree";
        first.NumericId = 11;
        first.Y = null;
        document.Placements.Add(new MapPlacement
        {
            Id = "b",
            Kind = "scenery",
            AssetId = "tree",
            NumericId = 12,
            X = 70,
            Z = -70,
            Y = 2.5f,
            Tags = new() { "first", "second" },
        });
        document.Placements.Add(new MapPlacement
        {
            Id = "c",
            Kind = "scenery",
            AssetId = "tree",
            NumericId = 13,
            X = 10,
            Z = 10,
            Y = null,
        });
        return (document, MapAssetClosure.Load(assets.Roots, assets.Source), Options);
    }

    internal static MapAssetClosure Assets()
    {
        var assets = NativeAssetFixtures.Valid();
        return MapAssetClosure.Load(assets.Roots, assets.Source);
    }

    internal static float SupportHeight(float x, float z) => x * 0.25f + z * 0.5f;

    internal static MapResolvedDocument ResolveRecording(MapDocument document,
        out IReadOnlyList<(float X, float Z)> calls)
    {
        var recorded = new List<(float X, float Z)>();
        MapResolvedDocument resolved = MapResolver.Resolve(document, Assets(), (x, z) =>
        {
            recorded.Add((x, z));
            return SupportHeight(x, z);
        }, Options);
        calls = recorded.AsReadOnly();
        return resolved;
    }

    internal static ResolverExpectations LoadExpectations() =>
        JsonSerializer.Deserialize<ResolverExpectations>(
            File.ReadAllText(Path.Combine(DirectoryPath, "resolver-v1-expectations.json")))
        ?? throw new InvalidDataException("Format-4 resolver expectations are null.");

    internal static void AssertMatchesExpectations(MapResolvedDocument resolved,
        IReadOnlyList<(float X, float Z)> calls)
    {
        ResolverExpectations expected = LoadExpectations();
        Assert.Equal(expected.Placements.Count, resolved.Placements.Count);
        for (int i = 0; i < expected.Placements.Count; i++)
        {
            ExpectedPlacement e = expected.Placements[i];
            MapResolvedPlacement actual = resolved.Placements[i];
            Assert.Equal((e.Id, e.Kind, e.AssetId, e.NumericId),
                (actual.PlacementId, actual.Kind, actual.AssetId, actual.NumericId));
            Assert.Equal((e.XBits, e.YBits, e.ZBits, e.YawBits, e.ScaleBits),
                (BitConverter.SingleToUInt32Bits(actual.Transform.Position.X),
                 BitConverter.SingleToUInt32Bits(actual.Transform.Position.Y),
                 BitConverter.SingleToUInt32Bits(actual.Transform.Position.Z),
                 BitConverter.SingleToUInt32Bits(actual.Transform.YawRadians),
                 BitConverter.SingleToUInt32Bits(actual.Transform.Scale)));
            Assert.Equal(e.Tags.ToArray(), actual.Tags.ToArray());
        }
        Assert.Equal(expected.Calls.Count, calls.Count);
        for (int i = 0; i < expected.Calls.Count; i++)
            Assert.Equal((expected.Calls[i].XBits, expected.Calls[i].ZBits),
                (BitConverter.SingleToUInt32Bits(calls[i].X), BitConverter.SingleToUInt32Bits(calls[i].Z)));
    }

    internal static string ComputeFixtureDigest() => ComputeFixtureDigest(DirectoryPath);

    internal static string ComputeFixtureDigest(string directory)
    {
        var entries = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(directory, path).Replace('\\', '/')))
            .OrderBy(entry => entry.Relative, StringComparer.Ordinal);
        var manifest = new StringBuilder();
        foreach (var entry in entries)
            manifest.Append(entry.Relative).Append('\t').Append(AssertFixtures.Sha256(entry.Path)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
    }
}

internal sealed record ResolverExpectations(string AuthoredHashFormatFour,
    IReadOnlyList<ExpectedPlacement> Placements, IReadOnlyList<ExpectedCall> Calls);

internal sealed record ExpectedPlacement(string Id, string Kind, string AssetId, long? NumericId,
    uint XBits, uint YBits, uint ZBits, uint YawBits, uint ScaleBits, IReadOnlyList<string> Tags);

internal sealed record ExpectedCall(uint XBits, uint ZBits);
