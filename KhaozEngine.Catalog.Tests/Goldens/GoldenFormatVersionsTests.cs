using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Goldens;

/// <summary>
/// The cross-version round trips of spec 15.3, which are the tests that only pay off later and cannot be
/// added retroactively.
/// <para>
/// <b>The enumeration is the mechanism.</b> Every version directory under <c>Goldens/</c> must be readable by
/// the CURRENT reader, whatever version that reader is at, so adding a format version means adding a
/// directory and forgetting to keep reading the old one goes red immediately. The current format versions
/// are checked to HAVE a directory, so the day <c>ChunkFormatVersion</c> becomes 2 this class is what says
/// the v2 set is missing.
/// </para>
/// <para>
/// <b>Plan choice, Appendix B item 11.</b> Spec 15.3's forward direction, a v2 reader handed a v1 chunk,
/// cannot be written while only v1 exists. The direction that CAN be written is the backward one, and it is
/// here: a reader handed a record whose format version is 2 refuses the WHOLE record with its own
/// format-version token rather than making a best-effort partial read (contracts 15), and the same holds for
/// a manifest whose format generation is above the engine's own (contracts 7.4). The forward direction
/// becomes real through the enumeration above, on its own, the day the version moves.
/// </para>
/// </summary>
public sealed class GoldenFormatVersionsTests
{
    /// <summary>The offset every one of the four formats puts its <c>ushort</c> format version at.</summary>
    const int FormatVersionOffset = 4;

    /// <summary>The offset a <c>KECM</c> puts its <c>uint</c> format generation at.</summary>
    const int ManifestGenerationOffset = 12;

    /// <summary>One case per golden of every version directory present.</summary>
    public static TheoryData<string, string> Cases() => GoldenLibrary.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryGoldenOfEveryVersion_IsReadableByTheCurrentReader(string version, string name)
    {
        GoldenFile golden = GoldenLibrary.Get(version, name);
        GoldenRoundTrip trip = GoldenCodecs.RoundTrip(golden.Stored, SideOf(golden));
        Assert.True(
            trip.Decoded,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The current reader refused {golden} with '{trip.Reason}'. A checked-in version set is read forever, so a format change adds a directory rather than editing one."));
    }

    [Fact]
    public void EveryCurrentFormatVersion_HasAGoldenDirectory()
    {
        var missing = new List<string>();
        foreach (ushort formatVersion in new[]
        {
            ContentPackFormat.ChunkFormatVersion,
            ContentPackFormat.ManifestFormatVersion,
            ContentPackFormat.RuleChunkFormatVersion,
            ContentPackFormat.TextChunkFormatVersion,
        })
        {
            string directory = "v" + formatVersion.ToString(CultureInfo.InvariantCulture);
            if (!HasVersion(directory))
            {
                missing.Add(directory);
            }
        }

        Assert.True(
            missing.Count == 0,
            "A format version has no golden set. Add the directory and generate its files through the shipped encoders, and leave every older set exactly as it is: "
                + string.Join(", ", missing));
    }

    [Fact]
    public void AChunkFromTheNextFormatVersion_IsRefusedWhole()
    {
        byte[] file = WithFormatVersion("chunk-tag-0.kecc", 2);
        Assert.False(ContentChunkCodec.TryDecode(file, registry: null, out ContentChunk? chunk, out string? reason));
        Assert.Null(chunk);
        Assert.Equal(ContentChunkCodec.ReasonFormatVersion, reason);

        // The header read refuses on its own, so nothing downstream ever sees a field of a layout it does
        // not know, and the refusal costs no allocation.
        Assert.False(ContentChunkCodec.TryReadHeader(file, out _, out reason));
        Assert.Equal(ContentChunkCodec.ReasonFormatVersion, reason);
    }

    [Fact]
    public void AManifestFromTheNextFormatVersion_IsRefusedWhole()
    {
        byte[] file = WithFormatVersion("manifest-server.kecm", 2);
        Assert.False(ContentManifestCodec.TryDecode(file, ContentManifestSide.Server, out ContentManifest? manifest, out string? reason));
        Assert.Null(manifest);
        Assert.Equal(ContentManifestCodec.ReasonFormatVersion, reason);
    }

    [Fact]
    public void ARuleChunkFromTheNextFormatVersion_IsRefusedWhole()
    {
        byte[] file = WithFormatVersion("rules.kecr", 2);
        Assert.False(ContentRuleChunkCodec.TryDecode(file, out RemapRuleSet? rules, out string? reason));
        Assert.Null(rules);
        Assert.Equal(ContentRuleChunkCodec.ReasonFormatVersion, reason);
    }

    [Fact]
    public void ATextChunkFromTheNextFormatVersion_IsRefusedWhole()
    {
        byte[] file = WithFormatVersion("text-en-us.kect", 2);
        Assert.False(ContentTextChunkCodec.TryDecode(file, out ContentTextChunk? chunk, out string? reason));
        Assert.Null(chunk);
        Assert.Equal(ContentTextChunkCodec.ReasonFormatVersion, reason);
    }

    [Theory]
    [InlineData("manifest-server.kecm", ContentManifestSide.Server)]
    [InlineData("manifest-client.kecm", ContentManifestSide.Client)]
    public void AManifestAboveTheEngineFormatGeneration_IsRefusedOnBothSides(string name, ContentManifestSide side)
    {
        byte[] file = (byte[])GoldenLibrary.Get("v1", name).Stored.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(ManifestGenerationOffset),
            (uint)ContentPackFormat.Generation + 1);

        Assert.False(ContentManifestCodec.TryDecode(file, side, out ContentManifest? manifest, out string? reason));
        Assert.Null(manifest);
        Assert.Equal(ContentManifestCodec.ReasonFormatGeneration, reason);
    }

    [Fact]
    public void AManifestAtTheEngineFormatGeneration_IsAccepted()
    {
        // The refusal is on ABOVE and never on equal, and a generation BELOW this build's is an older pack
        // this build still reads. The golden was baked at generation 1 and is that case from 19.14.0 on,
        // which is the half of the rule a consumer's already published versions depend on.
        GoldenFile golden = GoldenLibrary.Get("v1", "manifest-server.kecm");
        Assert.True(
            ContentManifestCodec.TryDecode(golden.Stored, ContentManifestSide.Server, out ContentManifest? manifest, out string? reason),
            reason);
        Assert.True(manifest.FormatGeneration <= ContentPackFormat.Generation);
        Assert.Equal(1u, manifest.FormatGeneration);
    }

    static bool HasVersion(string directory)
    {
        foreach (string version in GoldenLibrary.Versions)
        {
            if (string.Equals(version, directory, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    static byte[] WithFormatVersion(string name, ushort formatVersion)
    {
        byte[] file = (byte[])GoldenLibrary.Get("v1", name).Stored.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(FormatVersionOffset), formatVersion);
        return file;
    }

    static ContentManifestSide SideOf(GoldenFile golden)
        => golden.Kind == GoldenCodecs.ManifestKind
            && golden.Values.GetProperty("manifest").GetProperty("side").GetString() == nameof(ContentManifestSide.Client)
            ? ContentManifestSide.Client
            : ContentManifestSide.Server;
}
